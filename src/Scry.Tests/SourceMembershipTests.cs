/// <summary>
/// Membership of a set drawn from another source becomes a SQL <c>IN (SELECT …)</c>. The named source
/// is resolved and policy-filtered before the test, so membership is only ever of rows the caller
/// could have queried directly.
/// </summary>
[TestFixture]
public class SourceMembershipTests
{
    // ReSharper disable once NotAccessedPositionalProperty.Local
    record NameRow(string Name);

    [Test]
    public async Task MembershipOfAnotherSource()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Every employee's department id appears in Department, so all four match.
        var count = await client.Source<Employee>("Employee")
            .CountAsync(_ => client.Source<Department>("Department").Select(d => d.Id).Contains(_.DepartmentId));

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
                .Where(d => d.Name == "Sales")
                .Select(d => d.Id)
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
            .CountAsync(_ => client.Source<Ticket>("Ticket").Select(t => t.Id).Contains(_.Id));

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
                new WhereOp(new InSourceNode(
                    new MemberNode(["DepartmentId"]),
                    "Department",
                    new MemberNode(["Id"]),
                    new InSourceNode(
                        new MemberNode(["Id"]),
                        "Department",
                        new MemberNode(["Id"]),
                        null)))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("inside another"));
    }

    [Test]
    public void AnUnsupportedOperatorOnTheOtherSourceIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(
            () => client.Source<Employee>("Employee")
                .CountAsync(_ => client.Source<Department>("Department")
                    .OrderBy(d => d.Name)
                    .Select(d => d.Id)
                    .Contains(_.DepartmentId)));

        Assert.That(exception!.Message, Does.Contain("Where and a Select"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
