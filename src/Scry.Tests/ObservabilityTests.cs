using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

[TestFixture]
public class ObservabilityTests
{
    [Test]
    public async Task ActivityForSuccess()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);

        using var context = TestContext.CreateSeeded();
        SharedProcessor.Instance.Execute(EmployeeNames(), context);

        var activity = stopped.Single();
        await Verify(new
        {
            activity.DisplayName,
            activity.Status,
            Tags = activity.TagObjects.ToDictionary(_ => _.Key, _ => _.Value)
        });
    }

    [Test]
    public async Task ActivityForUnknownSource()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);

        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Missing", [new CountOp()]);
        Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        // The root is not in the schema, so the tag carries a placeholder rather than an
        // attacker-controlled string.
        var activity = stopped.Single();
        await Verify(new
        {
            activity.DisplayName,
            activity.Status,
            activity.StatusDescription,
            Tags = activity.TagObjects.ToDictionary(_ => _.Key, _ => _.Value)
        });
    }

    [Test]
    public async Task MetricsForSuccess()
    {
        var measurements = new List<(string Instrument, object Value, Dictionary<string, object?> Tags)>();
        using var listener = ListenMeters(measurements);

        using var context = TestContext.CreateSeeded();
        SharedProcessor.Instance.Execute(EmployeeNames(), context);

        // The duration value is wall-clock and scrubbed; the instruments, tags, and row count are the
        // stable part worth pinning.
        await Verify(measurements.Select(_ => new
        {
            _.Instrument,
            Value = _.Instrument == "scry.server.query.rows" ? _.Value : "{scrubbed}",
            _.Tags
        }));
    }

    [Test]
    public async Task AuditForSuccess()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        using var context = TestContext.CreateSeeded();
        SharedProcessor.Instance.Execute(EmployeeNames(), context, provider);

        var entry = auditor.Entries.Single();
        Assert.That(entry.Duration, Is.GreaterThan(TimeSpan.Zero));
        await VerifyEntry(entry);
    }

    [Test]
    public async Task AuditForPage()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new PageOp(Size: 2)
            ]);
        SharedProcessor.Instance.Execute(request, context, provider);

        await VerifyEntry(auditor.Entries.Single());
    }

    [Test]
    public async Task AuditForRejected()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [new WhereOp(new MemberNode(["Salary"]))]);
        Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context, provider));

        await VerifyEntry(auditor.Entries.Single());
    }

    [Test]
    public async Task AuditForFailed()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        // A policy that faults at query-build time: validation passes, execution throws — the path a
        // provider failure takes. The audit entry carries the real message; a client sees a generic 500.
        var processor = ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AddPolicy<Employee, ThrowingPolicy>();
        });

        using var context = TestContext.CreateSeeded();
        // The reflection invoke of the policy wraps the failure; the audit entry unwraps it, so the
        // trail names the root cause rather than the wrapper.
        var thrown = Assert.Throws<TargetInvocationException>(() => processor.Execute(EmployeeNames(), context, provider))!;
        Assert.That(thrown.InnerException, Is.TypeOf<InvalidOperationException>());

        await VerifyEntry(auditor.Entries.Single());
    }

    [Test]
    public async Task AuditForStream()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        var (_, rows) = SharedProcessor.Instance.Stream(EmployeeNames(), context, provider);
        var streamed = new List<Dictionary<string, object?>>();
        await foreach (var row in rows)
        {
            streamed.Add(row);
        }

        var entry = auditor.Entries.Single();
        Assert.That(entry.Rows, Is.EqualTo(streamed.Count));
        await VerifyEntry(entry);
    }

    [Test]
    public async Task AuditForAbandonedStream()
    {
        var auditor = new RecordingAuditor();
        await using var provider = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        var (_, rows) = SharedProcessor.Instance.Stream(EmployeeNames(), context, provider);
        await foreach (var _ in rows)
        {
            break;
        }

        await VerifyEntry(auditor.Entries.Single());
    }

    static QueryRequest EmployeeNames() =>
        QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new MemberNode(["Active"])),
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

    static Task VerifyEntry(ScryAuditEntry entry) =>
        Verify(entry)
            .IgnoreMember<ScryAuditEntry>(_ => _.Duration);

    static ActivityListener Listen(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => _.Name == ScryInstrumentation.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    static MeterListener ListenMeters(List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ScryInstrumentation.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, ToDictionary(tags))));
        listener.Start();
        return listener;
    }

    static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }

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

    sealed class ThrowingPolicy :
        IReturnablePolicy<Employee>
    {
        public IQueryable<Employee> Filter(IQueryable<Employee> source, ScryPolicyContext context) =>
            throw new InvalidOperationException("The policy faulted.");
    }
}
