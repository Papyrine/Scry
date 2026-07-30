/// <summary>
/// A join resolves its second source through the same allow-list and row policy a root goes through,
/// before the two sides meet — so it can only ever narrow.
/// </summary>
[TestFixture]
public class JoinTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeDepartment(string Employee, string Department);

    record TicketRow(string Employee, string Ticket);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task InnerJoin()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Join(
                client.Source<Department>("Department"),
                _ => _.DepartmentId,
                _ => _.Id,
                (employee, department) => new EmployeeDepartment(employee.Name, department.Name))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(4));
            Assert.That(rows.Single(_ => _.Employee == "Alice").Department, Is.EqualTo("Engineering"));
            Assert.That(rows.Single(_ => _.Employee == "Carol").Department, Is.EqualTo("Sales"));
        });
    }

    [Test]
    public async Task InnerJoinWithAFilterOnEachSide()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientJoin
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Active)
            .Join(
                client.Source<Department>("Department").Where(_ => _.Name == "Engineering"),
                _ => _.DepartmentId,
                _ => _.Id,
                (employee, department) => new EmployeeDepartment(employee.Name, department.Name))
            .ToListAsync();
        // end-snippet

        Assert.That(rows.Select(_ => _.Employee).Order(), Is.EqualTo(["Aaron", "Alice"]));
    }

    [Test]
    public async Task LeftJoinKeepsUnmatchedOuterRows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // No department matches, so every employee survives with a null department name.
        var rows = await client.Source<Employee>("Employee")
            .LeftJoin(
                client.Source<Department>("Department").Where(_ => _.Name == "Nowhere"),
                _ => _.DepartmentId,
                _ => _.Id,
                (employee, department) => new EmployeeDepartment(employee.Name, department!.Name))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(4));
            Assert.That(rows.Select(_ => _.Department), Is.All.Null);
        });
    }

    [Test]
    public async Task CountOverAJoin()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee")
            .Join(
                client.Source<Department>("Department"),
                _ => _.DepartmentId,
                _ => _.Id,
                (employee, department) => new EmployeeDepartment(employee.Name, department.Name))
            .CountAsync();

        Assert.That(count, Is.EqualTo(4));
    }

    [Test]
    public async Task TheInnerSourcePolicyIsAppliedBeforeTheJoin()
    {
        await using var context = TestContext.CreateSeeded();

        // Ticket carries [ReturnableWith(OpenTicketsOnlyPolicy)]. Joining to it must not become a way
        // to observe the closed ticket a direct query would hide.
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name == "Alice")
            .Join(
                client.Source<Ticket>("Ticket"),
                _ => _.Id,
                _ => _.Id,
                (employee, ticket) => new TicketRow(employee.Name, ticket.Name))
            .ToListAsync();

        // Alice is Id 1; ticket Id 1 is "Login bug", which is open. The closed ticket is unreachable
        // through the join at any key.
        var allJoined = await client.Source<Employee>("Employee")
            .Join(
                client.Source<Ticket>("Ticket"),
                _ => _.Id,
                _ => _.Id,
                (employee, ticket) => new TicketRow(employee.Name, ticket.Name))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Single().Ticket, Is.EqualTo("Login bug"));
            Assert.That(allJoined.Select(_ => _.Ticket), Does.Not.Contain("Old typo"));
        });
    }

    [Test]
    public void JoiningAnUnknownSourceIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [
                new JoinOp(
                    "Secrets",
                    JoinKind.Inner,
                    new MemberNode(["DepartmentId"]),
                    new MemberNode(["Id"]),
                    null,
                    [new("Name", JoinSide.Outer, ["Name"])])
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("Unknown source"));
    }

    [Test]
    public void AnIgnoredMemberStaysHiddenOnEitherSideOfAJoin()
    {
        using var context = TestContext.CreateSeeded();

        // Salary is [QueryIgnore]d on Employee; the outer side's allow-list still applies.
        var request = QueryRequest.Create(
            "Employee",
            [
                new JoinOp(
                    "Department",
                    JoinKind.Inner,
                    new MemberNode(["DepartmentId"]),
                    new MemberNode(["Id"]),
                    null,
                    [new("Salary", JoinSide.Outer, ["Salary"])])
            ]);

        Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public void ReadingTheWrongSideIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // Region is a member of Order, not of Department: each side is validated against its own type.
        var request = QueryRequest.Create(
            "Employee",
            [
                new JoinOp(
                    "Department",
                    JoinKind.Inner,
                    new MemberNode(["DepartmentId"]),
                    new MemberNode(["Id"]),
                    null,
                    [new("Region", JoinSide.Inner, ["Region"])])
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("not allow-listed"));
    }

    [Test]
    public void MismatchedKeyTypesAreRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [
                new JoinOp(
                    "Department",
                    JoinKind.Inner,
                    new MemberNode(["Name"]),
                    new MemberNode(["Id"]),
                    null,
                    [new("Name", JoinSide.Outer, ["Name"])])
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("same type"));
    }

    [Test]
    public void OperatorsAfterAJoinAreRejected()
    {
        using var context = TestContext.CreateSeeded();

        // Every later operator is single-rooted and could not say which side it meant.
        var request = QueryRequest.Create(
            "Employee",
            [
                new JoinOp(
                    "Department",
                    JoinKind.Inner,
                    new MemberNode(["DepartmentId"]),
                    new MemberNode(["Id"]),
                    null,
                    [new("Name", JoinSide.Outer, ["Name"])]),
                new OrderByOp(new MemberNode(["Name"]), Descending: false)
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("may follow a Join"));
    }

    [Test]
    public void AnEmptyJoinProjectionIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [
                new JoinOp(
                    "Department",
                    JoinKind.Inner,
                    new MemberNode(["DepartmentId"]),
                    new MemberNode(["Id"]),
                    null,
                    [])
            ]);

        Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public void UnsupportedOperatorsOnTheInnerSideAreRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Only Where crosses into the inner side; anything else would describe rows the join consumed.
        var exception = Assert.ThrowsAsync<NotSupportedException>(
            () => client.Source<Employee>("Employee")
                .Join(
                    client.Source<Department>("Department").OrderBy(_ => _.Name),
                    _ => _.DepartmentId,
                    _ => _.Id,
                    (employee, department) => new EmployeeDepartment(employee.Name, department.Name))
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("inner side of a join"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
