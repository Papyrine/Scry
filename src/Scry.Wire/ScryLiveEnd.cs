namespace Scry;

/// <summary>
/// The data of a <see cref="ScryLive.End"/> event: the server closed a live query that had not failed.
/// </summary>
/// <param name="Reconnect">
/// Whether asking again is expected to succeed. True where the server ended the stream to bound its
/// own state — a lifetime reached, an authentication ticket expired, a shutdown — and a client that
/// reconnects is re-authenticated by doing so, which is the point of ending it.
/// </param>
public sealed record ScryLiveEnd(bool Reconnect)
{
    /// <summary>Why the stream ended. For a log rather than for a branch.</summary>
    public string? Reason { get; init; }
}
