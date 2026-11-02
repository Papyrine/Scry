/// <summary>
/// The SQL Server math surface. Every function here was checked against a real database before it
/// reached the wire: one that Scry validates and rebinds but the provider cannot translate would fail
/// at execution rather than at validation, which is the trap <c>DayOfWeek</c> is kept out for.
/// </summary>
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
        await Assert.That(rows).Count().IsEqualTo(2);
        await Assert.That(rows.Min(_ => _.Log)).IsEqualTo(Math.Log(100d)).Within(0.0001);
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

        await Assert.That(rows.Single().Value).IsEqualTo(2d).Within(0.0001);
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

        await Assert.That(rows.Single().Value).IsEqualTo(Math.Exp(1d)).Within(0.0001);
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

        using (Assert.Multiple())
        {
            await Assert.That(row.Sin).IsEqualTo(Math.Sin(1d)).Within(0.0001);
            await Assert.That(row.Cos).IsEqualTo(Math.Cos(1d)).Within(0.0001);
            await Assert.That(row.Tan).IsEqualTo(Math.Tan(1d)).Within(0.0001);
            await Assert.That(row.Atan).IsEqualTo(Math.Atan(1d)).Within(0.0001);
        }
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

        using (Assert.Multiple())
        {
            await Assert.That(row.Asin).IsEqualTo(Math.Asin(0.5d)).Within(0.0001);
            await Assert.That(row.Acos).IsEqualTo(Math.Acos(0.5d)).Within(0.0001);
        }
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

        await Assert.That(rows.Single().Value).IsEqualTo(Math.Atan2(1d, 2d)).Within(0.0001);
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

        await Assert.That(rows.First().Amount).IsEqualTo(250m);
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

        using (Assert.Multiple())
        {
            await Assert.That(row.Halved).IsEqualTo(0.5d).Within(0.0001);

            // A narrower constant still widens to the member rather than the other way about.
            await Assert.That(row.Scaled).IsEqualTo(150m);

            // Integer division stays integer division, exactly as the same expression does in C#.
            await Assert.That(row.Integral).IsZero();
        }
    }

    [Test]
    public async Task ComparisonStillReadsItsConstantAtTheMembersType()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A comparison keeps inferring a narrower constant's type from the member, which is what lets
        // a bare literal compare against a decimal column; only a constant written wider than the
        // member, and not fitting it, widens the member instead — see NumericPromotionTests.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount > 80)
            .Select(_ => new {_.Amount})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Amount).Order()).IsEquivalentTo([100m, 250m], CollectionOrdering.Matching);
    }

    // SQL's ROUND rounds a midpoint away from zero and the client drops the mode that says so, while
    // Math.Round's default rounds a midpoint to even — so an in-memory source once answered a midpoint
    // differently from a column. A holiday's day of the month halved: the firsts and the twenty-fifth
    // all land on a midpoint, which away from zero rounds up and to even would have rounded down.
    [Test]
    public async Task RoundOverAnInMemorySourceRoundsAwayFromZero()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var whole = await client.Source<Holiday>("Holiday").CountAsync(_ => Math.Round(_.Date.Day / 2m) == 1m);
        var toADigit = await client.Source<Holiday>("Holiday").CountAsync(_ => Math.Round(_.Date.Day / 4m, 1) == 0.3m);

        using (Assert.Multiple())
        {
            await Assert.That(whole).IsEqualTo(2).Because("New Year and Workers Day fall on a first, and 0.5 rounds to 1");
            await Assert.That(toADigit).IsEqualTo(2).Because("0.25 rounds to 0.3");
        }
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
