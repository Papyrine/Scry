namespace Scry;

/// <summary>Adds a query to a <see cref="ScryBatch"/> instead of sending it on its own.</summary>
/// <remarks>
/// Batching is a transport concern, not a query operator: nothing here reaches the wire request, and
/// the server sees each entry exactly as it would have arrived alone — same validation, same row
/// policies, same audit trail. What changes is only how many HTTP requests carry them.
/// </remarks>
public static class ScryBatchExtensions
{
    /// <summary>
    /// Defers this query into <paramref name="batch"/>. Its terminal returns a task that completes when
    /// <see cref="ScryBatch.SendAsync"/> does — so collect the tasks first and await them after sending.
    /// </summary>
    public static IQueryable<T> InBatch<T>(this IQueryable<T> source, ScryBatch batch)
    {
        if (source.Provider is not QueryProvider provider)
        {
            throw new("This IQueryable is not a Scry source.");
        }

        // Checked here rather than only when the terminal runs: a terminal is async, so a throw from
        // inside it surfaces on the awaited task instead of on the line that made the mistake.
        batch.Attaching(provider.Call);

        // The expression is reused untouched, as the header operators do: the translator ignores the
        // root constant, so re-rooting it would change nothing and leave this visible to translation.
        return new CaptureQueryable<T>(provider.With(batch), source.Expression);
    }
}
