public class IntrospectionTests
{
    [Test]
    public Task Describe() =>
        Verify(SharedProcessor.Instance.Describe());

    // begin-snippet: namedSourceTest
    [Test]
    public async Task NameOverridesSourceNameButNotModelName()
    {
        var sources = SharedProcessor.Instance.Describe().Sources;

        // The CLR type is SalesRegion; [Queryable(Name = "Region")] renames only the source, so the
        // generated model stays SalesRegionQueryModel and the server's introspection agrees with
        // what the generator emits.
        var region = sources.Single(_ => _.Name == "Region");
        await Assert.That(region.Model).IsEqualTo("SalesRegionQueryModel");
        await Assert.That(region.Kind).IsEqualTo("Entity");
        await Assert.That(sources.Select(_ => _.Name)).DoesNotContain("SalesRegion");
    }
    // end-snippet

    // A re-emitted enum carries its values, so a client resolves a combined flag to the member the
    // server meant. Perks is [Flags] with explicit powers of two; a copy numbered by position would
    // hold 3 for Remote. The spellings here are the ones the generator reads from metadata, which is
    // what makes the two stamps agree.
    [Test]
    public async Task EnumsCarryTheirValuesAndFlags()
    {
        var perks = SharedProcessor.Instance.Describe().Enums.Single(_ => _.Name == "Perks");

        string[] names = ["None", "Parking", "Gym", "Remote"];
        string[] values = ["0", "1", "2", "4"];
        using (Assert.Multiple())
        {
            await Assert.That(perks.Values).IsEquivalentTo(names, CollectionOrdering.Matching);
            await Assert.That(perks.Constants).IsEquivalentTo(values, CollectionOrdering.Matching);
            await Assert.That(perks.IsFlags).IsTrue();
            await Assert.That(perks.Underlying).IsEqualTo("int");
        }
    }

    [Test]
    public async Task QueryableViewIsClassifiedAsView()
    {
        var source = SharedProcessor.Instance.Describe().Sources.Single(_ => _.Name == "DepartmentHeadcount");
        await Assert.That(source.Kind).IsEqualTo("View");
    }

    [Test]
    public async Task KeylessQueryableIsClassifiedAsView()
    {
        // [Queryable] on an EF [Keyless] type is the documented equivalent of [QueryableView], and
        // must classify identically. The two branches live in Schema.TryClassify.
        var source = SharedProcessor.Instance.Describe().Sources.Single(_ => _.Name == "RegionSummary");
        await Assert.That(source.Kind).IsEqualTo("View");
    }

    [Test]
    public async Task UnnamedSourcesFallBackToTheTypeName() =>
        await Assert.That(SharedProcessor.Instance.Describe().Sources.Select(_ => _.Name)).Contains("Employee");

    [Test]
    public async Task ComplexTypeAppearsInTypesButNotSources()
    {
        var introspection = SharedProcessor.Instance.Describe();

        // The complex type is a traversable member type, so it is a Type (for the generated model) but
        // never a Source (no entry point).
        var types = introspection.Types;
        await Assert.That(types.Select(_ => _.Model)).Contains("AddressQueryModel");
        await Assert.That(introspection.Sources.Select(_ => _.Name)).DoesNotContain("Address");

        // Employee references it as a navigation-shaped member; [QueryIgnore] Zip stays hidden.
        var address = types.Single(_ => _.Model == "EmployeeQueryModel")
            .Members.Single(_ => _.Name == "Address");
        await Assert.That(address.IsNavigation).IsTrue();
        await Assert.That(address.TypeDisplay).IsEqualTo("AddressQueryModel?");

        var addressModel = types.Single(_ => _.Model == "AddressQueryModel");
        await Assert.That(addressModel.Members.Select(_ => _.Name)).IsEquivalentTo(["City", "Country"]);
    }

    // The stamp is deliberately not asserted here: Describe's snapshot carries it, and that it did not
    // move when these annotations were added is the check that deprecation stays out of it. Hashing it
    // would report every deployed client as stale over a note to whoever next rebuilds one.
    [Test]
    public async Task ObsoleteIsCarriedForMembersSourcesAndTypes()
    {
        var introspection = SharedProcessor.Instance.Describe();

        // An [Obsolete] with a message: the message is what the generated client's warning quotes.
        var headcount = introspection.Types.Single(_ => _.Model == "DepartmentHeadcountQueryModel")
            .Members.Single(_ => _.Name == "Headcount");
        await Assert.That(headcount.Obsolete).IsEqualTo("Counts open roles too; use the Region rollup.");

        // A bare [Obsolete] is empty rather than null — deprecated, with nothing to add. It is reported on
        // the source as well as the type, so the entry point a query starts from warns too.
        await Assert.That(introspection.Types.Single(_ => _.Model == "RegionSummaryQueryModel").Obsolete).IsEmpty();
        await Assert.That(introspection.Sources.Single(_ => _.Name == "RegionSummary").Obsolete).IsEmpty();

        // Null everywhere else: absent from the payload entirely, so nothing changes for a member
        // nobody deprecated.
        await Assert.That(introspection.Types.Single(_ => _.Model == "EmployeeQueryModel").Obsolete).IsNull();
        await Assert.That(introspection.Types.Single(_ => _.Model == "DepartmentHeadcountQueryModel")
                .Members.Single(_ => _.Name == "Department").Obsolete).IsNull();
    }

    [Test]
    public async Task GuardrailAcceptsCorrectlyAnnotatedModel()
    {
        using var context = TestContext.CreateSeeded();

        // Against the live EF model, Address is a complex type (not an entity) and the sources are real
        // entities/views — so the startup guardrail passes.
        await Assert.That(() => SharedProcessor.Instance.ValidateAgainstModel(context)).ThrowsNothing();
    }
}
