/// <summary>
/// The default: at once, then a second, doubling to thirty, and never giving up. A live query that
/// stopped trying would be a page that silently went stale, and the caller who wants that says so by
/// cancelling.
/// </summary>
/// <remarks>
/// Every delay but the first is scattered by a fifth either way. A server that restarts drops every
/// live query it held in the same instant, and without the scatter they would all come back in the
/// same instant too, and again a second later.
/// </remarks>
sealed class BackoffRetryPolicy :
    IScryRetryPolicy
{
    public static BackoffRetryPolicy Instance { get; } = new();

    static TimeSpan longest = TimeSpan.FromSeconds(30);

    public TimeSpan? NextDelay(ScryRetryContext context)
    {
        if (context.PreviousRetryCount == 0)
        {
            return TimeSpan.Zero;
        }

        // Capped before shifting, so a long outage cannot shift its way round to a short delay.
        var doublings = Math.Min(context.PreviousRetryCount - 1, 5);
        var seconds = Math.Min(longest.TotalSeconds, 1 << doublings);
        var scatter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromSeconds(seconds * scatter);
    }
}
