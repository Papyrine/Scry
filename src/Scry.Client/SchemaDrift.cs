namespace Scry;

/// <summary>
/// Reports that the server's queryable surface no longer matches the one this client was generated
/// against. Raised by <see cref="ScryClient.SchemaStaleDetected"/>.
/// </summary>
/// <param name="ClientStamp">The schema stamp the client was generated with.</param>
/// <param name="ServerStamp">The schema stamp the server advertised.</param>
public sealed record SchemaDrift(string ClientStamp, string ServerStamp);
