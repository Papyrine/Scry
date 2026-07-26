namespace Scry.Client;

/// <summary>
/// The client entry point. Exposes allow-listed sources as <see cref="IQueryable{T}"/> and sends
/// translated queries to the server via a pluggable transport.
/// </summary>
public sealed class ScryClient(Func<QueryRequest, Cancel, Task<QueryResponse>> transport)
{
    // begin-snippet: scryClientApi
    /// <summary>Creates a client that POSTs queries to an HTTP endpoint.</summary>
    public static ScryClient ForHttp(HttpClient http, string endpoint) =>
        new((request, token) => PostAsync(http, endpoint, request, token));

    /// <summary>Returns an <see cref="IQueryable{T}"/> backed by the named allow-listed source.</summary>
    public IQueryable<T> Source<T>(string name) =>
        new CaptureQueryable<T>(new(this, name));
    // end-snippet

    internal Task<QueryResponse> SendAsync(QueryRequest request, Cancel cancel) =>
        transport(request, cancel);

    static async Task<QueryResponse> PostAsync(
        HttpClient http,
        string endpoint,
        QueryRequest request,
        Cancel cancel)
    {
        var json = ScryJson.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var message = await http.PostAsync(endpoint, content, cancel);

        var body = await message.Content.ReadAsStringAsync(cancel);
        if (message.IsSuccessStatusCode)
        {
            return ScryJson.DeserializeResponse(body);
        }

        throw new ScryRequestException((int) message.StatusCode, body);
    }
}
