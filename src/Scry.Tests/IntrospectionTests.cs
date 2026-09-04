[TestFixture]
public class IntrospectionTests
{
    [Test]
    public Task Describe() =>
        Verify(SharedProcessor.Instance.Describe());

    // begin-snippet: namedSourceTest
    [Test]
    public void NameOverridesSourceNameButNotModelName()
    {
        var sources = SharedProcessor.Instance.Describe().Sources;

        // The CLR type is SalesRegion; [Queryable(Name = "Region")] renames only the source, so the
        // generated model stays SalesRegionQueryModel and the server's introspection agrees with
        // what the generator emits.
        var region = sources.Single(_ => _.Name == "Region");
        Assert.That(region.Model, Is.EqualTo("SalesRegionQueryModel"));
        Assert.That(region.Kind, Is.EqualTo("Entity"));
        Assert.That(sources.Select(_ => _.Name), Does.Not.Contain("SalesRegion"));
    }
    // end-snippet

    // A re-emitted enum carries its values, so a client resolves a combined flag to the member the
    // server meant. Perks is [Flags] with explicit powers of two; a copy numbered by position would
    // hold 3 for Remote. The spellings here are the ones the generator reads from metadata, which is
    // what makes the two stamps agree.
    [Test]
    public void EnumsCarryTheirValuesAndFlags()
    {
        var perks = SharedProcessor.Instance.Describe().Enums.Single(_ => _.Name == "Perks");

        string[] names = ["None", "Parking", "Gym", "Remote"];
        string[] values = ["0", "1", "2", "4"];
        Assert.Multiple(() =>
        {
            Assert.That(perks.Values, Is.EqualTo(names));
            Assert.That(perks.Constants, Is.EqualTo(values));
            Assert.That(perks.IsFlags, Is.True);
            Assert.That(perks.Underlying, Is.EqualTo("int"));
        });
    }

    [Test]
    public void QueryableViewIsClassifiedAsView()
    {
        var source = SharedProcessor.Instance.Describe().Sources.Single(_ => _.Name == "DepartmentHeadcount");
        Assert.That(source.Kind, Is.EqualTo("View"));
    }

    [Test]
    public void KeylessQueryableIsClassifiedAsView()
    {
        // [Queryable] on an EF [Keyless] type is the documented equivalent of [QueryableView], and
        // must classify identically. The two branches live in Schema.TryClassify.
        var source = SharedProcessor.Instance.Describe().Sources.Single(_ => _.Name == "RegionSummary");
        Assert.That(source.Kind, Is.EqualTo("View"));
    }

    [Test]
    public void UnnamedSourcesFallBackToTheTypeName() =>
        Assert.That(
            SharedProcessor.Instance.Describe().Sources.Select(_ => _.Name),
            Does.Contain("Employee"));

    [Test]
    public void ComplexTypeAppearsInTypesButNotSources()
    {
        var introspection = SharedProcessor.Instance.Describe();

        // The complex type is a traversable member type, so it is a Type (for the generated model) but
        // never a Source (no entry point).
        Assert.That(introspection.Types.Select(_ => _.Model), Does.Contain("AddressQueryModel"));
        Assert.That(introspection.Sources.Select(_ => _.Name), Does.Not.Contain("Address"));

        // Employee references it as a navigation-shaped member; [QueryIgnore] Zip stays hidden.
        var address = introspection.Types.Single(_ => _.Model == "EmployeeQueryModel")
            .Members.Single(_ => _.Name == "Address");
        Assert.That(address.IsNavigation, Is.True);
        Assert.That(address.TypeDisplay, Is.EqualTo("AddressQueryModel?"));

        var addressModel = introspection.Types.Single(_ => _.Model == "AddressQueryModel");
        Assert.That(addressModel.Members.Select(_ => _.Name), Is.EquivalentTo(["City", "Country"]));
    }

    // The stamp is deliberately not asserted here: Describe's snapshot carries it, and that it did not
    // move when these annotations were added is the check that deprecation stays out of it. Hashing it
    // would report every deployed client as stale over a note to whoever next rebuilds one.
    [Test]
    public void ObsoleteIsCarriedForMembersSourcesAndTypes()
    {
        var introspection = SharedProcessor.Instance.Describe();

        // An [Obsolete] with a message: the message is what the generated client's warning quotes.
        var headcount = introspection.Types.Single(_ => _.Model == "DepartmentHeadcountQueryModel")
            .Members.Single(_ => _.Name == "Headcount");
        Assert.That(headcount.Obsolete, Is.EqualTo("Counts open roles too; use the Region rollup."));

        // A bare [Obsolete] is empty rather than null — deprecated, with nothing to add. It is reported on
        // the source as well as the type, so the entry point a query starts from warns too.
        Assert.That(introspection.Types.Single(_ => _.Model == "RegionSummaryQueryModel").Obsolete, Is.Empty);
        Assert.That(introspection.Sources.Single(_ => _.Name == "RegionSummary").Obsolete, Is.Empty);

        // Null everywhere else: absent from the payload entirely, so nothing changes for a member
        // nobody deprecated.
        Assert.That(introspection.Types.Single(_ => _.Model == "EmployeeQueryModel").Obsolete, Is.Null);
        Assert.That(
            introspection.Types.Single(_ => _.Model == "DepartmentHeadcountQueryModel")
                .Members.Single(_ => _.Name == "Department").Obsolete,
            Is.Null);
    }

    [Test]
    public void GuardrailAcceptsCorrectlyAnnotatedModel()
    {
        using var context = TestContext.CreateSeeded();

        // Against the live EF model, Address is a complex type (not an entity) and the sources are real
        // entities/views — so the startup guardrail passes.
        Assert.DoesNotThrow(() => SharedProcessor.Instance.ValidateAgainstModel(context));
    }
}
