/// <summary>
/// The per-query header hooks a captured query carries: what to write onto the outgoing request, and
/// what to read off the response that comes back.
/// </summary>
/// <remarks>
/// Immutable, and composed rather than accumulated into a bag. <see cref="HttpRequestHeaders"/> has no
/// public constructor, so there is nothing to write into until the request message exists — deferring
/// the write until then also means the caller works against the real header API rather than a mapped
/// subset of it. Both hooks are plain multicast delegates, so composing two of them is <c>+</c>.
/// </remarks>
sealed class ScryCall(
    Action<HttpRequestHeaders>? configureRequest,
    Action<HttpResponseHeaders>? readResponse)
{
    static readonly ScryCall empty = new(null, null);

    Action<HttpRequestHeaders>? ConfigureRequest { get; } = configureRequest;

    Action<HttpResponseHeaders>? ReadResponse { get; } = readResponse;

    /// <summary>Adds a hook that writes onto the outgoing request's headers.</summary>
    public static ScryCall Configuring(ScryCall? call, Action<HttpRequestHeaders> configure)
    {
        var scryCall = call ?? empty;
        return new(scryCall.ConfigureRequest + configure, scryCall.ReadResponse);
    }

    /// <summary>Adds a hook that reads the response's headers.</summary>
    public static ScryCall Reading(ScryCall? call, Action<HttpResponseHeaders> read)
    {
        var scryCall = call ?? empty;
        return new(scryCall.ConfigureRequest, scryCall.ReadResponse + read);
    }

    public void Configure(HttpRequestHeaders headers) =>
        ConfigureRequest?.Invoke(headers);

    public void Read(HttpResponseHeaders headers) =>
        ReadResponse?.Invoke(headers);
}
