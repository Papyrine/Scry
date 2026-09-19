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
            if (timeStamp.Length == 0)
            {
                return null;
            }

            return timeStamp;
        };

        return options;
    }
    // end-snippet

    // begin-snippet: useDeltaChanges
    /// <summary>
    /// Runs every live query again when anything is written to <typeparamref name="TContext"/>'s
    /// database, by watching the same change marker <see cref="UseDeltaFreshness{TContext}"/> reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marker moves for every writer there is — another node, another system, a bulk update, raw
    /// SQL — none of which has to know Scry exists. That makes the database the backplane: a
    /// deployment of several nodes needs nothing else for a write on one to reach the live queries
    /// held by the others.
    /// </para>
    /// <para>
    /// What it cannot say is what changed, so every live query is run again rather than the ones that
    /// read what was written. A run that finds its answer unchanged sends nothing, so the cost is
    /// queries and not traffic — and it is paid only while a live query is open, since nothing probes
    /// otherwise. Use it beside <see cref="ScryChangeInterceptor"/>, which does know what changed and
    /// reports it at once: the interceptor makes this node's own writes fast and precise, and this
    /// catches everything the interceptor cannot see.
    /// </para>
    /// <para>
    /// The marker trails a commit by a couple of hundred milliseconds on SQL Server, and is asked
    /// every <see cref="ScryOptions.ChangeProbeInterval"/>, so that is how far behind a write this
    /// alone can be.
    /// </para>
    /// </remarks>
    public static ScryOptions UseDeltaChanges<TContext>(this ScryOptions options)
        where TContext : DbContext
    {
        options.ChangeProbe = async (services, cancel) =>
        {
            var data = services.GetRequiredService<TContext>();
            var timeStamp = await data.GetLastTimeStamp(cancel);

            // A marker that says nothing is not one to compare against: the probe is skipped this
            // once rather than read as the database having moved.
            if (timeStamp.Length == 0)
            {
                return null;
            }

            return timeStamp;
        };

        return options;
    }
    // end-snippet
}
