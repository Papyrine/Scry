namespace Scry;

/// <summary>Endpoint wiring for serving Scry over a SignalR hub.</summary>
public static class ScrySignalRExtensions
{
    // begin-snippet: mapScryHub
    /// <summary>
    /// Maps <see cref="ScryHub"/> at <paramref name="pattern"/>. Needs <c>AddScry</c> and
    /// <c>AddSignalR</c>, and may be mapped beside <c>MapScry</c> or instead of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs the same startup checks <c>MapScry</c> does, so a host that serves queries over a hub
    /// alone is held to what one serving them over HTTP is. Authorization goes on what this returns,
    /// or on a hub derived from <see cref="ScryHub"/> mapped with the generic overload.
    /// </para>
    /// <para>
    /// Where commands are on, the hub carries writes, and a write over a hub is not guarded the way
    /// one over HTTP is: there is no JSON content type for a cross-site form to be unable to declare,
    /// and a WebSocket handshake is not subject to CORS. A hub that authenticates by cookie therefore
    /// needs that cookie at <c>SameSite=Lax</c> or <c>Strict</c>, or the host to check the handshake's
    /// <c>Origin</c>. One that authenticates by bearer token is not exposed, since a browser never
    /// attaches one on another site's behalf.
    /// </para>
    /// </remarks>
    public static HubEndpointConventionBuilder MapScryHub(this IEndpointRouteBuilder endpoints, string pattern) =>
        endpoints.MapScryHub<ScryHub>(pattern);

    /// <summary>The same, for a hub derived from <see cref="ScryHub"/>.</summary>
    public static HubEndpointConventionBuilder MapScryHub<THub>(this IEndpointRouteBuilder endpoints, string pattern)
        where THub : ScryHub
    {
        endpoints.ServiceProvider
            .GetRequiredService<ScryProcessor>()
            .EnsureReady(endpoints.ServiceProvider);
        return endpoints.MapHub<THub>(pattern);
    }
    // end-snippet
}
