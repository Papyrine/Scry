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

        // The direct counterpart, held to the active fleets by hand: the fleet's policy filters the
        // flatten's root, not a query rooted at Press, and the retired fleet holds a light press.
        var retired = context.Fleets.Single(_ => _.Name == "Retired").Id;
        var direct = Names(
            processor,
            context,
            "Press",
            [
                new WhereOp(new BinaryNode(BinaryOp.NotEqual, new MemberNode(["FleetId"]), new ConstNode(retired.ToString(), ClrTypeTag.Int32))),
                SelectName()
            ]);

        // Skipping the derived policy answers "Big press" as well; skipping the element's answers
        // "Small press" as well. The two yards' light presses are what both chains let through.
        Assert.Multiple(() =>
        {
            Assert.That(flattened, Is.EqualTo(["Annex press", "Depot press"]));
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

        Assert.That(flattened, Is.EqualTo(["Annex press", "Big press", "Depot press", "Drill", "Small press"]));
    }

    // A root carrying two policies — its base's and its own — over the same flatten: the count the
    // executor resets at the flatten is the element's, whatever the root carried.
    [Test]
    public void NarrowingAfterAFlattenFromATwiceRootAppliesTheDerivedPolicy()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(
            _ => _.AddPolicy<Fleet, ActiveFleetsOnlyPolicy>(),
            _ => _.AddPolicy<Yard, StaffedYardsOnlyPolicy>(),
            _ => _.AddPolicy<Press, LightPressesOnlyPolicy>());

        var flattened = Names(processor, context, "Yard", [new SelectManyOp(["Machines"]), new OfTypeOp("Press"), SelectName()]);

        // The unstaffed yard's press is hidden by the root's own policy; the depot's light press
        // survives the derived policy. A heavy press anywhere would have proven the derived policy
        // ran, and there is none in a yard, so the light policy is the one whose skipping would show.
        Assert.That(flattened, Is.EqualTo(["Depot press"]));
    }

    // A flatten stops the denied-row probe: the rows after it are the elements, and the probe asks
    // about the root's. A derived policy that fails the request elsewhere therefore hides after a
    // flatten instead. Pinned as the accepted behaviour — hiding discloses nothing, which is the
    // safe direction — so a change to it is a deliberate one.
    [Test]
    public void AnErroringDerivedPolicyHidesRatherThanFailsAfterAFlatten()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(
            _ => _.AddPolicy<Press, HeavyPressesOnlyPolicy>(new()
            {
                RootList = DeniedRowMode.Error
            }));

        var flattened = Names(processor, context, "Fleet", [new SelectManyOp(["Machines"]), new OfTypeOp("Press"), SelectName()]);

        Assert.That(flattened, Is.EqualTo(["Big press"]));
    }

    // Down a three-level chain, each level's policy is applied exactly once: the narrowing to the
    // middle applies the middle's, and the narrowing to the leaf applies only what the leaf adds.
    [Test]
    public void NarrowingTwiceAppliesEachLevelOnce()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(
            _ => _.AddPolicy<Press, TalliedPressPolicy>(),
            _ => _.AddPolicy<HeavyPress, TalliedHeavyPressPolicy>());
        TalliedPressPolicy.Applications = 0;
        TalliedHeavyPressPolicy.Applications = 0;

        var names = Names(processor, context, "Machine", [new OfTypeOp("Press"), new OfTypeOp("HeavyPress"), SelectName()]);

        Assert.Multiple(() =>
        {
            Assert.That(names, Is.EqualTo(["Big press"]));
            Assert.That(TalliedPressPolicy.Applications, Is.EqualTo(1));
            Assert.That(TalliedHeavyPressPolicy.Applications, Is.EqualTo(1));
        });
    }

    // A right join refuses a narrowed outer side, since EF hoists the narrowing into the combined
    // WHERE and the join quietly turns inner. A flatten over a Hide-mode element narrows inside the
    // collection subquery instead, which EF keeps as an APPLY — so the validator lets it through,
    // and this pins that it is right to: the hidden machine stays hidden, and the join stays a join.
    [Test]
    public void ARightJoinAfterAFlattenKeepsTheElementPolicy()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(
            _ => _.AddPolicy<Machine, WorkingMachinesOnlyPolicy>(new()
            {
                CollectionNavigation = DeniedCollectionMode.Hide
            }));
        var request = QueryRequest.Create(
            "Fleet",
            [
                new SelectManyOp(["Machines"]),
                new JoinOp(
                    "Fleet",
                    JoinKind.Right,
                    new MemberNode(["FleetId"]),
                    new MemberNode(["Id"]),
                    null,
                    [new("Machine", JoinSide.Outer, ["Name"]), new("Fleet", JoinSide.Inner, ["Name"])])
            ]);

        var rows = processor.Execute(request, context).Payload
            .EnumerateArray()
            .Select(_ => (Machine: _.GetProperty("machine").GetString(), Fleet: _.GetProperty("fleet").GetString()))
            .Order()
            .ToList();

        Assert.That(rows, Is.EqualTo([("Annex press", "Annex"), ("Big press", "Main"), ("Depot press", "Depot"), ("Drill", "Main"), ("Old press", "Retired")]));
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
