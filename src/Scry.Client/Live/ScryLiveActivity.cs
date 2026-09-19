namespace Scry;

/// <summary>
/// Something a live query did, reported to <see cref="ScryClient.LiveActivity"/> as it happens. One
/// <see cref="Session"/> is one live query from the consumer's point of view, across however many
/// connections it took to keep it answered.
/// </summary>
/// <remarks>
/// <para>
/// Reported from wherever the live query's pump is running, which is not the thread that started it
/// and not a UI thread. A handler that touches what a synchronization context owns marshals itself.
/// </para>
/// <para>
/// This is a diagnostic: it describes the live query rather than driving it, and a handler that
/// throws is swallowed. What a consumer acts on is the answers, or
/// <see cref="ScrySubscription.State"/> for the same states delivered where its callbacks are.
/// </para>
/// </remarks>
public sealed record ScryLiveActivity
{
    /// <summary>
    /// Identifies the live query, for the life of the process. Every report from one live query
    /// carries the same value, including the ones from connections it made after the first.
    /// </summary>
    public required long Session { get; init; }

    /// <summary>The request being held open — the one the same query asked once would send.</summary>
    public required QueryRequest Request { get; init; }

    /// <summary>Where the live query is now.</summary>
    public required ScrySubscriptionState State { get; init; }

    /// <summary>
    /// The answer, on the report that delivers one. Null on every other report, including the
    /// <see cref="ScrySubscriptionState.Live"/> one that says a connection is answering again.
    /// </summary>
    public QueryResponse? Answer { get; init; }

    /// <summary>
    /// What ended the connection, on a <see cref="ScrySubscriptionState.Reconnecting"/> or
    /// <see cref="ScrySubscriptionState.Faulted"/> report. Null where the server ended the stream
    /// rather than failed it.
    /// </summary>
    public Exception? Failure { get; init; }

    /// <summary>
    /// Which connection this is, counting from one. It rises each time the live query is asked
    /// again, so it is also how many times reconnecting has been needed.
    /// </summary>
    public required int Attempt { get; init; }
}
