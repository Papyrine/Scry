namespace Scry.Wire;

/// <summary>
/// Thrown while reading a query result whose payload carries an enum value name this client's
/// generated model does not have — the signature of a client generated before a server-side enum
/// value rename (or removal). Distinct from <see cref="ScryWireException"/>: the wire is well-formed;
/// the client is stale and needs to be regenerated (or, for a deployed app, reloaded).
/// </summary>
public sealed class ScryStaleClientException(string message) :
    Exception(message);
