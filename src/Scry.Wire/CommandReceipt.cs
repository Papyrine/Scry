namespace Scry;

/// <summary>
/// What a server says about a command: its id, where it is, and — once it has finished — its result or
/// why it failed. The answer to a command decided within the server's sync window, and each event of the
/// stream that answers one that is not.
/// </summary>
/// <remarks>
/// The optional members are init properties rather than positional ones: the writer omits them when
/// null, and a positional member is one the reader requires.
/// </remarks>
// begin-snippet: wireCommandReceipt
public sealed record CommandReceipt(int Version, Guid Id, CommandStatus Status)
{
    /// <summary>Creates a receipt stamped with <see cref="CommandRequest.CurrentVersion"/>.</summary>
    public static CommandReceipt Create(Guid id, CommandStatus status) =>
        new(CommandRequest.CurrentVersion, id, status);

    /// <summary>What the handler answered with, for a completed command that has a result.</summary>
    public JsonElement? Result { get; init; }

    /// <summary>
    /// Why a failed command failed: the message a handler chose to show, or a fixed one. Nothing
    /// internal leaves the server this way.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>The server's schema stamp, as every response carries it.</summary>
    public string? Stamp { get; init; }
}
// end-snippet
