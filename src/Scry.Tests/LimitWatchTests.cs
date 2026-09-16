/// <summary>
/// What a limit that only rejects cannot say: how close the traffic it accepts runs to it. With
/// <see cref="ScryOptions.LimitWatchFraction" /> set, a query that came within the fraction is
/// reported to an auditor and still answered, which is what makes tightening a limit something other
/// than a guess.
/// </summary>
[TestFixture]
public class LimitWatchTests
{
    [Test]
    public async Task ReportsALimitAQueryApproached()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        Watching(0.5).Execute(Take(6), context, provider);

        await Verify(auditor.Entries.Single().ApproachedLimits)
            .Snapshot(
                """
                [
                  {
                    Limit: MaxPageSize,
                    Used: 6,
                    Maximum: 10
                  }
                ]
                """);
    }

    [Test]
    public async Task ReportsNothingUnderTheFraction()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        Watching(0.5).Execute(Take(4), context, provider);

        Assert.That(auditor.Entries.Single().ApproachedLimits, Is.Null);
    }

    [Test]
    public async Task ReportsNothingWhileUnset()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        SharedProcessor.Instance.Execute(Take(6), context, provider);

        Assert.That(auditor.Entries.Single().ApproachedLimits, Is.Null);
    }

    // A rejected query broke a limit rather than approached one, and measuring it would mean walking
    // a request the gate has already refused.
    [Test]
    public async Task ARejectedQueryIsNotMeasured()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);
        var processor = Watching(0.5);

        await using var context = TestContext.CreateSeeded();
        Assert.Throws<ScryValidationException>(() => processor.Execute(Take(50), context, provider));

        var entry = auditor.Entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Outcome, Is.EqualTo(ScryQueryOutcome.Rejected));
            Assert.That(entry.ApproachedLimits, Is.Null);
        });
    }

    // Nothing is measured until something is there to read it, so the walk is not paid for by a
    // deployment that registered no auditor.
    [Test]
    public async Task NothingIsMeasuredWithoutAnAuditor()
    {
        await using var context = TestContext.CreateSeeded();

        Assert.DoesNotThrow(() => Watching(0.5).Execute(Take(6), context));
    }

    // Each of the counts below mirrors a rule the validator applies as it walks, so both sides are
    // pinned to the same number: a request at the limit is reported, and the same request one past it
    // is refused. A mirror that drifted low would stop reporting the first; one that drifted high
    // would see the first refused.

    [Test]
    public async Task ReportsAnInSetAtItsLimit()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        Watching(1, _ => _.MaxInValues = 3).Execute(In(3), context, provider);

        await Verify(auditor.Entries.Single().ApproachedLimits)
            .Snapshot(
                """
                [
                  {
                    Limit: MaxInValues,
                    Used: 3,
                    Maximum: 3
                  }
                ]
                """);
    }

    [Test]
    public async Task RefusesAnInSetPastTheSameLimit()
    {
        var processor = Watching(1, _ => _.MaxInValues = 3);

        await using var context = TestContext.CreateSeeded();
        Assert.Throws<ScryValidationException>(() => processor.Execute(In(4), context));
    }

    [Test]
    public async Task ReportsANavigationPathAtItsLimit()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        Watching(1, _ => _.MaxNavigationDepth = 2).Execute(Path(["Manager", "Name"]), context, provider);

        await Verify(auditor.Entries.Single().ApproachedLimits)
            .Snapshot(
                """
                [
                  {
                    Limit: MaxNavigationDepth,
                    Used: 2,
                    Maximum: 2
                  }
                ]
                """);
    }

    [Test]
    public async Task RefusesANavigationPathPastTheSameLimit()
    {
        var processor = Watching(1, _ => _.MaxNavigationDepth = 2);

        await using var context = TestContext.CreateSeeded();
        Assert.Throws<ScryValidationException>(
            () => processor.Execute(Path(["Manager", "Manager", "Name"]), context));
    }

    [Test]
    public async Task ReportsAProjectionAtItsLimit()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        Watching(1, _ => _.MaxProjectionMembers = 2).Execute(Select(2), context, provider);

        await Verify(auditor.Entries.Single().ApproachedLimits)
            .Snapshot(
                """
                [
                  {
                    Limit: MaxProjectionMembers,
                    Used: 2,
                    Maximum: 2
                  }
                ]
                """);
    }

    [Test]
    public async Task RefusesAProjectionPastTheSameLimit()
    {
        var processor = Watching(1, _ => _.MaxProjectionMembers = 2);

        await using var context = TestContext.CreateSeeded();
        Assert.Throws<ScryValidationException>(() => processor.Execute(Select(3), context));
    }

    [TestCase(0d)]
    [TestCase(1.5d)]
    [TestCase(-0.5d)]
    public void TheFractionIsCheckedAtStartup(double fraction)
    {
        var exception = Assert.Throws<Exception>(() => Watching(fraction));

        Assert.That(exception!.Message, Does.Contain("LimitWatchFraction"));
    }

    [Test]
    public void OneIsAllowed() =>
        Assert.DoesNotThrow(() => Watching(1));

    static ScryProcessor Watching(double fraction, Action<ScryOptions>? configure = null) =>
        ScryProcessor.Create<TestContext>(
            options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                options.MaxPageSize = 10;
                options.LimitWatchFraction = fraction;
                configure?.Invoke(options);
            });

    static QueryRequest In(int values)
    {
        var arguments = new List<Node>();
        for (var index = 0; index < values; index++)
        {
            arguments.Add(new ConstNode(index.ToString(), ClrTypeTag.Int32));
        }

        return QueryRequest.Create(
            "Employee",
            [new WhereOp(new CallNode(KnownFunction.In, new MemberNode(["Id"]), arguments))]);
    }

    static QueryRequest Path(string[] path) =>
        QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(BinaryOp.Equal, new MemberNode(path), new ConstNode("Ann", ClrTypeTag.String)))
            ]);

    static QueryRequest Select(int members)
    {
        string[] names = ["Name", "Id", "Active"];
        var projected = new List<ProjectionMember>();
        for (var index = 0; index < members; index++)
        {
            projected.Add(new(names[index], new NodeValue(new MemberNode([names[index]]))));
        }

        return QueryRequest.Create("Employee", [new SelectOp(new(projected))]);
    }

    // Ordered, because a Take over an unordered query is refused before any of this is reached
    static QueryRequest Take(int count) =>
        QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new TakeOp(count)
            ]);

    static ServiceProvider Services(RecordingAuditor auditor)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScryAuditor>(auditor);
        return services.BuildServiceProvider();
    }

    sealed class RecordingAuditor :
        IScryAuditor
    {
        public List<ScryAuditEntry> Entries { get; } = [];

        public void Record(ScryAuditEntry entry) =>
            Entries.Add(entry);
    }
}
