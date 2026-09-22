namespace Scry;

/// <summary>
/// One command, as the sidecar has seen it: the exchange that sent it, however many times it was asked
/// for again, and what the client said about it. Keyed by the command's id, so the exchange and the
/// reports about it are one row.
/// </summary>
/// <remarks>
/// Written from two places, like <see cref="ScrySidecarSession"/>: the exchanges come off the wire,
/// through <see cref="ScrySidecarHandler"/>, and the states from the client itself, through
/// <see cref="ScryClient.CommandActivity"/>, where the app wired it with
/// <see cref="ScrySidecarStore.Observe"/>. A command answered as a stream of receipts is recorded to its
/// headers only, so without the client's reports its row stops at pending.
/// </remarks>
public sealed class ScrySidecarCommand
{
    internal ScrySidecarCommand(Guid id, string name)
    {
        Id = id;
        Name = name;
        Updated = DateTimeOffset.Now;
    }

    /// <summary>The id the client gave the command.</summary>
    public Guid Id { get; }

    /// <summary>The command's name on the wire.</summary>
    public string Name { get; }

    /// <summary>Where the command is, as last seen or reported.</summary>
    public ScryCommandActivityKind State { get; private set; } = ScryCommandActivityKind.Sent;

    /// <summary>Why it was refused or failed, or why its outcome is unknown.</summary>
    public string? Error { get; private set; }

    /// <summary>What it answered with, pretty-printed, for a completed command that has a result.</summary>
    public string? ResultJson { get; private set; }

    /// <summary>Which connection it is being answered on, counting from one.</summary>
    public int Attempt { get; private set; } = 1;

    /// <summary>
    /// Whether this sidecar saw the command's own exchange. False for one sent somewhere it cannot
    /// watch — a hub connection, or a transport of the app's own — where what is known is what the
    /// client reported.
    /// </summary>
    public bool OnTheWire { get; internal set; }

    /// <summary>When anything about it was last seen.</summary>
    public DateTimeOffset Updated { get; private set; }

    internal void Report(ScryCommandActivity activity)
    {
        State = activity.Kind;
        Attempt = Math.Max(Attempt, activity.Attempt);
        if (activity.Receipt is { } receipt)
        {
            Receipt(receipt);
        }
        else if (activity.Failure is { } failure)
        {
            Error = failure.Message;
        }

        Updated = DateTimeOffset.Now;
    }

    internal void Receipt(CommandReceipt receipt)
    {
        State = receipt.Status switch
        {
            CommandStatus.Completed => ScryCommandActivityKind.Completed,
            CommandStatus.Failed => ScryCommandActivityKind.Failed,
            _ => ScryCommandActivityKind.Pending
        };
        Error = receipt.Error;
        if (receipt.Result is { } result)
        {
            ResultJson = SidecarJson.Prettify(result.GetRawText());
        }

        Updated = DateTimeOffset.Now;
    }

    internal void Saw(ScryCommandActivityKind state, string? error)
    {
        State = state;
        Error = error;
        Updated = DateTimeOffset.Now;
    }

    internal void AskedAgain()
    {
        Attempt++;
        Updated = DateTimeOffset.Now;
    }
}
