namespace Scry;

/// <summary>Thrown when the server rejects or fails a query.</summary>
public sealed class ScryRequestException(HttpStatusCode statusCode, string body) :
    Exception($"Scry query failed ({statusCode}): {body}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}
