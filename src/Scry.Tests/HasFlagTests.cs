/// <summary>
/// <c>Enum.HasFlag</c> over a [Flags] member, carried as <c>EnumHasFlag</c>. The provider owns the
/// SQL — EF translates the CLR call to <c>(x &amp; flag) = flag</c> — and the same call runs as
/// itself over an in-memory source.
/// </summary>
public class HasFlagTests
{
    [Test]
    public async Task FiltersByASingleFlag()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Perks.HasFlag(Perks.Gym))
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(["Aaron", "Alice", "Carol"], CollectionOrdering.Matching);
    }

    // A combined flag folds into one constant and travels by name — "Parking, Gym" — so the test asks
    // for both bits at once.
    [Test]
    public async Task FiltersByACombinedFlag()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Perks.HasFlag(Perks.Parking | Perks.Gym))
            .Select(_ => new {_.Name})
            .ToListAsync();

        await Assert.That(rows.Single().Name).IsEqualTo("Alice");
    }

    [Test]
    public async Task ReadsAFlagInAProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Select(_ => new {_.Name, Gym = _.Perks.HasFlag(Perks.Gym)})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single(_ => _.Name == "Bob").Gym).IsFalse();
            await Assert.That(rows.Where(_ => _.Name != "Bob").Select(_ => _.Gym)).All(_ => Equals(_, true));
        }
    }

    // HasFlag(None) is vacuously true — (x & 0) == 0 — and the database answers it the way the CLR
    // does.
    [Test]
    public async Task TheEmptyFlagMatchesEveryRow()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.Perks.HasFlag(Perks.None));

        await Assert.That(count).IsEqualTo(4);
    }

    [Test]
    public async Task RejectsHasFlagOverSomethingNotAnEnum()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new CallNode(KnownFunction.EnumHasFlag, new MemberNode(["Name"]), [new ConstNode("Gym", ClrTypeTag.Enum)])),
                new CountOp()
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("HasFlag is not supported over");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
