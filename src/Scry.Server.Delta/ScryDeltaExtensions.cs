namespace Scry;

/// <summary>
/// Wires <see href="https://github.com/SimonCropp/Delta">Delta</see> up as the freshness source behind
/// Scry's conditional requests.
/// </summary>
public static class ScryDeltaExtensions
{
    // begin-snippet: useDeltaFreshness
    /// <summary>
    /// Answers a repeated query with <c>304 Not Modified</c> while nothing has been written, by
    /// reading <typeparamref name="TContext"/>'s own change marker through Delta's
    /// <c>GetLastTimeStamp</c> — the transaction log's end position on SQL Server,
    /// <c>pg_last_committed_xact</c> on PostgreSQL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One read of a marker the database already maintains, in place of executing the query and
    /// writing its rows. That trade pays in almost any read-heavy app and does not pay where the data
    /// changes on every request, since the marker moves for a write to anything at all.
    /// </para>
    /// <para>
    /// The marker trails a commit rather than moving with it — a couple of hundred milliseconds on
    /// SQL Server — so inside that window a client that has just written can be told its copy is
    /// still current. A client that needs read-after-write sends <c>Cache-Control: no-cache</c>,
    /// which skips the comparison and re-executes.
    /// </para>
    /// <para>
    /// Where any source carries a row or attachment policy, its rows depend on who asked, and
    /// <see cref="ScryOptions.CacheScope"/> has to say what a cached response belongs to.
    /// <c>MapScry</c> refuses to start otherwise.
    /// </para>
    /// </remarks>
    public static ScryOptions UseDeltaFreshness<TContext>(this ScryOptions options)
        where TContext : DbContext
    {
        options.QueryFreshness = async (context, cancel) =>
        {
            var data = context.RequestServices.GetRequiredService<TContext>();
            var timeStamp = await data.GetLastTimeStamp(cancel);

            // A marker that says nothing identifies nothing, so the request is answered in full rather
            // than with an ETag that has a hole where its freshness should be.
            return timeStamp.Length == 0 ? null : timeStamp;
        };

        return options;
    }
    // end-snippet
}
