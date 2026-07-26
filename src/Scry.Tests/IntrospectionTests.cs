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

    static ScryProcessor Processor() =>
        ScryProcessor.Create<TestContext>(
            _ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));
}
