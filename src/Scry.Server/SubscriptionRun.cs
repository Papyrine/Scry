/// <summary>
/// Marks one execution as a live query's, and carries back what it read. Its presence on a
/// <see cref="CallScope"/> is what records the run as a subscription's; what the executor leaves on it
/// is what the subscription listens for until its next run.
/// </summary>
sealed class SubscriptionRun
{
    /// <summary>
    /// The entities the query read, by root name, or null where that could not be told — see
    /// <see cref="DependencyWalker"/>. Null until the query has been built, so a run that was rejected
    /// leaves the subscription listening for everything.
    /// </summary>
    public IReadOnlySet<string>? Dependencies { get; set; }

    /// <summary>
    /// The record of this run's answer, gathered and not yet handed to the sink, where the disclosure
    /// audit is on. Whether the answer is sent is decided after the run, by comparing it with the last
    /// one: sent, this is accepted first; not sent, it is let go, since nothing left.
    /// </summary>
    public DisclosureCapture? Capture { get; set; }

    /// <summary>
    /// The run's recorder, held back with <see cref="Capture"/> so that the run is reported once it is
    /// known whether its answer was recorded — and so an answer the sink refused is reported as the
    /// failure it was.
    /// </summary>
    public QueryRecorder? Recorder { get; set; }

    /// <summary>What the run answered with, for the report held back with it.</summary>
    public ResultKind Kind { get; set; }

    /// <summary>How many rows it answered with.</summary>
    public int? Rows { get; set; }

    /// <summary>The envelope a drifted client was answered with, where that is what the run produced.</summary>
    public QueryResponse? Fallback { get; set; }
}
