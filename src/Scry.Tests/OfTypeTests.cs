/// <summary>
/// Narrowing a query to a derived type. The name on the wire is resolved through the same allow-list
/// a request's own root goes through, so a type that was not opted in is unreachable however it is
/// spelled — and it must actually derive from the type being queried, which is what keeps the
/// narrowing a narrowing.
/// </summary>
[TestFixture]
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

        Assert.That(rows.Select(_ => _.Name).Order(), Is.EqualTo(["Trailer", "Van"]));
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

        Assert.That(rows.Single().Name, Is.EqualTo("Van"));
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

        Assert.That(rows.Single().Name, Is.EqualTo("Trailer"));
    }

    [Test]
    public async Task OrdersAndCountsTheNarrowedRows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Asset>("Asset")
            .OfType<Building>()
            .CountAsync();

        Assert.That(count, Is.EqualTo(1));
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

        Assert.That(rows.Sum(_ => _.Count), Is.EqualTo(2));
    }

    [Test]
    public void RejectsNarrowingToATypeThatIsNotOptedIn()
    {
        using var context = TestContext.CreateSeeded();

        // Artwork derives from Asset but carries no [Queryable], so it has no wire name at all.
        var request = QueryRequest.Create("Asset", [new OfTypeOp("Artwork")]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("Unknown source 'Artwork'"));
    }

    [Test]
    public void RejectsNarrowingToAnUnrelatedType()
    {
        using var context = TestContext.CreateSeeded();

        // Order is opted in, so it has a name — but it is not on this hierarchy, so narrowing to it
        // would widen the query to a source the request never named.
        var request = QueryRequest.Create("Asset", [new OfTypeOp("Order")]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("does not derive from 'Asset'"));
    }

    [Test]
    public void RejectsNarrowingToTheSameType()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create("Asset", [new OfTypeOp("Asset")]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("does not narrow"));
    }

    [Test]
    public void RejectsWideningToTheBase()
    {
        using var context = TestContext.CreateSeeded();

        // The reverse direction is not a narrowing: it would let a query rooted at a derived source
        // reach rows the source it named never contained.
        var request = QueryRequest.Create("Vehicle", [new OfTypeOp("Asset")]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("does not derive from 'Vehicle'"));
    }

    [Test]
    public void RejectsReadingADerivedMemberWithoutNarrowing()
    {
        using var context = TestContext.CreateSeeded();

        // Without the OfType the row is an Asset, whose allow-list has no Wheels.
        var request = QueryRequest.Create(
            "Asset",
            [new WhereOp(new BinaryNode(BinaryOp.GreaterThan, new MemberNode(["Wheels"]), new ConstNode("2", ClrTypeTag.Int32)))]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("Wheels"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
