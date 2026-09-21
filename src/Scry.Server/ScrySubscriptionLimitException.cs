namespace Scry;

/// <summary>
/// Thrown when a live query is refused because the server, or the caller's share of it, is already
/// holding as many as it allows. Nothing about the query was wrong and nothing of it ran, so the same
/// request is worth sending again later. The HTTP endpoint answers <c>503</c> for the server's limit
/// and <c>429</c> for the caller's, both as <see cref="ScryErrorCode.SubscriptionLimit"/>.
/// </summary>
public sealed class ScrySubscriptionLimitException(string message, bool perCaller) :
    Exception(message)
{
    /// <summary>
    /// Whether the limit reached was this caller's own (<c>ScryOptions.MaxSubscriptionsPerCaller</c>)
    /// rather than the server's (<c>ScryOptions.MaxSubscriptions</c>).
    /// </summary>
    public bool PerCaller { get; } = perCaller;
}
