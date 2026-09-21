namespace Scry;

/// <summary>
/// Decides how long a live query waits before asking again after its connection ended, and whether
/// it asks at all. Set on <see cref="ScryClient.Reconnect"/>.
/// </summary>
/// <remarks>
/// Consulted only for an ending worth asking again after: a connection that was cut or refused to
/// open, a server that failed or was busy, a stream the server ended to bound its own lifetime. A
/// request the server rejected, denied, or attributed to a stale client ends the live query whatever
/// this says — asking again would be asking for the same answer.
/// </remarks>
public interface IScryRetryPolicy
{
    /// <summary>
    /// How long to wait before the next attempt, or null to give up — which ends the live query with
    /// the failure that was being retried.
    /// </summary>
    TimeSpan? NextDelay(ScryRetryContext context);
}
