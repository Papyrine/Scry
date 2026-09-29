/// <summary>
/// Narrowing a query to a derived type. The name on the wire is resolved through the same allow-list
/// a request's own root goes through, so a type that was not opted in is unreachable however it is
/// spelled — and it must actually derive from the type being queried, which is what keeps the
/// narrowing a narrowing.
/// </summary>
public class OfTypeTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record VehicleRow(string Name, int Wheels);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task NarrowsToADerivedType()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientOfType
        var rows = await client.Source<Asset>("Asset")
            .OfType<Vehicle>()
            .Select(_ => new VehicleRow(_.Name, _.Wheels))
            .ToListAsync();
        // end-snippet

        await Assert.That(rows.Select(_ => _.Name).Order()).IsEquivalentTo(["Trailer", "Van"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadsAMemberTheBaseDoesNotExpose()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Wheels is declared on Vehicle, so it is only nameable once the query has narrowed.
        var rows = await client.Source<Asset>("Asset")
            .OfType<Vehicle>()
            .Where(_ => _.Wheels > 2)
            .Select(_ => new VehicleRow(_.Name, _.Wheels))
            .ToListAsync();

        await Assert.That(rows.Single().Name).IsEqualTo("Van");
    }

    [Test]
    public async Task FiltersTheBaseBeforeNarrowing()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Asset>("Asset")
            .Where(_ => _.Name != "Van")
            .OfType<Vehicle>()
            .Select(_ => new VehicleRow(_.Name, _.Wheels))
            .ToListAsync();

        await Assert.That(rows.Single().Name).IsEqualTo("Trailer");
    }

    [Test]
    public async Task OrdersAndCountsTheNarrowedRows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Asset>("Asset")
            .OfType<Building>()
            .CountAsync();

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task GroupsTheNarrowedRows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Asset>("Asset")
            .OfType<Vehicle>()
            .GroupBy(_ => _.Wheels)
            .Select(_ => new {Wheels = _.Key, Count = _.Count()})
            .ToListAsync();

        await Assert.That(rows.Sum(_ => _.Count)).IsEqualTo(2);
    }

    [Test]
    public async Task RejectsNarrowingToATypeThatIsNotOptedIn()
    {
        using var context = TestContext.CreateSeeded();

        // Artwork derives from Asset but carries no [Queryable], so it has no wire name at all.
        var request = QueryRequest.Create("Asset", [new OfTypeOp("Artwork")]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("Unknown source 'Artwork'");
    }

    [Test]
    public async Task RejectsNarrowingToAnUnrelatedType()
    {
        using var context = TestContext.CreateSeeded();

        // Order is opted in, so it has a name — but it is not on this hierarchy, so narrowing to it
        // would widen the query to a source the request never named.
        var request = QueryRequest.Create("Asset", [new OfTypeOp("Order")]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("does not derive from 'Asset'");
    }

    [Test]
    public async Task RejectsNarrowingToTheSameType()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create("Asset", [new OfTypeOp("Asset")]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("does not narrow");
    }

    [Test]
    public async Task RejectsWideningToTheBase()
    {
        using var context = TestContext.CreateSeeded();

        // The reverse direction is not a narrowing: it would let a query rooted at a derived source
        // reach rows the source it named never contained.
        var request = QueryRequest.Create("Vehicle", [new OfTypeOp("Asset")]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("does not derive from 'Vehicle'");
    }

    [Test]
    public async Task RejectsReadingADerivedMemberWithoutNarrowing()
    {
        using var context = TestContext.CreateSeeded();

        // Without the OfType the row is an Asset, whose allow-list has no Wheels.
        var request = QueryRequest.Create(
            "Asset",
            [new WhereOp(new BinaryNode(BinaryOp.GreaterThan, new MemberNode(["Wheels"]), new ConstNode("2", ClrTypeTag.Int32)))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("Wheels");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
