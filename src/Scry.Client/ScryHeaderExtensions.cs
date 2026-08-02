namespace Scry;

/// <summary>
/// Attaches HTTP headers to a single query, and reads the headers of the response it returns.
/// </summary>
/// <remarks>
/// These are transport concerns, not query operators: nothing here reaches the wire request, and the
/// server is never told a header hook existed. They are the per-query counterpart to
/// <c>HttpClient.DefaultRequestHeaders</c>, which applies to every call a client makes.
/// <para>
/// Headers are carried by the HTTP transport, so a query carrying them must come from a client built by
/// <see cref="ScryClient.ForHttp"/>; a custom transport delegate has nowhere to put them and says so
/// rather than dropping them. A header attached to the inner side of a join or a set operator is
/// likewise ignored — those are folded into the one request the outer query sends.
/// </para>
/// <para>
/// Whatever a client sends is attacker-controlled by the time the server reads it. Treat these as
/// hint data (a correlation id, a trace id, a client build) and never as an authorization input.
/// </para>
/// </remarks>
public static class ScryHeaderExtensions
{
    // begin-snippet: queryHeaders
    /// <summary>Sends <paramref name="name"/>: <paramref name="value"/> with this query's request.</summary>
    public static IQueryable<T> WithHeader<T>(this IQueryable<T> source, string name, string value) =>
        source.WithHeaders(_ => _.TryAddWithoutValidation(name, value));

    /// <summary>Configures the headers of this query's request.</summary>
    public static IQueryable<T> WithHeaders<T>(this IQueryable<T> source, Action<HttpRequestHeaders> configure) =>
        Rebind(source, _ => ScryCall.Configuring(_, configure));

    /// <summary>
    /// Reads the headers of this query's response, including when the query fails — a trace or
    /// correlation header is most useful on the response that went wrong.
    /// </summary>
    public static IQueryable<T> OnResponseHeaders<T>(this IQueryable<T> source, Action<HttpResponseHeaders> read) =>
        Rebind(source, _ => ScryCall.Reading(_, read));
    // end-snippet

    /// <summary>
    /// Returns the same captured query against a provider carrying the added hook. The expression is
    /// reused untouched: the translator ignores the root constant, so re-rooting it is unnecessary, and
    /// leaving it alone keeps these operators invisible to translation.
    /// </summary>
    static IQueryable<T> Rebind<T>(IQueryable<T> source, Func<ScryCall?, ScryCall> add)
    {
        if (source.Provider is not QueryProvider provider)
        {
            throw new("This IQueryable is not a Scry source.");
        }

        return new CaptureQueryable<T>(provider.With(add(provider.Call)), source.Expression);
    }
}
