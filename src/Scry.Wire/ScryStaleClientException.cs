namespace Scry;

/// <summary>
/// Thrown when a query fails in a way attributable to this client being generated against an older
/// model surface: the server rejected or failed the request and marked the error
/// (<see cref="ScryError.StaleClient"/>), or a result payload could not be read into this client's
/// generated model while the schema stamp already showed it as drifted. Distinct from
/// <see cref="ScryWireException"/>: the wire is well-formed; the client is stale. Catch it to prompt a
/// regenerate — or, for a deployed app, a reload.
/// </summary>
public sealed class ScryStaleClientException :
    Exception
{
    public ScryStaleClientException(string message) :
        base(message)
    {
    }

    public ScryStaleClientException(string message, Exception inner) :
        base(message, inner)
    {
    }
}
