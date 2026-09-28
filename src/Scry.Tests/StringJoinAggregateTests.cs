/// <summary>
/// <c>string.Join</c> over a group — the text aggregate, SQL's <c>STRING_AGG</c>. The joined values
/// are ordered by themselves: SQL leaves the concatenation order unspecified, so the server imposes
/// one — <c>WITHIN GROUP</c> on SQL Server, the same <c>OrderBy</c> in memory — and the answer reads
/// identically from either source.
/// </summary>
public class StringJoinAggregateTests
{
    [Test]
    public async Task StringJoinsTheGroupsValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // North holds codes "40" and "8"; ordered by themselves as text, "40" sorts first.
        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new
            {
                Region = _.Key,
                Codes = string.Join(",", _.Select(_ => _.Code))
            })
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(regions.Single(_ => _.Region == "North").Codes).IsEqualTo("40,8");
            await Assert.That(regions.Single(_ => _.Region == "South").Codes).IsEqualTo("17");
        }
    }

    [Test]
    public async Task CharJoinsTheGroupsValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // North holds codes "40" and "8"; ordered by themselves as text, "40" sorts first.
        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new
            {
                Region = _.Key,
                Codes = string.Join(',', _.Select(_ => _.Code))
            })
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(regions.Single(_ => _.Region == "North").Codes).IsEqualTo("40,8");
            await Assert.That(regions.Single(_ => _.Region == "South").Codes).IsEqualTo("17");
        }
    }

    // string.Concat is string.Join's empty-separator spelling, and reaches the wire as exactly that.
    [Test]
    public async Task ConcatJoinsWithNothingBetween()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new
            {
                Region = _.Key,
                Codes = string.Concat(_.Select(_ => _.Code))
            })
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(regions.Single(_ => _.Region == "North").Codes).IsEqualTo("408");
            await Assert.That(regions.Single(_ => _.Region == "South").Codes).IsEqualTo("17");
        }
    }

    // Like Join, Concat folds the whole group: the composed forms stay off the text aggregate.
    [Test]
    public async Task AFilteredConcatIsRefusedAtTranslation()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new
                {
                    Codes = string.Concat(_.Where(_ => _.Amount > 90).Select(_ => _.Code))
                })
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("folds the whole group");
    }

    [Test]
    public async Task AConcatOverSomethingNotTextIsRefusedAtTranslation()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new
                {
                    Codes = string.Concat(_.Select(_ => _.Amount))
                })
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("select a string member");
    }

    // The result-selector spelling unfolds into the same GroupBy + Select, so the aggregate reads
    // identically through it.
    [Test]
    public async Task StringJoinsThroughAResultSelector()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(
                _ => _.Region,
                (region, orders) => new
                {
                    Region = region,
                    Codes = string.Join("|", orders.Select(_ => _.Code))
                })
            .ToListAsync();

        await Assert.That(regions.Single(_ => _.Region == "North").Codes).IsEqualTo("40|8");
    }

    [Test]
    public async Task CharJoinsThroughAResultSelector()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(
                _ => _.Region,
                (region, orders) => new
                {
                    Region = region,
                    Codes = string.Join('|', orders.Select(_ => _.Code))
                })
            .ToListAsync();

        await Assert.That(regions.Single(_ => _.Region == "North").Codes).IsEqualTo("40|8");
    }

    [Test]
    public async Task ComposesWithTheOtherAggregates()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new
            {
                Region = _.Key,
                Codes = string.Join(", ", _.Select(_ => _.Code)),
                Total = _.Sum(_ => _.Amount)
            })
            .ToListAsync();

        var north = regions.Single(_ => _.Region == "North");
        using (Assert.Multiple())
        {
            await Assert.That(north.Codes).IsEqualTo("40, 8");
            await Assert.That(north.Total).IsEqualTo(350m);
        }
    }

    // The separator is text either way; it is the values the selector reads that are not.
    [Test]
    public async Task ANonTextSelectorIsRefusedAtTranslation()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new
                {
                    Amounts = string.Join(',', _.Select(_ => _.Amount))
                })
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("joins text");
    }

    // The separator travels only on Join: any other aggregate carrying one is a malformed request.
    [Test]
    public async Task ASeparatorOnAnotherAggregateIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new("Region", new NodeValue(new MemberNode(["Region"]))),
                        new("Total", new NodeValue(new AggregateNode(AggregateFn.Sum, new MemberNode(["Amount"]), ",")))
                    ]))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("does not take a separator");
    }

    [Test]
    public async Task AJoinWithoutASeparatorIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Codes", new NodeValue(new AggregateNode(AggregateFn.Join, new MemberNode(["Code"]))))]))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("Join requires a separator");
    }

    [Test]
    public async Task AJoinOverSomethingNotTextIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Amounts", new NodeValue(new AggregateNode(AggregateFn.Join, new MemberNode(["Amount"]), ",")))]))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("Join aggregates text");
    }

    // The separator is the one string a client hands the aggregate, and it reaches SQL the way every
    // client value does — as a parameter, not as a literal in the statement text. Inlined, each
    // distinct separator would compile and cache a plan of its own.
    [Test]
    public async Task TheSeparatorIsAParameter()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var request = client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new
            {
                Region = _.Key,
                Codes = string.Join(", ", _.Select(_ => _.Code))
            })
            .ToScryRequest();
        var sql = SharedProcessor.Instance.ToQueryString(request, context, NoServices.Instance);

        await Assert.That(sql).Matches(@"STRING_AGG\([^,]+, @\w+\)");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));

    sealed class NoServices :
        IServiceProvider
    {
        public static readonly NoServices Instance = new();

        public object? GetService(Type serviceType) =>
            null;
    }
}
