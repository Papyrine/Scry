namespace Scry.Tests;

[TestFixture]
public class IntrospectionTests
{
    [Test]
    public Task Describe()
    {
        var processor = ScryProcessor.Create<TestContext>(
            _ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));

        return Verify(processor.Describe());
    }

    // begin-snippet: namedSourceTest
    [Test]
    public void NameOverridesSourceNameButNotModelName()
    {
        var sources = Processor().Describe().Sources;

        // The CLR type is SalesRegion; [Queryable(Name = "Region")] renames only the source, so the
        // generated model stays SalesRegionQueryModel and the server's introspection agrees with
        // what the generator emits.
        var region = sources.Single(_ => _.Name == "Region");
        Assert.That(region.Model, Is.EqualTo("SalesRegionQueryModel"));
        Assert.That(region.Kind, Is.EqualTo("Entity"));
        Assert.That(sources.Select(_ => _.Name), Does.Not.Contain("SalesRegion"));
    }
    // end-snippet

    [Test]
    public void QueryableViewIsClassifiedAsView()
    {
        var source = Processor().Describe().Sources.Single(_ => _.Name == "DepartmentHeadcount");
        Assert.That(source.Kind, Is.EqualTo("View"));
    }

    [Test]
    public void KeylessQueryableIsClassifiedAsView()
    {
        // [Queryable] on an EF [Keyless] type is the documented equivalent of [QueryableView], and
        // must classify identically. The two branches live in Schema.TryClassify.
        var source = Processor().Describe().Sources.Single(_ => _.Name == "RegionSummary");
        Assert.That(source.Kind, Is.EqualTo("View"));
    }

    [Test]
    public void UnnamedSourcesFallBackToTheTypeName() =>
        Assert.That(
            Processor().Describe().Sources.Select(_ => _.Name),
            Does.Contain("Employee"));

    [Test]
    public void ComplexTypeAppearsInTypesButNotSources()
    {
        var introspection = Processor().Describe();

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
        Assert.That(addressModel.Members.Select(_ => _.Name), Is.EquivalentTo(new[] { "City", "Country" }));
    }

    [Test]
    public void GuardrailAcceptsCorrectlyAnnotatedModel()
    {
        using var context = TestContext.CreateSeeded();

        // Against the live EF model, Address is a complex type (not an entity) and the sources are real
        // entities/views — so the startup guardrail passes.
        Assert.DoesNotThrow(() => Processor().ValidateAgainstModel(context));
    }

    static ScryProcessor Processor() =>
        ScryProcessor.Create<TestContext>(
            _ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));
}
