namespace Scry.Client;

/// <summary>Thrown when the server rejects or fails a query.</summary>
public sealed class ScryRequestException(int statusCode, string body) :
    Exception($"Scry query failed ({statusCode}): {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}
