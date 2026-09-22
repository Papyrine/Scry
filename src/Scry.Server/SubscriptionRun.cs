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
}
