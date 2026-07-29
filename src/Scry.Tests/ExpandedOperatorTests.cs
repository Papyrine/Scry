/// <summary>
/// Round-trips the operators, terminals and functions added beyond the original closed set, each one
/// written as client LINQ and executed against LocalDB — so the translator, the validator, the
/// rebinder and the SQL EF produces are all covered by the same assertion.
/// </summary>
[TestFixture]
public class ExpandedOperatorTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record RegionRow(string Region);

    record NameRow(string Name);

    record OrderShape(string Region, decimal Amount);

    record EmployeeCard(string Name, DepartmentCard Department);

    record DepartmentCard(string Name);

    record EmployeeTwoCard(string Name, DepartmentTwoCard Department);

    record DepartmentTwoCard(string Name, int Length);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task ContainsOverClosureSetBecomesIn()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        string[] wanted = ["North", "West"];

        var rows = await client.Source<Order>("Order")
            .Where(_ => wanted.Contains(_.Region))
            .OrderBy(_ => _.Amount)
            .Select(_ => new OrderShape(_.Region, _.Amount))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Amount), Is.EqualTo([100m, 250m]));
    }

    [Test]
    public async Task ContainsOverEmptySetMatchesNothing()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var wanted = new List<string>();

        var count = await client.Source<Order>("Order")
            .Where(_ => wanted.Contains(_.Region))
            .CountAsync();

        Assert.That(count, Is.Zero);
    }

    [Test]
    public async Task ContainsOverListOfIds()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var ids = new List<int> { 1, 3 };

        var count = await client.Source<Order>("Order")
            .Where(_ => ids.Contains(_.Id))
            .CountAsync();

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task AggregateTerminals()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        IQueryable<Order> Orders() => client.Source<Order>("Order");

        var sum = await Orders().SumAsync(_ => _.Amount);
        var average = await Orders().AverageAsync(_ => _.Amount);
        var min = await Orders().MinAsync(_ => _.Amount);
        var max = await Orders().MaxAsync(_ => _.Amount);

        // Average over an integer member returns a double, matching System.Linq's own overloads.
        var averageQuantity = await Orders().AverageAsync(_ => _.Id);

        Assert.Multiple(() =>
        {
            Assert.That(sum, Is.EqualTo(425m));
            Assert.That(average, Is.EqualTo(141.666m).Within(0.01m));
            Assert.That(min, Is.EqualTo(75m));
            Assert.That(max, Is.EqualTo(250m));
            Assert.That(averageQuantity, Is.EqualTo(2d));
        });
    }

    [Test]
    public async Task AggregateOverNullableMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // One of the three orders has no discount; SQL SUM and AVG both ignore it.
        var sum = await client.Source<Order>("Order").SumAsync(_ => _.Discount);
        var max = await client.Source<Order>("Order").MaxAsync(_ => _.Discount);

        Assert.Multiple(() =>
        {
            Assert.That(sum, Is.EqualTo(15m));
            Assert.That(max, Is.EqualTo(10m));
        });
    }

    [Test]
    public async Task MinOverNoRowsIsDefaultRatherThanAFault()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var min = await client.Source<Order>("Order")
            .Where(_ => _.Region == "Nowhere")
            .MinAsync(_ => _.Amount);

        Assert.That(min, Is.Zero);
    }

    [Test]
    public async Task AggregateAfterFilter()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var sum = await client.Source<Order>("Order")
            .Where(_ => _.Region == "North")
            .SumAsync(_ => _.Amount);

        Assert.That(sum, Is.EqualTo(350m));
    }

    [Test]
    public async Task CountAndLongCountWithPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee").CountAsync(_ => _.Active);
        var longCount = await client.Source<Employee>("Employee").LongCountAsync();
        var longCountFiltered = await client.Source<Employee>("Employee").LongCountAsync(_ => !_.Active);

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(longCount, Is.EqualTo(4L));
            Assert.That(longCountFiltered, Is.EqualTo(1L));
        });
    }

    [Test]
    public async Task AnyAndAllWithPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var anyContractor = await client.Source<Employee>("Employee").AnyAsync(_ => _.Status == Status.Contractor);
        var allActive = await client.Source<Employee>("Employee").AllAsync(_ => _.Active);
        var allNamed = await client.Source<Employee>("Employee").AllAsync(_ => _.Name != "");

        Assert.Multiple(() =>
        {
            Assert.That(anyContractor, Is.True);
            Assert.That(allActive, Is.False);
            Assert.That(allNamed, Is.True);
        });
    }

    [Test]
    public async Task DistinctOverProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Three orders across two regions: the projection is deduplicated by the database.
        var rows = await client.Source<Order>("Order")
            .Select(_ => new RegionRow(_.Region))
            .Distinct()
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Region).Order(), Is.EqualTo(["North", "South"]));
    }

    [Test]
    public async Task DistinctCount()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order")
            .Select(_ => new RegionRow(_.Region))
            .Distinct()
            .CountAsync();

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void PagingAfterDistinctIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // An ordering cannot survive a Distinct, so slicing its output would be slicing an undefined
        // order — the same reason EF warns about the shape.
        var exception = Assert.ThrowsAsync<ScryValidationException>(
            () => client.Source<Order>("Order")
                .Select(_ => new RegionRow(_.Region))
                .Distinct()
                .Take(1)
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("Take is not allowed after Distinct"));
    }

    [Test]
    public async Task DistinctOverPocoSourceComparesValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // An in-memory source runs the same operator under LINQ to Objects, where the projected rows
        // are object[] and only an explicit value comparison dedupes them.
        var rows = await client.Source<Holiday>("Holiday")
            .Select(_ => new NameRow(_.Name))
            .Distinct()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task LastRequiresOrderingAndReverses()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var last = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .LastAsync();

        var lastOrDefault = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Where(_ => _.Name == "Nobody")
            .Select(_ => new NameRow(_.Name))
            .LastOrDefaultAsync();

        Assert.Multiple(() =>
        {
            Assert.That(last!.Name, Is.EqualTo("Carol"));
            Assert.That(lastOrDefault, Is.Null);
        });
    }

    [Test]
    public void LastWithoutOrderingIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<ScryValidationException>(
            () => client.Source<Employee>("Employee")
                .Select(_ => new NameRow(_.Name))
                .LastAsync());

        Assert.That(exception!.Message, Does.Contain("ordered"));
    }

    [Test]
    public async Task ElementAt()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        IQueryable<NameRow> Ordered() => client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name));

        var second = await Ordered().ElementAtAsync(1);
        var past = await Ordered().ElementAtOrDefaultAsync(99);

        Assert.Multiple(() =>
        {
            Assert.That(second!.Name, Is.EqualTo("Alice"));
            Assert.That(past, Is.Null);
        });
    }

    [Test]
    public async Task StringFunctions()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        IQueryable<Employee> Employees() => client.Source<Employee>("Employee");

        var byLength = await Employees().CountAsync(_ => _.Name.Length == 5);
        var byTrimmed = await Employees().CountAsync(_ => _.Name.Trim() == "Alice");
        var bySubstring = await Employees().CountAsync(_ => _.Name.Substring(0, 2) == "Al");
        var bySubstringToEnd = await Employees().CountAsync(_ => _.Name.Substring(1) == "lice");
        var byIndexOf = await Employees().CountAsync(_ => _.Name.IndexOf("ob") == 1);
        var byReplace = await Employees().CountAsync(_ => _.Name.Replace("a", "4") == "C4rol");
        var byWhiteSpace = await Employees().CountAsync(_ => !string.IsNullOrWhiteSpace(_.Name));

        Assert.Multiple(() =>
        {
            Assert.That(byLength, Is.EqualTo(3), "Aaron, Alice and Carol");
            Assert.That(byTrimmed, Is.EqualTo(1));
            Assert.That(bySubstring, Is.EqualTo(1));
            Assert.That(bySubstringToEnd, Is.EqualTo(1));
            Assert.That(byIndexOf, Is.EqualTo(1), "Bob");
            Assert.That(byReplace, Is.EqualTo(1));
            Assert.That(byWhiteSpace, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task DateFunctions()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        IQueryable<Order> Orders() => client.Source<Order>("Order");

        var byYear = await Orders().CountAsync(_ => _.Placed.Year == 2026);
        var byMonth = await Orders().CountAsync(_ => _.Placed.Month == 3);
        var byHour = await Orders().CountAsync(_ => _.Placed.Hour == 14);
        var byMinute = await Orders().CountAsync(_ => _.Placed.Minute == 30);
        var bySecond = await Orders().CountAsync(_ => _.Placed.Second == 59);
        var byDayOfYear = await Orders().CountAsync(_ => _.Placed.DayOfYear == 365);
        var byDatePart = await Orders().CountAsync(_ => _.Placed.Date == new DateTime(2026, 3, 4));
        var byAddDays = await Orders().CountAsync(_ => _.Placed.AddDays(1).Day == 5);
        var byAddMonths = await Orders().CountAsync(_ => _.Placed.AddMonths(1).Month == 4);

        Assert.Multiple(() =>
        {
            Assert.That(byYear, Is.EqualTo(2));
            Assert.That(byMonth, Is.EqualTo(1));
            Assert.That(byHour, Is.EqualTo(1));
            Assert.That(byMinute, Is.EqualTo(1));
            Assert.That(bySecond, Is.EqualTo(1));
            Assert.That(byDayOfYear, Is.EqualTo(1), "31 December 2025");
            Assert.That(byDatePart, Is.EqualTo(1));
            Assert.That(byAddDays, Is.EqualTo(1));
            Assert.That(byAddMonths, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DatePartOverPocoSource()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // DateOnly carries a different set of parts to DateTime, and the POCO source runs them in
        // memory rather than as SQL.
        var count = await client.Source<Holiday>("Holiday").CountAsync(_ => _.Date.Month == 12);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task MathFunctions()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        IQueryable<Order> Orders() => client.Source<Order>("Order");

        var byAbs = await Orders().CountAsync(_ => Math.Abs(_.Amount) == 75m);
        var byRound = await Orders().CountAsync(_ => Math.Round(_.Amount / 3, 2) == 33.33m);
        var byCeiling = await Orders().CountAsync(_ => Math.Ceiling(_.Amount / 3) == 34m);
        var byFloor = await Orders().CountAsync(_ => Math.Floor(_.Amount / 3) == 33m);

        Assert.Multiple(() =>
        {
            Assert.That(byAbs, Is.EqualTo(1));
            Assert.That(byRound, Is.EqualTo(1));
            Assert.That(byCeiling, Is.EqualTo(1));
            Assert.That(byFloor, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ModuloCoalesceAndConditional()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var even = await client.Source<Order>("Order").CountAsync(_ => _.Id % 2 == 0);
        var coalesced = await client.Source<Order>("Order").CountAsync(_ => (_.Discount ?? 0m) == 0m);
        var conditional = await client.Source<Employee>("Employee")
            .CountAsync(_ => (_.Active ? _.Name : "inactive") == "inactive");

        Assert.Multiple(() =>
        {
            Assert.That(even, Is.EqualTo(1));
            Assert.That(coalesced, Is.EqualTo(1), "the order with no discount");
            Assert.That(conditional, Is.EqualTo(1), "Bob");
        });
    }

    [Test]
    public async Task FunctionInAProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name == "Alice")
            .Select(_ => new NameRow(_.Name.ToUpper()))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("ALICE"));
    }

    [Test]
    public async Task ArithmeticAndConditionalInAProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Amount)
            .Select(_ => new OrderShape(
                _.Region == "North" ? "N" : "S",
                _.Amount - (_.Discount ?? 0m)))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(_ => _.Region), Is.EqualTo(["S", "N", "N"]));
            Assert.That(rows.Select(_ => _.Amount), Is.EqualTo([70m, 90m, 250m]));
        });
    }

    [Test]
    public void ConstantOnlyProjectionMemberIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A leaf that reads nothing from the row is a value the client already has, and EF rejects a
        // constant in a client projection outright — so it is reported as a rejection, not a fault.
        var exception = Assert.ThrowsAsync<ScryValidationException>(
            () => client.Source<Employee>("Employee")
                .Select(_ => new NameRow("fixed"))
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("must read at least one member"));
    }

    [Test]
    public async Task ConstantCombinedWithARowMemberInAProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A constant is fine as part of a computed leaf: it becomes part of the SQL expression rather
        // than a value materialized on its own.
        var suffix = "!";

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name == "Alice")
            .Select(_ => new NameRow(_.Name.Replace("A", "4") + suffix))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("4lice!"));
    }

    [Test]
    public async Task ExpressionInANestedProjectionMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The navigation the nested object descends into is inferred from the path inside the
        // expression, not from a bare path.
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name == "Alice")
            .Select(_ => new EmployeeCard(_.Name, new(_.Department!.Name.ToUpper())))
            .ToListAsync();

        Assert.That(rows.Single().Department.Name, Is.EqualTo("ENGINEERING"));
    }

    [Test]
    public async Task NestedProjectionMixingAPathAndAnExpression()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name == "Alice")
            .Select(_ => new EmployeeTwoCard(_.Name, new(_.Department!.Name, _.Department!.Name.Length)))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Single().Department.Name, Is.EqualTo("Engineering"));
            Assert.That(rows.Single().Department.Length, Is.EqualTo(11));
        });
    }

    [Test]
    public async Task ExpressionInAGroupedProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new NameRow(_.Key.ToUpper()))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name).Order(), Is.EqualTo(new[] { "NORTH", "SOUTH" }));
    }

    [Test]
    public async Task ComposedAggregatesInAGroupedProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // North holds two orders totalling 350; the mean is computed from two aggregates rather than
        // asked for directly.
        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new OrderShape(_.Key, _.Sum(_ => _.Amount) / _.Count()))
            .ToListAsync();

        Assert.That(rows.Single(_ => _.Region == "North").Amount, Is.EqualTo(175m));
    }

    [Test]
    public void ANonKeyMemberInAGroupedProjectionIsStillRejected()
    {
        using var context = TestContext.CreateSeeded();

        // Composition does not widen what a group can read: every column but the key has been folded
        // away, so burying one inside an expression must not smuggle it back.
        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new(
                [
                    new("Region", new NodeValue(new MemberNode(["Region"]))),
                    new("Smuggled", new NodeValue(new BinaryNode(
                        BinaryOp.Add,
                        new MemberNode(["Amount"]),
                        new ConstNode("1", ClrTypeTag.Decimal))))
                ]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("group key or aggregates"));
    }

    [Test]
    public async Task HavingFiltersGroups()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Two orders in North, one in South: the group filter keeps only the region with more than one.
        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Where(_ => _.Count() > 1)
            .Select(_ => new OrderShape(_.Key, _.Sum(_ => _.Amount)))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Single().Region, Is.EqualTo("North"));
            Assert.That(rows.Single().Amount, Is.EqualTo(350m));
        });
    }

    [Test]
    public async Task HavingOverAnAggregateAndTheKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Where(_ => _.Sum(_ => _.Amount) > 100m && _.Key != "South")
            .Select(_ => new OrderShape(_.Key, _.Sum(_ => _.Amount)))
            .ToListAsync();

        Assert.That(rows.Single().Region, Is.EqualTo("North"));
    }

    [Test]
    public async Task SeveralGroupFiltersConjoin()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Where(_ => _.Count() > 1)
            .Where(_ => _.Max(_ => _.Amount) > 1000m)
            .Select(_ => new OrderShape(_.Key, _.Sum(_ => _.Amount)))
            .ToListAsync();

        Assert.That(rows, Is.Empty);
    }

    [Test]
    public void HavingOverANonKeyMemberIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // Region is the key, Amount is not: every other column has been folded away by the grouping.
        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new WhereOp(new BinaryNode(
                    BinaryOp.GreaterThan,
                    new MemberNode(["Amount"]),
                    new ConstNode("1", ClrTypeTag.Decimal))),
                new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("group key or aggregates"));
    }

    [Test]
    public async Task ReverseInvertsTheOrdering()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Reverse()
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Carol", "Bob", "Alice", "Aaron"]));
    }

    [Test]
    public void ReverseWithoutOrderingIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<ScryValidationException>(
            () => client.Source<Employee>("Employee")
                .Reverse()
                .Select(_ => new NameRow(_.Name))
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("ordered"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
