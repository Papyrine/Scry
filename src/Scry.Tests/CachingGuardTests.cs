/// <summary>
/// The startup guard against an unscoped ETag asks which sources answer by caller. A source carrying
/// a policy is one. So is a POCO source supplied by a factory: it is given the request's services and
/// may read the caller off them, and the freshness token watches only the database, so nothing else
/// would notice such a source varying. One registered as the collection itself cannot vary, and is
/// not counted.
/// </summary>
public class CachingGuardTests
{
    [Test]
    public async Task AFactorySuppliedPocoSourceAnswersByCaller()
    {
        var processor = Build(_ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));

        var holiday = processor.CallerDependentSources.Single(_ => _.Source == "Holiday");

        using (Assert.Multiple())
        {
            await Assert.That(holiday.Why).Contains("factory");
            await Assert.That(holiday.Hint).Contains("collection itself");
        }
    }

    [Test]
    public async Task AFixedPocoSourceDoesNot()
    {
        var processor = Build(_ => _.AddPocoSource(Holiday.Seed().ToList()));

        await Assert.That(processor.CallerDependentSources.Select(_ => _.Source)).DoesNotContain("Holiday");
    }

    // The sources are named in one order, by name, so a startup message names the same one every
    // run; a policied source that sorts before the factory-supplied one is named first, with no
    // registration to suggest instead.
    [Test]
    public async Task SourcesAreNamedInOneOrder()
    {
        var processor = Build(_ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));

        var sources = processor.CallerDependentSources.ToList();
        var names = sources.Select(_ => _.Source).ToList();
        var first = sources[0];

        using (Assert.Multiple())
        {
            await Assert.That(names).IsEquivalentTo(names.OrderBy(_ => _, StringComparer.Ordinal), CollectionOrdering.Matching);
            await Assert.That(first.Source).IsNotEqualTo("Holiday");
            await Assert.That(first.Why).Contains("policy");
            await Assert.That(first.Hint).IsNull();
        }
    }

    static ScryProcessor Build(Action<ScryOptions> configure) =>
        ScryProcessor.Create<TestContext>(configure);
}
