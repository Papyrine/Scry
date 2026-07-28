namespace Scry.Wire;

/// <summary>
/// Thrown when a query fails in a way attributable to this client being generated against an older
/// model surface: the server rejected or failed the request and marked the error
/// (<see cref="ScryError.StaleClient"/>), or the result payload carried an enum value name this
/// client's generated model does not have. Distinct from <see cref="ScryWireException"/>: the wire is
/// well-formed; the client is stale. Catch it to prompt a regenerate — or, for a deployed app, a
/// reload.
/// </summary>
public sealed class ScryStaleClientException(string message) :
    Exception(message);
