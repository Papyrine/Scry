/// <summary>
/// Membership of a set drawn from another source becomes a SQL <c>IN (SELECT …)</c>. The named source
/// is resolved and policy-filtered before the test, so membership is only ever of rows the caller
/// could have queried directly.
/// </summary>
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

        await Assert.That(count).IsEqualTo(4);
    }

    // An optional member tested against required keys. C# only lets this be written with the keys
    // lifted — Select(e => (int?)e.Id) — and the client drops that cast as lifting, so the server
    // meets an int? value against int candidates and has to lift the candidates itself.
    [Test]
    public async Task AnOptionalValueAgainstRequiredCandidates()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Aaron and Bob report to Alice; the other two have no manager, and a null is in no set.
        var count = await client.Source<Employee>("Employee")
            .CountAsync(_ =>
                client
                    .Source<Employee>("Employee")
                    .Select(_ => (int?) _.Id)
                    .Contains(_.ManagerId));

        await Assert.That(count).IsEqualTo(2);
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

        await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(["Bob", "Carol"], CollectionOrdering.Matching);
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
        await Assert.That(open).IsEqualTo(2);
    }

    [Test]
    public async Task MembershipAgainstAnUnknownSourceIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new InSourceNode(
                    new MemberNode(["DepartmentId"]),
                    "Secrets",
                    new MemberNode(["Id"])))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("Unknown source");
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
                    new MemberNode(["Salary"])))
            ]);

        Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public async Task ANestedMembershipTestIsRejected()
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
                            new MemberNode(["Id"]))))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("inside another");
    }

    [Test]
    public async Task ASubqueryInsideAMembershipTestIsRejected()
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
                        new SubqueryNode(["Lines"], SubqueryFn.Any)))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("inside a membership test");
    }

    [Test]
    public async Task ASubqueryMayBeTheMembershipValue()
    {
        using var context = TestContext.CreateSeeded();

        // The value reads the row being tested, so a subquery there is one correlated query per row —
        // the same cost it has in any other predicate.
        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(
                    new InSourceNode(
                        new SubqueryNode(["Lines"], SubqueryFn.Count),
                        "Order",
                        new MemberNode(["Id"]))),
                new CountOp()
            ]);

        await Assert.That(() => SharedProcessor.Instance.Execute(request, context)).ThrowsNothing();
    }

    [Test]
    public async Task AMembershipTestInsideASubqueryInTheValueIsRejected()
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
                            new InSourceNode(new MemberNode(["OrderId"]), "Order", new MemberNode(["Id"]))),
                        "Order",
                        new MemberNode(["Id"])))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("inside a subquery");
    }

    [Test]
    public async Task AnUnsupportedOperatorOnTheOtherSourceIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => client.Source<Employee>("Employee")
            .CountAsync(_ => client
                .Source<Department>("Department")
                .OrderBy(_ => _.Name)
                .Select(_ => _.Id)
                .Contains(_.DepartmentId)));

        await Assert.That(exception!.Message).Contains("Where and a Select");
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
        await Assert.That(rows.Select(_ => $"{_.Name} {_.Department.Name} {_.Department.InSales}")).IsEquivalentTo([
                "Aaron Engineering False",
                "Alice Engineering False",
                "Bob Sales True",
                "Carol Sales True"
            ], CollectionOrdering.Matching);
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
