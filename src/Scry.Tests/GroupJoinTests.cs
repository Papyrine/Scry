/// <summary>
/// A group join pairs each outer row with the matching inner rows. The group is only ever aggregated
/// — projecting it would make the response nested — so what a client gets is a flat row carrying a
/// correlated aggregate over the second source.
/// </summary>
public class GroupJoinTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record DepartmentSize(string Department, int Headcount);

    record DepartmentNames(string Department, string? First);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task CountsTheMatchingInnerRows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientGroupJoin
        var rows = await client.Source<Department>("Department")
            .GroupJoin(
                client.Source<Employee>("Employee"),
                _ => _.Id,
                _ => _.DepartmentId,
                (department, employees) => new DepartmentSize(department.Name, employees.Count()))
            .ToListAsync();
        // end-snippet

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single(_ => _.Department == "Engineering").Headcount).IsEqualTo(2);
            await Assert.That(rows.Single(_ => _.Department == "Sales").Headcount).IsEqualTo(2);
        }
    }

    [Test]
    public async Task FoldsTheMatchingInnerRowsWithASelector()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Department>("Department")
            .GroupJoin(
                client.Source<Employee>("Employee"),
                _ => _.Id,
                _ => _.DepartmentId,
                (department, employees) => new DepartmentNames(department.Name, employees.Min(_ => _.Name)))
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single(_ => _.Department == "Engineering").First).IsEqualTo("Aaron");
            await Assert.That(rows.Single(_ => _.Department == "Sales").First).IsEqualTo("Bob");
        }
    }

    [Test]
    public async Task FiltersTheInnerSideBeforeAggregating()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The inner source is filtered where it is resolved, so the group holds only the rows the
        // filter left — exactly as it would for an ordinary join.
        var rows = await client.Source<Department>("Department")
            .GroupJoin(
                client.Source<Employee>("Employee").Where(_ => _.Active),
                _ => _.Id,
                _ => _.DepartmentId,
                (department, employees) => new DepartmentSize(department.Name, employees.Count()))
            .ToListAsync();

        await Assert.That(rows.Single(_ => _.Department == "Sales").Headcount).IsEqualTo(1);
    }

    [Test]
    public async Task KeepsAnOuterRowWithNoMatches()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Department>("Department")
            .GroupJoin(
                client.Source<Employee>("Employee").Where(_ => _.Name == "Nobody"),
                _ => _.Id,
                _ => _.DepartmentId,
                (department, employees) => new DepartmentSize(department.Name, employees.Count()))
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(rows).Count().IsEqualTo(2);
            await Assert.That(rows.Select(_ => _.Headcount)).All(_ => _ == 0);
        }
    }

    [Test]
    public async Task AppliesTheInnerSidePolicyBeforeCounting()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Ticket carries [ReturnableWith(OpenTicketsOnlyPolicy)]. Counting the group must not become a
        // way to observe how many rows the policy hides.
        var rows = await client.Source<Employee>("Employee")
            .GroupJoin(
                client.Source<Ticket>("Ticket"),
                _ => _.Id,
                _ => _.Id,
                (employee, tickets) => new DepartmentSize(employee.Name, tickets.Count()))
            .ToListAsync();

        await Assert.That(rows.Sum(_ => _.Headcount)).IsEqualTo(context.Tickets.Count(_ => _.IsOpen));
    }

    [Test]
    public async Task RejectsReadingTheInnerSideDirectly()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Department",
            [
                new JoinOp(
                    "Employee",
                    JoinKind.Group,
                    new MemberNode(["Id"]),
                    new MemberNode(["DepartmentId"]),
                    InnerPredicate: null,
                    [
                        new("Department", JoinSide.Outer, ["Name"]),
                        new("Employee", JoinSide.Inner, ["Name"])
                    ])
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("reads the inner side of a GroupJoin directly");
    }

    [Test]
    public async Task RejectsAGroupJoinThatAggregatesNothing()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Department",
            [
                new JoinOp(
                    "Employee",
                    JoinKind.Group,
                    new MemberNode(["Id"]),
                    new MemberNode(["DepartmentId"]),
                    InnerPredicate: null,
                    [new("Department", JoinSide.Outer, ["Name"])])
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("must aggregate its inner side");
    }

    [Test]
    public async Task RejectsAnAggregateOnAnOrdinaryJoin()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Department",
            [
                new JoinOp(
                    "Employee",
                    JoinKind.Inner,
                    new MemberNode(["Id"]),
                    new MemberNode(["DepartmentId"]),
                    InnerPredicate: null,
                    [
                        new("Department", JoinSide.Outer, ["Name"]),
                        new("Headcount", JoinSide.Inner, [])
                        {
                            Aggregate = new(AggregateFn.Count, Selector: null)
                        }
                    ])
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("only the inner side of a GroupJoin may do");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
