namespace Scry;

/// <summary>
/// A command a client sends: which one, the id the client gave it, and its payload. Answered with a
/// <see cref="CommandReceipt"/>, straight away where the command finishes quickly and as a stream of
/// them where it does not.
/// </summary>
/// <remarks>
/// The payload is the command's properties as JSON, bound on the server into the server's own command
/// type through the allow-listed properties alone — never the type the client says it sent.
/// </remarks>
// begin-snippet: wireCommandRequest
public sealed record CommandRequest(int Version, string Command, Guid Id, JsonElement Payload)
{
    /// <summary>The current command request version. Versioned apart from the query wire, which this does not touch.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Creates a request stamped with <see cref="CurrentVersion"/>.</summary>
    public static CommandRequest Create(string command, Guid id, JsonElement payload, string? stamp = null) =>
        new(CurrentVersion, command, id, payload)
        {
            Stamp = stamp
        };

    /// <summary>
    /// The schema stamp of the generated client model the command came from, when known. Read for the
    /// same reason <see cref="QueryRequest.Stamp"/> is — to attribute a rejection to a stale client —
    /// and never as an authorization input.
    /// </summary>
    public string? Stamp { get; init; }
}
// end-snippet
