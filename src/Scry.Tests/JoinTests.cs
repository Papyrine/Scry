/// <summary>
/// A join resolves its second source through the same allow-list and row policy a root goes through,
/// before the two sides meet — so it can only ever narrow.
/// </summary>
public class JoinTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeDepartment(string Employee, string Department);

    record TicketRow(string Employee, string Ticket);

    record VehicleRow(string Employee, string Vehicle);

    record EmployeeWithDepartment(string Employee, string? Department, int? DepartmentId);

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

        using (Assert.Multiple())
        {
            await Assert.That(rows).Count().IsEqualTo(4);
            await Assert.That(rows.Single(_ => _.Employee == "Alice").Department).IsEqualTo("Engineering");
            await Assert.That(rows.Single(_ => _.Employee == "Carol").Department).IsEqualTo("Sales");
        }
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

        await Assert.That(rows.Select(_ => _.Employee).Order()).IsEquivalentTo(["Aaron", "Alice"], CollectionOrdering.Matching);
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

        using (Assert.Multiple())
        {
            await Assert.That(rows).Count().IsEqualTo(4);
            await Assert.That(rows.Select(_ => _.Department)).All(_ => _ is null);
        }
    }

    [Test]
    public async Task RightJoinKeepsUnmatchedInnerRows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Joined on id to id so that two employees have no department at that key — the unmatched
        // inner rows a right join is there to keep.
        // begin-snippet: clientRightJoin
        var rows = await client.Source<Department>("Department")
            .RightJoin(
                client.Source<Employee>("Employee"),
                _ => _.Id,
                _ => _.Id,
                (department, employee) => new EmployeeWithDepartment(
                    employee.Name,
                    department!.Name,
                    department.Id))
            .ToListAsync();
        // end-snippet

        using (Assert.Multiple())
        {
            await Assert.That(rows).Count().IsEqualTo(4);
            await Assert.That(rows.Count(_ => _.Department is null)).IsEqualTo(2);

            // The outer side is the absent one, so its non-nullable int is widened rather than faulting.
            await Assert.That(rows.Count(_ => _.DepartmentId is null)).IsEqualTo(2);
            await Assert.That(rows.Single(_ => _.Employee == "Alice").Department).IsEqualTo("Engineering");
        }
    }

    [Test]
    public async Task RightJoinRejectsANarrowedOuterSide()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // EF hoists the outer predicate out of the join, silently turning the right join into an inner
        // one. Scry refuses the shape rather than answering it wrongly.
        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(
            () => client.Source<Department>("Department")
                .Where(_ => _.Name == "Engineering")
                .RightJoin(
                    client.Source<Employee>("Employee"),
                    _ => _.Id,
                    _ => _.Id,
                    (department, employee) => new EmployeeWithDepartment(employee.Name, department!.Name, department.Id))
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("RightJoin cannot narrow its outer side");
    }

    // A narrowing to a derived type is a predicate on the discriminator, hoisted the same way — and
    // the derived source's own policies are applied after it, so they would be hoisted with it.
    [Test]
    public async Task RightJoinRejectsANarrowedToDerivedOuterSide()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(
            () => client.Source<Asset>("Asset")
                .OfType<Vehicle>()
                .RightJoin(
                    client.Source<Employee>("Employee"),
                    _ => _.Id,
                    _ => _.Id,
                    (vehicle, employee) => new VehicleRow(employee.Name, vehicle!.Name))
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("RightJoin cannot narrow its outer side");
    }

    [Test]
    public async Task RightJoinRejectsAPoliciedOuterSide()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A row policy is a filter, so it would be hoisted the same way — leaving the policy applied
        // but the join semantics wrong. Refused at validation instead.
        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(
            () => client.Source<Ticket>("Ticket")
                .RightJoin(
                    client.Source<Employee>("Employee"),
                    _ => _.Id,
                    _ => _.Id,
                    (ticket, employee) => new TicketRow(employee.Name, ticket!.Name))
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("cannot be the outer side of a RightJoin");
    }

    [Test]
    public async Task RightJoinAppliesTheInnerSidePolicy()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .RightJoin(
                client.Source<Ticket>("Ticket"),
                _ => _.Id,
                _ => _.Id,
                (employee, ticket) => new TicketRow(employee!.Name, ticket.Name))
            .ToListAsync();

        // The policy filters the inner source before the join, so the closed ticket cannot reappear
        // through the side a right join preserves.
        await Assert.That(rows.Select(_ => _.Ticket)).DoesNotContain("Old typo");
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

        await Assert.That(count).IsEqualTo(4);
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

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single().Ticket).IsEqualTo("Login bug");
            await Assert.That(allJoined.Select(_ => _.Ticket)).DoesNotContain("Old typo");
        }
    }

    [Test]
    public async Task JoiningAnUnknownSourceIsRejected()
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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("Unknown source");
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

        Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public async Task ReadingTheWrongSideIsRejected()
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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("not allow-listed");
    }

    [Test]
    public async Task MismatchedKeyTypesAreRejected()
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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("same type");
    }

    [Test]
    public async Task OperatorsAfterAJoinAreRejected()
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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("may follow a Join");
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

        Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public async Task UnsupportedOperatorsOnTheInnerSideAreRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Only Where crosses into the inner side; anything else would describe rows the join consumed.
        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => client.Source<Employee>("Employee")
                .Join(
                    client.Source<Department>("Department").OrderBy(_ => _.Name),
                    _ => _.DepartmentId,
                    _ => _.Id,
                    (employee, department) => new EmployeeDepartment(employee.Name, department.Name))
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("inner side of a join");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
