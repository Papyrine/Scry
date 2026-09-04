using System.Linq.Expressions;

/// <summary>
/// Numeric promotion: the widening the server reapplies to an expression's operands. The client's
/// implicit conversions never reach the wire — the translator drops the Convert nodes that are
/// nullable lifting or enum boxing, which the server reproduces anyway — so an expression written as
/// 'decimal member > int member' arrives as two bare members of different types and has to be widened
/// again before it is rebound. A cast the client wrote is another matter: a widening one is carried,
/// since dropping it would change the answer, and a narrowing one is refused. Each case here is
/// written as client LINQ and executed against LocalDB, so the drop, the rebind and the SQL EF
/// produces are all covered by the same assertion.
/// </summary>
[TestFixture]
public class NumericPromotionTests
{
    record IdRow(int Id);

    [Test]
    public async Task ComparisonPromotesToTheWiderOperand()
    {
        // Quantity is uint and Id is int, so the pair widens to the unsigned type before comparing.
        // Orders 1 and 2 have more items than their id; order 3 has fewer.
        var ids = await Ids(_ => _.Quantity > _.Id);

        Assert.That(ids, Is.EqualTo([1, 2]));
    }

    [Test]
    public async Task DecimalWinsOverAnIntegerOperand()
    {
        // Amount is decimal and Quantity is uint, so decimal is the target: it outranks every integer
        // width. These amounts are whole, so the rows would come back the same either way — what the
        // target actually is, is pinned by TheWideningIsInTheSql.
        var ids = await Ids(_ => _.Amount > _.Quantity);

        Assert.That(ids, Is.EqualTo([1, 2, 3]));
    }

    [Test]
    public async Task PromotionDoesNotDependOnOperandOrder()
    {
        // The same pair as above with the narrow operand on the left. The target is the widest of the
        // two either way, so the answer cannot depend on which side it was written on.
        var ids = await Ids(_ => _.Id < _.Amount);

        Assert.That(ids, Is.EqualTo([1, 2, 3]));
    }

    [Test]
    public async Task ANullOperandPropagatesRatherThanBecomingZero()
    {
        // Discount is decimal? and Id is int, so the target is a nullable decimal and order 2 — whose
        // discount is null — matches nothing. Promoting to a non-nullable decimal would read that null
        // as a zero and silently exclude the row for the wrong reason, or include it.
        var ids = await Ids(_ => _.Discount > _.Id);

        Assert.That(ids, Is.EqualTo([1, 3]));
    }

    [Test]
    public async Task AnUnsignedLongOperandKeepsItsFullRange()
    {
        // Sku is ulong and order 2 carries ulong.MaxValue, which is above long.MaxValue. The pair widens
        // to the unsigned long rather than to a signed one, so that row still compares as the largest
        // value rather than wrapping to -1 and dropping out.
        var ids = await Ids(_ => _.Sku > _.Quantity);

        Assert.That(ids, Is.EqualTo([1, 2, 3]));
    }

    [Test]
    public async Task EqualityPromotesLikeAComparison()
    {
        // Equality reaches the same widening as an ordering comparison: no order has an id equal to its
        // quantity, and reading either operand at the other's type would not change that.
        var ids = await Ids(_ => _.Id == _.Quantity);

        Assert.That(ids, Is.Empty);
    }

    // A cast the client wrote is carried where dropping it would change the answer: in arithmetic,
    // where two integers would otherwise divide as integers. Quantities are 3, 7 and 1.
    [Test]
    public async Task AWideningCastMakesTheDivisionFloatingPoint()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new {Half = (double)_.Quantity / 2, PerId = (double)_.Quantity / _.Id})
            .ToListAsync();

        double[] halves = [1.5, 3.5, 0.5];
        double[] perId = [3, 3.5, 1d / 3];
        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(_ => _.Half), Is.EqualTo(halves));
            Assert.That(rows.Select(_ => _.PerId), Is.EqualTo(perId).Within(1e-12));
        });
    }

    [Test]
    public Task TheCarriedCastIsAWideningFunction()
    {
        // The cast travels as the function that reads its target type, over the member — the same
        // function that parses text, which the server tells apart by what it is given.
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var request = client.Source<Order>("Order")
            .Select(_ => new {Half = (double)_.Quantity / 2})
            .ToScryRequest();

        return Verify(request);
    }

    // What is not carried is refused rather than dropped: reading an enum or a char as a number,
    // and narrowing, which the database and the CLR do differently.
    [Test]
    public void ReadingAnEnumAsANumberIsRefused()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.Throws<NotSupportedException>(() => client.Source<Employee>("Employee")
            .Select(_ => new {Code = (int)_.Status})
            .ToScryRequest());

        Assert.That(exception!.Message, Does.Contain("reads an enum as a number"));
    }

    [Test]
    public void ANarrowingCastIsRefused()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.Throws<NotSupportedException>(() => client.Source<Order>("Order")
            .Where(_ => (int)_.Amount > 5)
            .ToScryRequest());

        Assert.That(exception!.Message, Does.Contain("narrows"));
    }

    // The conversions C# writes into a comparison — an enum to its number, a narrower operand to the
    // wider one's type — are not casts the client wrote, and are dropped: the wire compares an enum
    // by name and leaves promoting a comparison to the server. A captured value rather than a
    // literal, since the compiler folds a literal enum to its number before there is a tree at all.
    [Test]
    public void AComparisonStillTravelsAsWritten()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var wanted = Status.FullTime;

        var request = client.Source<Employee>("Employee")
            .Where(_ => _.Status == wanted && _.Id > _.DepartmentId)
            .ToScryRequest();
        var promoted = client.Source<Order>("Order")
            .Where(_ => _.Amount > _.Id)
            .ToScryRequest();

        var json = ScryJson.Serialize(request);
        var promotedJson = ScryJson.Serialize(promoted);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"value\":\"FullTime\",\"tag\":\"Enum\""));
            Assert.That(json, Does.Not.Contain("From"));
            Assert.That(promotedJson, Does.Not.Contain("From"));
        });
    }

    [Test]
    public Task TheWideningIsInTheSql()
    {
        // The promotion is only visible as a cast in the SQL, which is what pins the target type itself
        // rather than the answer it happens to produce. EF maps uint to bigint and ulong to
        // decimal(20,0), so those are the shapes the widened operands take in the query.
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        string Where(Expression<Func<Order, bool>> predicate)
        {
            var request = client.Source<Order>("Order")
                .Where(predicate)
                .ToScryRequest();
            var sql = SharedProcessor.Instance.ToQueryString(request, context, NoServices.Instance);

            // Only the predicate, so adding a column to the model does not churn this.
            return sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        }

        return Verify(
                string.Join(
                    Environment.NewLine,
                    Where(_ => _.Quantity > _.Id),
                    Where(_ => _.Amount > _.Quantity),
                    Where(_ => _.Id < _.Amount),
                    Where(_ => _.Discount > _.Id),
                    Where(_ => _.Sku > _.Quantity),
                    Where(_ => _.Id == _.Quantity)))
            .Snapshot(
                """
                WHERE [o].[Quantity] > CAST([o].[Id] AS bigint)
                WHERE [o].[Amount] > CAST([o].[Quantity] AS decimal(18,2))
                WHERE CAST([o].[Id] AS decimal(18,2)) < [o].[Amount]
                WHERE [o].[Discount] > CAST([o].[Id] AS decimal(18,2))
                WHERE [o].[Sku] > CAST([o].[Quantity] AS decimal(20,0))
                WHERE CAST([o].[Id] AS bigint) = [o].[Quantity]
                """);
    }

    [Test]
    public async Task SumOverAnUnsignedMemberWidensToLong()
    {
        // Sum has no unsigned overload, so the client's own selector is typed long — and that
        // conversion is dropped on the way out. The server sees a bare uint member and has to widen it
        // again to reach an aggregate that exists. Quantities are 3, 7 and 1.
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var total = await client.Source<Order>("Order")
            .SumAsync(_ => _.Quantity);

        Assert.That(total, Is.EqualTo(11L));
    }

    [Test]
    public async Task AverageOverAnUnsignedMemberIsNotTruncated()
    {
        // The same widening under Average. Neither Sum nor Average has an overload taking the member's
        // own unsigned type, so without the promotion there is no aggregate to bind to at all — the
        // widening is what makes the call resolvable, not merely what makes it accurate.
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var average = await client.Source<Order>("Order")
            .AverageAsync(_ => _.Quantity);

        Assert.That(average, Is.EqualTo(11d / 3).Within(1e-12));
    }

    [Test]
    public async Task AnAggregateOverAFloatingBodyStaysFloating()
    {
        // The body is a function rather than a member, so its type comes from the function the server
        // resolved and not from the column. A square root is a double, and the sum of the three stays
        // one rather than collapsing to the decimal the amounts were read from.
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var total = await client.Source<Order>("Order")
            .SumAsync(_ => Math.Sqrt((double)_.Amount));

        Assert.That(total, Is.EqualTo(Math.Sqrt(100) + Math.Sqrt(250) + Math.Sqrt(75)).Within(1e-9));
    }

    [Test]
    public void AnOperandThatIsNotNumericIsLeftAlone()
    {
        // char is not one of the numeric widths, so the pair is not promoted and both operands reach
        // the operator at their own types. .NET defines no GreaterThan over char and int, and that
        // refusal is caught and answered as a validation failure naming the pair — a 400 — rather than
        // escaping as a fault. EF, given the same comparison, builds the SQL and fails at the database.
        var exception = Assert.ThrowsAsync<ScryValidationException>(() => Ids(_ => _.Grade > _.Id))!;

        Assert.That(exception.Message, Does.Contain("'GreaterThan' is not defined for 'Char' and 'Int32'"));
    }

    [Test]
    public async Task AComparisonWithinOneTypeIsUnaffected()
    {
        // The bail-out refuses the mixing, not the type: a char compared to a char never needed
        // promoting. Grades are 'A', 'B' and 'A'.
        var ids = await Ids(_ => _.Grade > 'A');

        Assert.That(ids, Is.EqualTo([2]));
    }

    static async Task<List<int>> Ids(Expression<Func<Order, bool>> predicate)
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(predicate)
            .OrderBy(_ => _.Id)
            .Select(_ => new IdRow(_.Id))
            .ToListAsync();

        return rows.Select(_ => _.Id).ToList();
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));

    sealed class NoServices :
        IServiceProvider
    {
        public static readonly NoServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
