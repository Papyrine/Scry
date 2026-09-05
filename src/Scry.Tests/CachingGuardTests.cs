/// <summary>
/// The startup guard against an unscoped ETag asks which sources answer by caller. A source carrying
/// a policy is one. So is a POCO source supplied by a factory: it is given the request's services and
/// may read the caller off them, and the freshness token watches only the database, so nothing else
/// would notice such a source varying. One registered as the collection itself cannot vary, and is
/// not counted.
/// </summary>
[TestFixture]
public class CachingGuardTests
{
    [Test]
    public void AFactorySuppliedPocoSourceAnswersByCaller()
    {
        var processor = Build(_ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));

        var holiday = processor.CallerDependentSources.Single(_ => _.Source == "Holiday");

        Assert.Multiple(() =>
        {
            Assert.That(holiday.Why, Does.Contain("factory"));
            Assert.That(holiday.Hint, Does.Contain("collection itself"));
        });
    }

    [Test]
    public void AFixedPocoSourceDoesNot()
    {
        var processor = Build(_ => _.AddPocoSource(Holiday.Seed().ToList()));

        Assert.That(processor.CallerDependentSources.Select(_ => _.Source), Does.Not.Contain("Holiday"));
    }

    // The sources are named in one order, by name, so a startup message names the same one every
    // run; a policied source that sorts before the factory-supplied one is named first, with no
    // registration to suggest instead.
    [Test]
    public void SourcesAreNamedInOneOrder()
    {
        var processor = Build(_ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));

        var sources = processor.CallerDependentSources.ToList();
        var names = sources.Select(_ => _.Source).ToList();
        var first = sources[0];

        Assert.Multiple(() =>
        {
            Assert.That(names, Is.EqualTo(names.OrderBy(_ => _, StringComparer.Ordinal)));
            Assert.That(first.Source, Is.Not.EqualTo("Holiday"));
            Assert.That(first.Why, Does.Contain("policy"));
            Assert.That(first.Hint, Is.Null);
        });
    }

    static ScryProcessor Build(Action<ScryOptions> configure) =>
        ScryProcessor.Create<TestContext>(configure);
}
