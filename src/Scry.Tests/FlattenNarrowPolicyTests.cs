/// <summary>
/// A flatten changes which rows the pipeline reads, and so which policy chain they already carry. A
/// narrowing after it has to apply the derived type's own policies on top of the element's — exactly
/// as narrowing from the element's source would — rather than skipping as many levels as the root
/// carried. Every query here has to match its direct counterpart, or the longer request would be the
/// weaker authorization.
/// </summary>
[TestFixture]
public class FlattenNarrowPolicyTests
{
    // Fleet is policied, Machine is not, and Press carries a policy of its own: the shape where the
    // root's chain is as long as the derived type's, so counting the root's levels skips all of them.
    [Test]
    public void NarrowingAfterAFlattenAppliesTheDerivedPolicy()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(
            _ => _.AddPolicy<Fleet, ActiveFleetsOnlyPolicy>(),
            _ => _.AddPolicy<Press, HeavyPressesOnlyPolicy>());

        var flattened = Names(processor, context, "Fleet", [new SelectManyOp(["Machines"]), new OfTypeOp("Press"), SelectName()]);
        var direct = Names(processor, context, "Press", [SelectName()]);

        Assert.Multiple(() =>
        {
            Assert.That(flattened, Is.EqualTo(["Big press"]));
            Assert.That(flattened, Is.EqualTo(direct));
        });
    }

    // The derived type's own members are what a skipped policy would hand over.
    [Test]
    public void NarrowingAfterAFlattenHidesTheDerivedMembersOfADeniedRow()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(
            _ => _.AddPolicy<Fleet, ActiveFleetsOnlyPolicy>(),
            _ => _.AddPolicy<Press, HeavyPressesOnlyPolicy>());

        var response = processor.Execute(
            QueryRequest.Create(
                "Fleet",
                [
                    new SelectManyOp(["Machines"]),
                    new OfTypeOp("Press"),
                    new SelectOp(new([new("Tonnage", new NodeValue(new MemberNode(["Tonnage"])))]))
                ]),
            context);

        var tonnages = response.Payload.EnumerateArray().Select(_ => _.GetProperty("tonnage").GetInt32()).ToList();
        Assert.That(tonnages, Is.EqualTo([200]));
    }

    // The element policied too, read through the collection by opting in: the flatten applies the
    // element's whole chain and the narrowing adds only what the derived type declares. The two
    // policies keep disjoint rows, so a skipped one on either side answers with a name.
    [Test]
    public void NarrowingAfterAFlattenOfAPoliciedElementAppliesBothChains()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(
            _ => _.AddPolicy<Fleet, ActiveFleetsOnlyPolicy>(),
            _ => _.AddPolicy<Machine, WorkingMachinesOnlyPolicy>(new()
            {
                CollectionNavigation = DeniedCollectionMode.Hide
            }),
            _ => _.AddPolicy<Press, LightPressesOnlyPolicy>());

        var flattened = Names(processor, context, "Fleet", [new SelectManyOp(["Machines"]), new OfTypeOp("Press"), SelectName()]);

        // The direct counterpart, held to the active fleet by hand: the fleet's policy filters the
        // flatten's root, not a query rooted at Press, and the retired fleet holds a light press.
        var main = context.Fleets.Single(_ => _.Name == "Main").Id;
        var direct = Names(
            processor,
            context,
            "Press",
            [
                new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["FleetId"]), new ConstNode(main.ToString(), ClrTypeTag.Int32))),
                SelectName()
            ]);

        // Skipping the derived policy answers "Big press"; skipping the element's answers "Small press".
        Assert.Multiple(() =>
        {
            Assert.That(flattened, Is.Empty);
            Assert.That(flattened, Is.EqualTo(direct));
        });
    }

    // Without a policy on the root the count started at zero and the derived chain was applied
    // whole, so this is the case that was always right and must stay so.
    [Test]
    public void NarrowingAfterAFlattenOfAnUnpoliciedRootAppliesTheDerivedPolicy()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(_ => _.AddPolicy<Press, HeavyPressesOnlyPolicy>());

        var flattened = Names(processor, context, "Fleet", [new SelectManyOp(["Machines"]), new OfTypeOp("Press"), SelectName()]);

        Assert.That(flattened, Is.EqualTo(["Big press"]));
    }

    // The root's policy still applies to the flatten itself: the retired fleet's machines are never read.
    [Test]
    public void TheRootPolicyStillFiltersWhatIsFlattened()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(_ => _.AddPolicy<Fleet, ActiveFleetsOnlyPolicy>());

        var flattened = Names(processor, context, "Fleet", [new SelectManyOp(["Machines"]), SelectName()]);

        Assert.That(flattened, Is.EqualTo(["Big press", "Drill", "Small press"]));
    }

    static SelectOp SelectName() =>
        new(new([new("Name", new NodeValue(new MemberNode(["Name"])))]));

    static List<string> Names(ScryProcessor processor, TestContext context, string root, IReadOnlyList<QueryOp> pipeline)
    {
        var response = processor.Execute(QueryRequest.Create(root, pipeline), context);
        return response.Payload
            .EnumerateArray()
            .Select(_ => _.GetProperty("name").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    static ScryProcessor Build(params Action<ScryOptions>[] extras) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            foreach (var extra in extras)
            {
                extra(options);
            }
        });
}
