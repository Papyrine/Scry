namespace Scry;

/// <summary>
/// Something a command did, reported to <see cref="ScryClient.CommandActivity"/> as it happens: sent,
/// refused, pending, asked for again, and how it ended.
/// </summary>
/// <remarks>
/// <para>
/// Raised over every transport, so it is the one place a command sent over a hub connection is
/// observable at all. Raised wherever the command's receipts are being read, which is not necessarily
/// the thread that sent it.
/// </para>
/// <para>
/// A diagnostic: it describes the command rather than driving it, and a handler that throws is
/// swallowed. What a consumer acts on is the <see cref="ScryCommandOutcome"/>.
/// </para>
/// </remarks>
public sealed record ScryCommandActivity
{
    /// <summary>The command's id — the same on every report about it.</summary>
    public required Guid Id { get; init; }

    /// <summary>The command's name on the wire.</summary>
    public required string Command { get; init; }

    /// <summary>What happened.</summary>
    public required ScryCommandActivityKind Kind { get; init; }

    /// <summary>The request as sent.</summary>
    public required CommandRequest Request { get; init; }

    /// <summary>The receipt, on a report one arrived with.</summary>
    public CommandReceipt? Receipt { get; init; }

    /// <summary>
    /// What refused the command, what ended the connection it was being answered on, or why its
    /// outcome is unknown — on the reports that have one.
    /// </summary>
    public Exception? Failure { get; init; }

    /// <summary>Which connection the command is being answered on, counting from one.</summary>
    public required int Attempt { get; init; }
}

/// <summary>What a <see cref="ScryCommandActivity"/> reports.</summary>
public enum ScryCommandActivityKind
{
    /// <summary>About to be sent.</summary>
    Sent,

    /// <summary>
    /// Refused before it was accepted — malformed, denied, its target not there, one too many — so it
    /// never ran. Sending it threw.
    /// </summary>
    Refused,

    /// <summary>Accepted, and answered as still being handled.</summary>
    Pending,

    /// <summary>The connection it was being answered on ended before its outcome, and it is being asked for again.</summary>
    Reattaching,

    /// <summary>Handled.</summary>
    Completed,

    /// <summary>Accepted and then not done.</summary>
    Failed,

    /// <summary>Lost track of: the server no longer holds its outcome.</summary>
    Unknown
}
