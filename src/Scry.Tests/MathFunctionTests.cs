/// <summary>
/// The SQL Server math surface. Every function here was checked against a real database before it
/// reached the wire: one that Scry validates and rebinds but the provider cannot translate would fail
/// at execution rather than at validation, which is the trap <c>DayOfWeek</c> is kept out for.
/// </summary>
[TestFixture]
public class MathFunctionTests
{
    [Test]
    public async Task ExpAndTheLogarithms()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientMathFunctions
        var rows = await client.Source<Order>("Order")
            .Where(_ => Math.Log10((double)_.Amount) >= 2)
            .Select(_ => new {_.Region, Log = Math.Log((double)_.Amount)})
            .ToListAsync();
        // end-snippet

        // 100 and 250 clear log10 >= 2; 75 does not.
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Min(_ => _.Log), Is.EqualTo(Math.Log(100d)).Within(0.0001));
    }

    [Test]
    public async Task LogarithmToABase()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount == 100m)
            .Select(_ => new {Value = Math.Log((double)_.Amount, 10d)})
            .ToListAsync();

        Assert.That(rows.Single().Value, Is.EqualTo(2d).Within(0.0001));
    }

    [Test]
    public async Task ExpRoundTrips()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount == 75m)
            .Select(_ => new {Value = Math.Exp(_.Quantity)})
            .ToListAsync();

        Assert.That(rows.Single().Value, Is.EqualTo(Math.Exp(1d)).Within(0.0001));
    }

    [Test]
    public async Task TheTrigonometricFunctions()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount == 75m)
            .Select(
                _ => new
                {
                    Sin = Math.Sin(_.Quantity),
                    Cos = Math.Cos(_.Quantity),
                    Tan = Math.Tan(_.Quantity),
                    Atan = Math.Atan(_.Quantity)
                })
            .ToListAsync();

        var row = rows.Single();

        Assert.Multiple(() =>
        {
            Assert.That(row.Sin, Is.EqualTo(Math.Sin(1d)).Within(0.0001));
            Assert.That(row.Cos, Is.EqualTo(Math.Cos(1d)).Within(0.0001));
            Assert.That(row.Tan, Is.EqualTo(Math.Tan(1d)).Within(0.0001));
            Assert.That(row.Atan, Is.EqualTo(Math.Atan(1d)).Within(0.0001));
        });
    }

    [Test]
    public async Task TheInverseTrigonometricFunctions()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Asin and Acos are only defined over [-1, 1], so the member is scaled into range first.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount == 75m)
            .Select(
                _ => new
                {
                    Asin = Math.Asin(_.Quantity / 2d),
                    Acos = Math.Acos(_.Quantity / 2d)
                })
            .ToListAsync();

        var row = rows.Single();

        Assert.Multiple(() =>
        {
            Assert.That(row.Asin, Is.EqualTo(Math.Asin(0.5d)).Within(0.0001));
            Assert.That(row.Acos, Is.EqualTo(Math.Acos(0.5d)).Within(0.0001));
        });
    }

    [Test]
    public async Task Atan2TakesItsSecondOperandAsAnArgument()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount == 75m)
            .Select(_ => new {Value = Math.Atan2(_.Quantity, 2d)})
            .ToListAsync();

        Assert.That(rows.Single().Value, Is.EqualTo(Math.Atan2(1d, 2d)).Within(0.0001));
    }

    [Test]
    public async Task OrdersByAComputedMathValue()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Not only projectable: the same expression is usable as an ordering key.
        var rows = await client.Source<Order>("Order")
            .OrderByDescending(_ => Math.Log((double)_.Amount))
            .Select(_ => new {_.Amount})
            .ToListAsync();

        Assert.That(rows.First().Amount, Is.EqualTo(250m));
    }

    [Test]
    public async Task ArithmeticPromotesToTheWidestOperand()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Translation drops the client's cast, so without the server reapplying C#'s promotion these
        // would be computed in the member's integer type: the first would answer 0 rather than 0.5.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount == 75m)
            .Select(
                _ => new
                {
                    Halved = _.Quantity / 2d,
                    Scaled = _.Amount * 2,
                    Integral = _.Quantity / 2
                })
            .ToListAsync();

        var row = rows.Single();

        Assert.Multiple(() =>
        {
            Assert.That(row.Halved, Is.EqualTo(0.5d).Within(0.0001));

            // A narrower constant still widens to the member rather than the other way about.
            Assert.That(row.Scaled, Is.EqualTo(150m));

            // Integer division stays integer division, exactly as the same expression does in C#.
            Assert.That(row.Integral, Is.Zero);
        });
    }

    [Test]
    public async Task ComparisonStillReadsItsConstantAtTheMembersType()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The promotion applies to arithmetic only: a comparison keeps inferring the constant's type
        // from the member, which is what lets a bare literal compare against a decimal column.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount > 80)
            .Select(_ => new {_.Amount})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Amount).Order(), Is.EqualTo([100m, 250m]));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
