/// <summary>
/// A POCO hierarchy is one collection of rows: a derived <c>[QueryablePoco]</c> with no registration
/// of its own reads its nearest registered base's rows narrowed by type. That is also the shape a
/// policy on a POCO base is reached through by narrowing, where the executor's retype runs over an
/// in-memory query rather than a discriminator — pinned here to execute, and to match rooting at the
/// derived source, rather than assumed from the entity case.
/// </summary>
public class PocoHierarchyTests
{
    [Test]
    public async Task NarrowingFromAPocoRootAppliesTheBasePolicy()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(_ => _.AddPolicy<Holiday, PublishedHolidaysOnlyPolicy>());

        var narrowed = Names(processor, context, "Holiday", [new OfTypeOp("PublicHoliday"), SelectName()]);
        var direct = Names(processor, context, "PublicHoliday", [SelectName()]);

        using (Assert.Multiple())
        {
            await Assert.That(narrowed).IsEquivalentTo(["Anzac Day"], CollectionOrdering.Matching);
            await Assert.That(narrowed).IsEquivalentTo(direct, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ADerivedPocoReadsTheBaseRowsNarrowedByType()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build();

        var derived = Names(processor, context, "PublicHoliday", [SelectName()]);
        var all = Names(processor, context, "Holiday", [SelectName()]);

        using (Assert.Multiple())
        {
            await Assert.That(derived).IsEquivalentTo(["Anzac Day", "Unpublished day"], CollectionOrdering.Matching);
            await Assert.That(all).Count().IsEqualTo(5);
        }
    }

    // The derived type's own members are readable on its own source and after narrowing, and on
    // neither before it.
    [Test]
    public async Task TheDerivedMembersAreReadableOnceNarrowed()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build();
        var region = new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))]));

        var narrowed = processor.Execute(QueryRequest.Create("Holiday", [new OfTypeOp("PublicHoliday"), region]), context);

        using (Assert.Multiple())
        {
            await Assert.That(narrowed.Payload.EnumerateArray().Select(_ => _.GetProperty("region").GetString())).All(_ => Equals(_, "AU"));
            Assert.ThrowsExactly<ScryValidationException>(() => processor.Execute(QueryRequest.Create("Holiday", [region]), context));
        }
    }

    // A base with no registration is still refused at startup; reading through an ancestor only
    // reaches a registration that exists.
    [Test]
    public async Task ABaseWithNoRegistrationIsStillRefusedAtStartup()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => ScryProcessor.Create<TestContext>(_ => { }));

        await Assert.That(exception.Message).Contains("has no data registered");
    }

    // A caller-dependent base makes its derived source caller-dependent too, since that is where the
    // rows come from: the same caching refusal reaches both.
    [Test]
    public async Task ADerivedPocoInheritsTheBaseCallerDependence()
    {
        var processor = ScryProcessor.Create<TestContext>(_ => _.AddPocoSource<Holiday>(_ => PublicHoliday.SeedWithPublic()));

        await Assert.That(processor.CallerDependentSources.Select(_ => _.Source)).Contains("PublicHoliday");
    }

    static SelectOp SelectName() =>
        new(new([new("Name", new NodeValue(new MemberNode(["Name"])))]));

    static List<string> Names(ScryProcessor processor, TestContext context, string root, IReadOnlyList<QueryOp> pipeline) =>
        processor.Execute(QueryRequest.Create(root, pipeline), context).Payload
            .EnumerateArray()
            .Select(_ => _.GetProperty("name").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();

    static ScryProcessor Build(params Action<ScryOptions>[] extras) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource(PublicHoliday.SeedWithPublic());
            foreach (var extra in extras)
            {
                extra(options);
            }
        });
}
