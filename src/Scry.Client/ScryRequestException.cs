namespace Scry;

/// <summary>Thrown when the server rejects or fails a query.</summary>
/// <remarks>
/// The failures whose remedy is specific get types of their own —
/// <see cref="ScryStaleClientException"/> and <see cref="ScryPermissionException"/> — so this is what
/// is left: a rejection, a malformed request, a refused media type, or an execution failure.
/// <see cref="Code"/> says which, so code that retries (or declines to) branches on a closed set
/// rather than on <see cref="StatusCode"/>, which answers two questions at once.
/// </remarks>
public sealed class ScryRequestException(HttpStatusCode statusCode, ScryErrorCode code, string body) :
    Exception($"Scry query failed ({statusCode}): {body}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>
    /// Which of the endpoint's answers this is, or <see cref="ScryErrorCode.Unknown"/> where the body
    /// carried no code — a response from a proxy rather than from the endpoint.
    /// </summary>
    public ScryErrorCode Code { get; } = code;

    public string Body { get; } = body;
}
