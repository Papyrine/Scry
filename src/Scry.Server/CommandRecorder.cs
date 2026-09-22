/// <summary>
/// Telemetry for commands: an <see cref="Activity"/> per command sent, how long answering it took, how
/// long handling it took, and how many are in flight. Pay-for-play, as a query's is.
/// </summary>
static class CommandRecorder
{
    static string? version = typeof(CommandRecorder).Assembly.GetName().Version?.ToString();

    static ActivitySource activitySource = new(ScryInstrumentation.ActivitySourceName, version);

    static Meter meter = new(ScryInstrumentation.MeterName, version);

    static Histogram<double> duration = meter.CreateHistogram<double>(
        "scry.server.command.duration",
        unit: "s",
        description: "Duration of answering one command: its outcome where it finished inside the sync window, pending where it did not, or its refusal.",
        advice: new()
        {
            HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
        });

    static Histogram<double> handling = meter.CreateHistogram<double>(
        "scry.server.command.handling.duration",
        unit: "s",
        description: "Duration of one command from being accepted to finishing, however and wherever it was handled.",
        advice: new()
        {
            HistogramBucketBoundaries = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300]
        });

    static UpDownCounter<long> pending = meter.CreateUpDownCounter<long>(
        "scry.server.commands.pending",
        unit: "{command}",
        description: "Commands accepted and not yet finished.");

    public static Activity? Start(string command)
    {
        var activity = activitySource.StartActivity($"scry.command {command}");
        activity?.SetTag("scry.command", command);
        return activity;
    }

    /// <summary>
    /// Records how a command was answered — <c>completed</c>, <c>failed</c> and <c>pending</c> for one
    /// that was accepted; <c>malformed</c>, <c>rejected</c>, <c>denied</c>, <c>not_found</c> and
    /// <c>limited</c> for one that was not — and ends its activity.
    /// </summary>
    public static void Answered(Activity? activity, string command, string outcome, TimeSpan elapsed, string? error = null)
    {
        duration.Record(
            elapsed.TotalSeconds,
            new TagList
            {
                { "scry.command", command },
                { "scry.outcome", outcome }
            });
        if (activity is null)
        {
            return;
        }

        activity.SetTag("scry.outcome", outcome);
        if (error is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, error);
        }

        activity.Dispose();
    }

    public static void Handled(string command, CommandStatus status, TimeSpan elapsed) =>
        handling.Record(
            elapsed.TotalSeconds,
            new TagList
            {
                { "scry.command", command },
                { "scry.outcome", status == CommandStatus.Completed ? "completed" : "failed" }
            });

    public static void Accepted() =>
        pending.Add(1);

    public static void Released() =>
        pending.Add(-1);
}
