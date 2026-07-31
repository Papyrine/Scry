/// <summary>
/// A row policy is the one annotation that inherits. An opted-in subclass is a source in its own
/// right, so a policy that stopped at the type it was written on would make opting a subclass in a way
/// to read exactly the rows its base's policy hides. Every source applies the whole chain, base-most
/// first, however each level declares it — <c>[ReturnableWith]</c> or a programmatic AddPolicy.
/// </summary>
[TestFixture]
public class PolicyInheritanceTests
{
    [Test]
    public async Task SubclassCannotShedThePolicyItsBaseCarries()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Announcement declares its own policy and derives from Post, which declares one too.
        // "Unpublished notice" is pinned, so Announcement's own policy keeps it and only Post's drops
        // it: it comes back if the base's policy stops at the type that declared it.
        var rows = await client.Source<Announcement>("Announcement")
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Live notice"]));
    }

    [Test]
    public async Task NarrowingToASubclassMatchesQueryingItDirectly()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Narrowing applies the base's policy and then the derived one. Rooting at the derived source
        // has to reach the same rows, or the shorter request would be the weaker authorization.
        var narrowed = await client.Source<Post>("Post")
            .OfType<Announcement>()
            .Select(_ => new {_.Name})
            .ToListAsync();

        var direct = await client.Source<Announcement>("Announcement")
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(narrowed.Select(_ => _.Name), Is.EqualTo(direct.Select(_ => _.Name)));
        Assert.That(narrowed.Select(_ => _.Name), Is.EqualTo(["Live notice"]));
    }

    [Test]
    public async Task ABaseSourceAppliesOnlyThePoliciesOnItsOwnChain()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The other direction: a policy inherits downwards only. Announcement's is not in Post's chain,
        // so the unpinned announcement is still a Post the base's own policy admits.
        var rows = await client.Source<Post>("Post")
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Live notice", "Live post", "Unpinned notice"]));
    }

    [Test]
    public async Task ProgrammaticPolicyOnABaseCoversTheSourcesDerivingFromIt()
    {
        await using var context = TestContext.CreateSeeded();

        // Asset declares no policy of its own — this one is registered against it in code — and Vehicle
        // derives from it and is a source in its own right, reachable without naming Asset at all.
        var client = ClientFor(context, Processor(_ => _.AddPolicy<Asset, VisibleAssetsOnlyPolicy>()));

        var rows = await client.Source<Vehicle>("Vehicle")
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Van"]));
    }

    [Test]
    public async Task ProgrammaticPolicyReplacesTheAttributeOnlyOnTheTypeItNames()
    {
        await using var context = TestContext.CreateSeeded();

        // AllAnnouncementsPolicy displaces Announcement's own [ReturnableWith], which would otherwise
        // have kept the pinned rows only. Post's is declared a level up, so registering this one does
        // not remove it — the unpinned announcement arrives, the unpublished one still does not.
        var client = ClientFor(context, Processor(_ => _.AddPolicy<Announcement, AllAnnouncementsPolicy>()));

        var rows = await client.Source<Announcement>("Announcement")
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Live notice", "Unpinned notice"]));
    }

    static ScryProcessor Processor(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra(options);
        });

    static ScryClient ClientFor(TestContext context) =>
        ClientFor(context, SharedProcessor.Instance);

    static ScryClient ClientFor(TestContext context, ScryProcessor processor) =>
        new((request, _) => Task.FromResult(processor.Execute(request, context)));
}
