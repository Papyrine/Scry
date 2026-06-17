using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using Skry.Wire;

namespace Skry.Client;

/// <summary>
/// The client entry point. Exposes allow-listed sources as <see cref="IQueryable{T}"/> and sends
/// translated queries to the server via a pluggable transport.
/// </summary>
public sealed class SkryClient(Func<QueryRequest, CancellationToken, Task<QueryResponse>> transport)
{
    /// <summary>Creates a client that POSTs queries to an HTTP endpoint.</summary>
    public static SkryClient ForHttp(HttpClient http, string endpoint) =>
        new((request, token) => PostAsync(http, endpoint, request, token));

    /// <summary>Returns an <see cref="IQueryable{T}"/> backed by the named allow-listed source.</summary>
    public IQueryable<T> Source<T>(string name) =>
        new SkryQueryable<T>(new SkryQueryProvider(this, name));

    internal Task<QueryResponse> SendAsync(QueryRequest request, CancellationToken token) =>
        transport(request, token);

    static async Task<QueryResponse> PostAsync(
        HttpClient http,
        string endpoint,
        QueryRequest request,
        CancellationToken token)
    {
        var json = SkryJson.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var message = await http.PostAsync(endpoint, content, token);

        var body = await message.Content.ReadAsStringAsync(token);
        if (!message.IsSuccessStatusCode)
        {
            throw new SkryRequestException((int)message.StatusCode, body);
        }

        return SkryJson.DeserializeResponse(body);
    }
}

/// <summary>Thrown when the server rejects or fails a query.</summary>
public sealed class SkryRequestException(int statusCode, string body) :
    Exception($"Skry query failed ({statusCode}): {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}
