namespace Scry;

/// <summary>
/// One server-sent event read off a live query's connection, as the sidecar saw it go past.
/// </summary>
/// <remarks>
/// Recorded for every event the server writes, the heartbeats included — an idle live query that is
/// still being pinged looks exactly like a hung one otherwise. The names are
/// <see cref="ScryLive.Result"/> and the constants beside it.
/// </remarks>
public sealed record ScrySidecarEvent
{
    public required DateTimeOffset At { get; init; }

    /// <summary>The event's name: <c>result</c>, <c>unchanged</c>, <c>ping</c>, <c>error</c>, <c>end</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// What the server named this answer, which is what the next connection sends back as
    /// <see cref="ScryLive.LastEventIdHeader"/>. Only a <see cref="ScryLive.Result"/> carries one.
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>The size of the event's data, whether or not the data itself was kept.</summary>
    public required int Bytes { get; init; }

    /// <summary>
    /// The data pretty-printed, for the events recent and small enough to have been kept —
    /// see <see cref="ScrySidecarOptions.MaxRetainedAnswers"/> and
    /// <see cref="ScrySidecarOptions.MaxRetainedAnswerBytes"/>. Null where only the size is known.
    /// </summary>
    public string? Json { get; init; }
}
