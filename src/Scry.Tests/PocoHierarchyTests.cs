/// <summary>
/// A POCO hierarchy is one collection of rows: a derived <c>[QueryablePoco]</c> with no registration
/// of its own reads its nearest registered base's rows narrowed by type. That is also the shape a
/// policy on a POCO base is reached through by narrowing, where the executor's retype runs over an
/// in-memory query rather than a discriminator — pinned here to execute, and to match rooting at the
/// derived source, rather than assumed from the entity case.
/// </summary>
[TestFixture]
public class PocoHierarchyTests
{
    [Test]
    public void NarrowingFromAPocoRootAppliesTheBasePolicy()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(_ => _.AddPolicy<Holiday, PublishedHolidaysOnlyPolicy>());

        var narrowed = Names(processor, context, "Holiday", [new OfTypeOp("PublicHoliday"), SelectName()]);
        var direct = Names(processor, context, "PublicHoliday", [SelectName()]);

        Assert.Multiple(() =>
        {
            Assert.That(narrowed, Is.EqualTo(["Anzac Day"]));
            Assert.That(narrowed, Is.EqualTo(direct));
        });
    }

    [Test]
    public void ADerivedPocoReadsTheBaseRowsNarrowedByType()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build();

        var derived = Names(processor, context, "PublicHoliday", [SelectName()]);
        var all = Names(processor, context, "Holiday", [SelectName()]);

        Assert.Multiple(() =>
        {
            Assert.That(derived, Is.EqualTo(["Anzac Day", "Unpublished day"]));
            Assert.That(all, Has.Count.EqualTo(5));
        });
    }

    // The derived type's own members are readable on its own source and after narrowing, and on
    // neither before it.
    [Test]
    public void TheDerivedMembersAreReadableOnceNarrowed()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build();
        var region = new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))]));

        var narrowed = processor.Execute(QueryRequest.Create("Holiday", [new OfTypeOp("PublicHoliday"), region]), context);

        Assert.Multiple(() =>
        {
            Assert.That(narrowed.Payload.EnumerateArray().Select(_ => _.GetProperty("region").GetString()), Is.All.EqualTo("AU"));
            Assert.Throws<ScryValidationException>(() => processor.Execute(QueryRequest.Create("Holiday", [region]), context));
        });
    }

    // A base with no registration is still refused at startup; reading through an ancestor only
    // reaches a registration that exists.
    [Test]
    public void ABaseWithNoRegistrationIsStillRefusedAtStartup()
    {
        var exception = Assert.Throws<Exception>(() => ScryProcessor.Create<TestContext>(_ => { }))!;

        Assert.That(exception.Message, Does.Contain("has no data registered"));
    }

    // A caller-dependent base makes its derived source caller-dependent too, since that is where the
    // rows come from: the same caching refusal reaches both.
    [Test]
    public void ADerivedPocoInheritsTheBaseCallerDependence()
    {
        var processor = ScryProcessor.Create<TestContext>(_ => _.AddPocoSource<Holiday>(_ => PublicHoliday.SeedWithPublic()));

        Assert.That(processor.CallerDependentSources.Select(_ => _.Source), Does.Contain("PublicHoliday"));
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
