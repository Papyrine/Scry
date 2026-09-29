/// <summary>
/// Round-trips the operators, terminals and functions added beyond the original closed set, each one
/// written as client LINQ and executed against LocalDB — so the translator, the validator, the
/// rebinder and the SQL EF produces are all covered by the same assertion.
/// </summary>
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

    record HolidayRow(string Name, Date Date);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task ContainsOverClosureSetBecomesIn()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        string[] wanted = ["North", "West"];

        // begin-snippet: clientSetMembership
        var rows = await client.Source<Order>("Order")
            .Where(_ => wanted.Contains(_.Region))
            .OrderBy(_ => _.Amount)
            .Select(_ => new OrderShape(_.Region, _.Amount))
            .ToListAsync();
        // end-snippet

        await Assert.That(rows.Select(_ => _.Amount)).IsEquivalentTo([100m, 250m], CollectionOrdering.Matching);
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

        await Assert.That(count).IsZero();
    }

    [Test]
    public async Task ContainsOverListOfIds()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var ids = new List<int>
        {
            1,
            3
        };

        var count = await client.Source<Order>("Order")
            .Where(_ => ids.Contains(_.Id))
            .CountAsync();

        await Assert.That(count).IsEqualTo(2);
    }

    // A set of optional values tested against a required member: C# lifts the member, which the
    // client drops, so the server meets a null candidate over a required type. A null is in no set
    // of required values; it once read as the type's default and matched the inactive employee.
    [Test]
    public async Task ANullCandidateOverARequiredMemberMatchesNothing()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var flags = new List<bool?>
        {
            null
        };

        var count = await client.Source<Employee>("Employee")
            .Where(_ => flags.Contains(_.Active))
            .CountAsync();

        await Assert.That(count).IsZero();
    }

    [Test]
    public async Task ANullCandidateOverAnOptionalMemberMatchesItsNull()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var managers = new List<int?>
        {
            null,
            1
        };

        // Alice manages Aaron and Bob; Alice and Carol have no manager.
        var count = await client.Source<Employee>("Employee")
            .Where(_ => managers.Contains(_.ManagerId))
            .CountAsync();

        await Assert.That(count).IsEqualTo(4);
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

        using (Assert.Multiple())
        {
            await Assert.That(sum).IsEqualTo(425m);
            await Assert.That(average).IsEqualTo(141.666m).Within(0.01m);
            await Assert.That(min).IsEqualTo(75m);
            await Assert.That(max).IsEqualTo(250m);
            await Assert.That(averageQuantity).IsEqualTo(2d);
        }
    }

    [Test]
    public async Task AggregateOverNullableMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // One of the three orders has no discount; SQL SUM and AVG both ignore it.
        var sum = await client.Source<Order>("Order").SumAsync(_ => _.Discount);
        var max = await client.Source<Order>("Order").MaxAsync(_ => _.Discount);

        using (Assert.Multiple())
        {
            await Assert.That(sum).IsEqualTo(15m);
            await Assert.That(max).IsEqualTo(10m);
        }
    }

    [Test]
    public async Task MinOverNoRowsIsDefaultRatherThanAFault()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var min = await client.Source<Order>("Order")
            .Where(_ => _.Region == "Nowhere")
            .MinAsync(_ => _.Amount);

        await Assert.That(min).IsZero();
    }

    [Test]
    public async Task AggregateAfterFilter()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientAggregateTerminal
        var sum = await client.Source<Order>("Order")
            .Where(_ => _.Region == "North")
            .SumAsync(_ => _.Amount);
        // end-snippet

        await Assert.That(sum).IsEqualTo(350m);
    }

    [Test]
    public async Task CountAndLongCountWithPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee").CountAsync(_ => _.Active);
        var longCount = await client.Source<Employee>("Employee").LongCountAsync();
        var longCountFiltered = await client.Source<Employee>("Employee").LongCountAsync(_ => !_.Active);

        using (Assert.Multiple())
        {
            await Assert.That(count).IsEqualTo(3);
            await Assert.That(longCount).IsEqualTo(4L);
            await Assert.That(longCountFiltered).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task AnyAndAllWithPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var anyContractor = await client.Source<Employee>("Employee").AnyAsync(_ => _.Status == Status.Contractor);
        var allActive = await client.Source<Employee>("Employee").AllAsync(_ => _.Active);
        var allNamed = await client.Source<Employee>("Employee").AllAsync(_ => _.Name != "");

        using (Assert.Multiple())
        {
            await Assert.That(anyContractor).IsTrue();
            await Assert.That(allActive).IsFalse();
            await Assert.That(allNamed).IsTrue();
        }
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

        await Assert.That(rows.Select(_ => _.Region).Order()).IsEquivalentTo(["North", "South"], CollectionOrdering.Matching);
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

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task OrderingADeduplicatedQuery()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new RegionRow(_.Region))
            .Distinct()
            .OrderByDescending(_ => _.Region)
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Region)).IsEquivalentTo(["South", "North"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task PagingADeduplicatedQuery()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Top-N over deduplicated values: the ordering makes the slice well defined.
        // begin-snippet: clientDistinctPaging
        var rows = await client.Source<Order>("Order")
            .Select(_ => new RegionRow(_.Region))
            .Distinct()
            .OrderBy(_ => _.Region)
            .Take(1)
            .ToListAsync();
        // end-snippet

        await Assert.That(rows.Single().Region).IsEqualTo("North");
    }

    [Test]
    public async Task PagingADeduplicatedQueryWithoutOrderingIsRejected()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Without an ordering the slice would be of an order the deduplication never defined.
        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(() => client.Source<Order>("Order")
            .Select(_ => new RegionRow(_.Region))
            .Distinct()
            .Take(1)
            .ToListAsync());

        await Assert.That(exception!.Message).Contains("requires an OrderBy");
    }

    // An ordering written before the deduplication described the rows that fed it, and EF drops it
    // under DISTINCT unless every ordered column is projected — so it leaves the slice as undefined
    // as no ordering at all.
    [Test]
    public async Task PagingADeduplicatedQueryOrderedBeforeTheDistinctIsRejected()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(() => client.Source<Order>("Order")
            .OrderBy(_ => _.Placed)
            .Select(_ => new RegionRow(_.Region))
            .Distinct()
            .Take(1)
            .ToListAsync());

        await Assert.That(exception!.Message).Contains("requires an OrderBy");
    }

    [Test]
    public async Task OrderingADeduplicatedQueryByAnotherMemberIsRejected()
    {
        await using var context = TestContext.CreateSeeded();

        // Only the deduplicated member survives the Distinct, so it is the only thing to order by.
        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))])),
                new DistinctOp(),
                new OrderByOp(new MemberNode(["Amount"]), Descending: false)
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("projected member");
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

        await Assert.That(rows).Count().IsEqualTo(3);
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

        using (Assert.Multiple())
        {
            await Assert.That(last!.Name).IsEqualTo("Carol");
            await Assert.That(lastOrDefault).IsNull();
        }
    }

    [Test]
    public async Task LastWithoutOrderingIsRejected()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(() => client.Source<Employee>("Employee")
            .Select(_ => new NameRow(_.Name))
            .LastAsync());

        await Assert.That(exception!.Message).Contains("ordered");
    }

    [Test]
    public async Task ElementAt()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        IQueryable<NameRow> Ordered() =>
            client.Source<Employee>("Employee")
                .OrderBy(_ => _.Name)
                .Select(_ => new NameRow(_.Name));

        var second = await Ordered().ElementAtAsync(1);
        var past = await Ordered().ElementAtOrDefaultAsync(99);

        using (Assert.Multiple())
        {
            await Assert.That(second!.Name).IsEqualTo("Alice");
            await Assert.That(past).IsNull();
        }
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

        using (Assert.Multiple())
        {
            await Assert.That(byLength).IsEqualTo(3).Because("Aaron, Alice and Carol");
            await Assert.That(byTrimmed).IsEqualTo(1);
            await Assert.That(bySubstring).IsEqualTo(1);
            await Assert.That(bySubstringToEnd).IsEqualTo(1);
            await Assert.That(byIndexOf).IsEqualTo(1).Because("Bob");
            await Assert.That(byReplace).IsEqualTo(1);
            await Assert.That(byWhiteSpace).IsEqualTo(4);
        }
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
        var byMillisecond = await Orders().CountAsync(_ => _.Placed.Millisecond == 0);
        var byDayOfYear = await Orders().CountAsync(_ => _.Placed.DayOfYear == 365);
        var byDatePart = await Orders().CountAsync(_ => _.Placed.Date == new DateTime(2026, 3, 4));
        var byAddDays = await Orders().CountAsync(_ => _.Placed.AddDays(1).Day == 5);
        var byAddMonths = await Orders().CountAsync(_ => _.Placed.AddMonths(1).Month == 4);

        using (Assert.Multiple())
        {
            await Assert.That(byYear).IsEqualTo(2);
            await Assert.That(byMonth).IsEqualTo(1);
            await Assert.That(byHour).IsEqualTo(1);
            await Assert.That(byMinute).IsEqualTo(1);
            await Assert.That(bySecond).IsEqualTo(1);
            await Assert.That(byMillisecond).IsEqualTo(3).Because("none of the seeded times carry milliseconds");
            await Assert.That(byDayOfYear).IsEqualTo(1).Because("31 December 2025");
            await Assert.That(byDatePart).IsEqualTo(1);
            await Assert.That(byAddDays).IsEqualTo(1);
            await Assert.That(byAddMonths).IsEqualTo(1);
        }
    }

    [Test]
    public async Task DatePartOverPocoSource()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // DateOnly carries a different set of parts to DateTime, and the POCO source runs them in
        // memory rather than as SQL.
        var count = await client.Source<Holiday>("Holiday").CountAsync(_ => _.Date.Month == 12);

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task MathFunctions()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        IQueryable<Order> Orders() => client.Source<Order>("Order");

        var bySqrt = await Orders().CountAsync(_ => Math.Sqrt((double) _.Amount) > 15d);
        var byPow = await Orders().CountAsync(_ => Math.Pow(_.Quantity, 2d) == 49d);
        var byTruncate = await Orders().CountAsync(_ => Math.Truncate(_.Amount / 3) == 33m);

        var byAbs = await Orders().CountAsync(_ => Math.Abs(_.Amount) == 75m);
        var byRound = await Orders().CountAsync(_ => Math.Round(_.Amount / 3, 2) == 33.33m);
        var byCeiling = await Orders().CountAsync(_ => Math.Ceiling(_.Amount / 3) == 34m);
        var byFloor = await Orders().CountAsync(_ => Math.Floor(_.Amount / 3) == 33m);

        using (Assert.Multiple())
        {
            await Assert.That(bySqrt).IsEqualTo(1).Because("only 250 has a root above 15");
            await Assert.That(byPow).IsEqualTo(1).Because("the order with quantity 7");
            await Assert.That(byTruncate).IsEqualTo(1);
            await Assert.That(byAbs).IsEqualTo(1);
            await Assert.That(byRound).IsEqualTo(1);
            await Assert.That(byCeiling).IsEqualTo(1);
            await Assert.That(byFloor).IsEqualTo(1);
        }
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

        using (Assert.Multiple())
        {
            await Assert.That(even).IsEqualTo(1);
            await Assert.That(coalesced).IsEqualTo(1).Because("the order with no discount");
            await Assert.That(conditional).IsEqualTo(1).Because("Bob");
        }
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

        await Assert.That(rows.Single().Name).IsEqualTo("ALICE");
    }

    [Test]
    public async Task ArithmeticAndConditionalInAProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientComputedProjection
        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Amount)
            .Select(_ => new OrderShape(
                _.Region == "North" ? "N" : "S",
                _.Amount - (_.Discount ?? 0m)))
            .ToListAsync();
        // end-snippet

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(_ => _.Region)).IsEquivalentTo(["S", "N", "N"], CollectionOrdering.Matching);
            await Assert.That(rows.Select(_ => _.Amount)).IsEquivalentTo([70m, 90m, 250m], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ConstantOnlyProjectionMemberIsRejected()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A leaf that reads nothing from the row is a value the client already has, and EF rejects a
        // constant in a client projection outright — so it is reported as a rejection, not a fault.
        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(() => client.Source<Employee>("Employee")
            .Select(_ => new NameRow("fixed"))
            .ToListAsync());

        await Assert.That(exception!.Message).Contains("must read at least one member");
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

        await Assert.That(rows.Single().Name).IsEqualTo("4lice!");
    }

    [Test]
    public async Task ExpressionInANestedProjectionMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The navigation the nested object descends into is inferred from the path inside the
        // expression, not from a bare path.
        // begin-snippet: clientNestedComputed
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name == "Alice")
            .Select(_ => new EmployeeCard(_.Name, new(_.Department!.Name.ToUpper())))
            .ToListAsync();
        // end-snippet

        await Assert.That(rows.Single().Department.Name).IsEqualTo("ENGINEERING");
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

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single().Department.Name).IsEqualTo("Engineering");
            await Assert.That(rows.Single().Department.Length).IsEqualTo(11);
        }
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

        await Assert.That(rows.Select(_ => _.Name).Order()).IsEquivalentTo(["NORTH", "SOUTH"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ComposedAggregatesInAGroupedProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // North holds two orders totalling 350; the mean is computed from two aggregates rather than
        // asked for directly.
        // begin-snippet: clientGroupedComputed
        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new OrderShape(_.Key, _.Sum(_ => _.Amount) / _.Count()))
            .ToListAsync();
        // end-snippet

        await Assert.That(rows.Single(_ => _.Region == "North").Amount).IsEqualTo(175m);
    }

    [Test]
    public async Task ANonKeyMemberInAGroupedProjectionIsStillRejected()
    {
        await using var context = TestContext.CreateSeeded();

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

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("group key or aggregates");
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

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single().Region).IsEqualTo("North");
            await Assert.That(rows.Single().Amount).IsEqualTo(350m);
        }
    }

    [Test]
    public async Task HavingOverAnAggregateAndTheKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientHaving
        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Where(_ => _.Sum(_ => _.Amount) > 100m && _.Key != "South")
            .Select(_ => new OrderShape(_.Key, _.Sum(_ => _.Amount)))
            .ToListAsync();
        // end-snippet

        await Assert.That(rows.Single().Region).IsEqualTo("North");
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

        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task HavingOverANonKeyMemberIsRejected()
    {
        await using var context = TestContext.CreateSeeded();

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

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("group key or aggregates");
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

        await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(["Carol", "Bob", "Alice", "Aaron"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReverseWithoutOrderingIsRejected()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(() => client.Source<Employee>("Employee")
            .Reverse()
            .Select(_ => new NameRow(_.Name))
            .ToListAsync());

        await Assert.That(exception!.Message).Contains("ordered");
    }

    [Test]
    public async Task StringConcatenation()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var separator = " of ";

        // Both spellings mean the same thing: an Add of two strings, which the server rebinds to
        // string.Concat.
        var byOperator = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.Name + separator + _.Address.City == "Alice of London");
        var byConcat = await client.Source<Employee>("Employee")
            .CountAsync(_ => string.Concat(_.Name, separator, _.Address.City) == "Alice of London");

        using (Assert.Multiple())
        {
            await Assert.That(byOperator).IsEqualTo(1);
            await Assert.That(byConcat).IsEqualTo(1);
        }
    }

    [Test]
    public async Task InterpolatedString()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name == "Alice")
            .Select(_ => new NameRow($"{_.Name} ({_.Address.City})"))
            .ToListAsync();

        await Assert.That(rows.Single().Name).IsEqualTo("Alice (London)");
    }

    [Test]
    public async Task AFormattedInterpolationHoleIsRejected()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A format specifier would change the value, and the database has no equivalent spelling.
        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => client.Source<Order>("Order")
            .Select(_ => new NameRow($"{_.Amount:N2}"))
            .ToListAsync());

        await Assert.That(exception!.Message).Contains("plain holes").Or.Contains("string values");
    }

    [Test]
    public async Task ANonStringInterpolationHoleIsConverted()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A hole that is not a string is converted by the database, the same as a non-string operand
        // of '+' is — see StringConcatTests.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new NameRow($"{_.Region}-{_.Amount}"))
            .ToListAsync();

        await Assert.That(rows.Single().Name).StartsWith("South-75");
    }

    // Four holes bind the params overload of string.Format, whose array is its second argument. The
    // fixed-arity overloads stop at three holes, so an interpolation could grow to three and then
    // failed at the fourth as an unsupported array.
    [Test]
    public async Task InterpolatedStringWithFourHoles()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new NameRow($"{_.Region} {_.Id} {_.Grade} {_.Amount}"))
            .ToListAsync();

        await Assert.That(rows.Single().Name).StartsWith("South ").And.Contains(" 75");
    }

    [Test]
    public async Task CharMemberAndConstant()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var wanted = 'A';

        // char is primitive, so it is already a scalar on both sides. Its constant has no tag of its
        // own and uses the String tag, reconciled to the member's type server-side.
        var byLiteral = await client.Source<Order>("Order").CountAsync(_ => _.Grade == 'B');
        var byCaptured = await client.Source<Order>("Order").CountAsync(_ => _.Grade == wanted);

        using (Assert.Multiple())
        {
            await Assert.That(byLiteral).IsEqualTo(1);
            await Assert.That(byCaptured).IsEqualTo(2);
        }
    }

    [Test]
    public async Task OrderingADeduplicatedQueryOverSeveralMembers()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Two North orders differ by amount, so all three pairs survive deduplication.
        var rows = await client.Source<Order>("Order")
            .Select(_ => new OrderShape(_.Region, _.Amount))
            .Distinct()
            .OrderBy(_ => _.Amount)
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Amount)).IsEquivalentTo([75m, 100m, 250m], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CountingADeduplicatedQueryOverSeveralMembers()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var pairs = await client.Source<Order>("Order")
            .Select(_ => new OrderShape(_.Region, _.Amount))
            .Distinct()
            .CountAsync();

        // Region alone collapses to two; the pair does not.
        var regions = await client.Source<Order>("Order")
            .Select(_ => new RegionRow(_.Region))
            .Distinct()
            .CountAsync();

        using (Assert.Multiple())
        {
            await Assert.That(pairs).IsEqualTo(3);
            await Assert.That(regions).IsEqualTo(2);
        }
    }

    [Test]
    public async Task PagingADeduplicatedQueryOverSeveralMembers()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new OrderShape(_.Region, _.Amount))
            .Distinct()
            .OrderByDescending(_ => _.Amount)
            .Take(2)
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Amount)).IsEquivalentTo([250m, 100m], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DeduplicatingOverSeveralMembersInAnInMemorySource()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A POCO source deduplicates under LINQ to Objects, where the row's own structural equality
        // is what makes it work.
        var count = await client.Source<Holiday>("Holiday")
            .Select(_ => new HolidayRow(_.Name, _.Date))
            .Distinct()
            .CountAsync();

        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task OrderingADeduplicatedQueryByANestedMemberIsRejected()
    {
        await using var context = TestContext.CreateSeeded();

        // A nested object contributes several leaves under one name, leaving nothing to order by.
        var request = QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new(
                [
                    new("Name", new NodeValue(new MemberNode(["Name"]))),
                    new("Department", new NestedValue(
                        ["Department"],
                        new([new("Name", new NodeValue(new MemberNode(["Name"])))])))
                ])),
                new DistinctOp(),
                new OrderByOp(new MemberNode(["Name"]), Descending: false)
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("nested projection member");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
