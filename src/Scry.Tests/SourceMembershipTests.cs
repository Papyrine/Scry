/// <summary>
/// Membership of a set drawn from another source becomes a SQL <c>IN (SELECT …)</c>. The named source
/// is resolved and policy-filtered before the test, so membership is only ever of rows the caller
/// could have queried directly.
/// </summary>
[TestFixture]
public class SourceMembershipTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record NameRow(string Name);

    record EmployeeCard(string Name, DepartmentCard Department);

    record DepartmentCard(string Name, bool InSales);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task MembershipOfAnotherSource()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Every employee's department id appears in Department, so all four match.
        var count = await client.Source<Employee>("Employee")
            .CountAsync(_ =>
                client
                    .Source<Department>("Department")
                    .Select(_ => _.Id)
                    .Contains(_.DepartmentId));

        Assert.That(count, Is.EqualTo(4));
    }

    [Test]
    public async Task MembershipNarrowedByAFilterOnTheOtherSource()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientSourceMembership
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => client.Source<Department>("Department")
                .Where(_ => _.Name == "Sales")
                .Select(_ => _.Id)
                .Contains(_.DepartmentId))
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();
        // end-snippet

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Bob", "Carol"]));
    }

    [Test]
    public async Task MembershipIsPolicyFilteredOnTheOtherSource()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Ticket carries [ReturnableWith(OpenTicketsOnlyPolicy)], hiding the closed ticket (Id 3).
        // Membership must not reveal that it exists.
        var open = await client.Source<Employee>("Employee")
            .CountAsync(_ =>
                client
                    .Source<Ticket>("Ticket")
                    .Select(_ => _.Id)
                    .Contains(_.Id));

        // Employees are Ids 1..4; tickets 1 and 2 are open, 3 is not. So only two match, not three.
        Assert.That(open, Is.EqualTo(2));
    }

    [Test]
    public void MembershipAgainstAnUnknownSourceIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new InSourceNode(
                    new MemberNode(["DepartmentId"]),
                    "Secrets",
                    new MemberNode(["Id"]),
                    null))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("Unknown source"));
    }

    [Test]
    public void AnIgnoredMemberStaysHiddenOnTheOtherSource()
    {
        using var context = TestContext.CreateSeeded();

        // Salary is [QueryIgnore]d; the other source's own allow-list applies to the selector.
        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(new InSourceNode(
                    new MemberNode(["Amount"]),
                    "Employee",
                    new MemberNode(["Salary"]),
                    null))
            ]);

        Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public void ANestedMembershipTestIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new InSourceNode(
                        new MemberNode(["DepartmentId"]),
                        "Department",
                        new MemberNode(["Id"]),
                        new InSourceNode(
                            new MemberNode(["Id"]),
                            "Department",
                            new MemberNode(["Id"]),
                            null)))
            ]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("inside another"));
    }

    [Test]
    public void ASubqueryInsideAMembershipTestIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // The filter reads a row of the other source, so a subquery there runs once per row of the set.
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new InSourceNode(
                        new MemberNode(["DepartmentId"]),
                        "Order",
                        new MemberNode(["Id"]),
                        new SubqueryNode(["Lines"], SubqueryFn.Any, null, null)))
            ]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("inside a membership test"));
    }

    [Test]
    public void ASubqueryMayBeTheMembershipValue()
    {
        using var context = TestContext.CreateSeeded();

        // The value reads the row being tested, so a subquery there is one correlated query per row —
        // the same cost it has in any other predicate.
        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(
                    new InSourceNode(
                        new SubqueryNode(["Lines"], SubqueryFn.Count, null, null),
                        "Order",
                        new MemberNode(["Id"]),
                        null)),
                new CountOp()
            ]);

        Assert.DoesNotThrow(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public void AMembershipTestInsideASubqueryInTheValueIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // The one place a subquery may sit inside a membership test is the value; its own expressions
        // are still guarded, so the two cannot be chained through it.
        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(
                    new InSourceNode(
                        new SubqueryNode(
                            ["Lines"],
                            SubqueryFn.Count,
                            new InSourceNode(new MemberNode(["OrderId"]), "Order", new MemberNode(["Id"]), null),
                            null),
                        "Order",
                        new MemberNode(["Id"]),
                        null))
            ]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("inside a subquery"));
    }

    [Test]
    public void AnUnsupportedOperatorOnTheOtherSourceIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => client.Source<Employee>("Employee")
            .CountAsync(_ => client
                .Source<Department>("Department")
                .OrderBy(_ => _.Name)
                .Select(_ => _.Id)
                .Contains(_.DepartmentId)));

        Assert.That(exception!.Message, Does.Contain("Where and a Select"));
    }

    [Test]
    public async Task MembershipInsideANestedProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The tested value reads the row, so it names the navigation the nested projection descends
        // into and is rebased onto it like any other member. The selector reads a Department row
        // instead, so it keeps the path it was written with.
        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeCard(
                _.Name,
                new(_.Department!.Name,
                    client.Source<Department>("Department")
                        .Where(_ => _.Name == "Sales")
                        .Select(_ => _.Name)
                        .Contains(_.Department!.Name))))
            .ToListAsync();

        // Aaron and Alice are in Engineering, Bob and Carol in Sales.
        Assert.That(
            rows.Select(_ => $"{_.Name} {_.Department.Name} {_.Department.InSales}"),
            Is.EqualTo([
                "Aaron Engineering False",
                "Alice Engineering False",
                "Bob Sales True",
                "Carol Sales True"
            ]));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
