[TestFixture]
public class SqlPreviewTests
{
    [Test]
    public Task SqlForFilteredProjection()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new MemberNode(["Active"])),
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(
                    new(
                    [
                        new("Name", new NodeValue(new MemberNode(["Name"]))),
                        new("Department", new NodeValue(new MemberNode(["Department", "Name"])))
                    ]))
            ]);

        using var context = TestContext.CreateSeeded();
        return Verify(SharedProcessor.Instance.ToQueryString(request, context, EmptyServices.Instance));
    }

    [Test]
    public void APolicyIsInTheSql()
    {
        // The preview goes through the same build a query does, so a row policy is part of the SQL it
        // shows. That is the whole reason the explorer keeps this behind a guard of its own: the SQL
        // discloses the shape of the filter, not just its effect.
        var processor = Processor(_ => _.AddPolicy<Employee, ActiveOnlyPolicy>());
        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))]);

        using var context = TestContext.CreateSeeded();
        var sql = processor.ToQueryString(request, context, EmptyServices.Instance);

        Assert.That(sql, Does.Contain("Active"));
    }

    [Test]
    public void ATerminalIsRefused()
    {
        // A terminal is answered by running the query, so there is no SQL to show without executing
        // one — refused rather than run, and refused before the executor reaches the terminal.
        var request = QueryRequest.Create("Employee", [new CountOp()]);

        using var context = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.ToQueryString(request, context, EmptyServices.Instance))!;

        Assert.That(exception.Message, Does.Contain("ends in Count"));
    }

    [Test]
    public void APageIsRefused()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new PageOp(Size: 2)
            ]);

        using var context = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.ToQueryString(request, context, EmptyServices.Instance))!;

        Assert.That(exception.Message, Does.Contain("ends in Page"));
    }

    [Test]
    public void APocoSourceHasNoSql()
    {
        // Holiday is a [QueryablePoco] — rows supplied in memory, so there is no database to have SQL.
        var request = QueryRequest.Create(
            "Holiday",
            [new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))]);

        using var context = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.ToQueryString(request, context, EmptyServices.Instance))!;

        Assert.That(exception.Message, Does.Contain("not backed by the database"));
    }

    [Test]
    public void AnUnallowedMemberIsStillRejected()
    {
        // Validation runs first here exactly as it does for a query: previewing is not a way to reach
        // a member a query could not.
        var request = QueryRequest.Create("Employee", [new WhereOp(new MemberNode(["Salary"]))]);

        using var context = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.ToQueryString(request, context, EmptyServices.Instance))!;

        Assert.That(exception.Message, Does.Contain("not allow-listed"));
    }

    static ScryProcessor Processor(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(
            options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                extra(options);
            });

    sealed class EmptyServices :
        IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
