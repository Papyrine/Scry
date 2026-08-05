/// <summary>
/// <c>double.DegreesToRadians</c> / <c>RadiansToDegrees</c>, carried as <c>MathDegreesToRadians</c> /
/// <c>MathRadiansToDegrees</c>. Statics on the floating types rather than on <c>Math</c>, translated
/// by the provider to SQL's <c>RADIANS</c> / <c>DEGREES</c>; like the trigonometry they accompany,
/// they are defined over double alone, so an integer or decimal member widens to reach them.
/// </summary>
[TestFixture]
public class AngleConversionTests
{
    [Test]
    public async Task ConvertsDegreesToRadians()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Quantity 3 → 180 degrees → π.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Quantity == 3u)
            .Select(_ => new {Radians = double.DegreesToRadians(_.Quantity * 60d)})
            .ToListAsync();

        Assert.That(rows.Single().Radians, Is.EqualTo(Math.PI).Within(1e-9));
    }

    [Test]
    public async Task ConvertsRadiansToDegrees()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Amount 100 → π radians → 180 degrees.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount == 100m)
            .Select(_ => new {Degrees = double.RadiansToDegrees((double)_.Amount / 100d * Math.PI)})
            .ToListAsync();

        Assert.That(rows.Single().Degrees, Is.EqualTo(180d).Within(1e-9));
    }

    [Test]
    public async Task FiltersByAConvertedAngle()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Quantities 3, 7 and 1 → 180°, 420° and 60° → π, 7π/3 and π/3 radians.
        var count = await client.Source<Order>("Order")
            .CountAsync(_ => double.DegreesToRadians(_.Quantity * 60d) > 3d);

        Assert.That(count, Is.EqualTo(2));
    }

    // The float statics spell the same functions.
    [Test]
    public async Task TheFloatSpellingMeansTheSame()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Quantity == 3u)
            .Select(_ => new {Radians = float.DegreesToRadians(_.Quantity * 60f)})
            .ToListAsync();

        Assert.That(rows.Single().Radians, Is.EqualTo(Math.PI).Within(1e-6));
    }

    [Test]
    public void RejectsSomethingNotNumeric()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Radians", new NodeValue(new CallNode(KnownFunction.MathDegreesToRadians, new MemberNode(["Region"]), [])))]))]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("DegreesToRadians is not supported over"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
