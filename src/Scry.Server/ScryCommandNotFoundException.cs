namespace Scry;

/// <summary>
/// Thrown where what a command names is not there for its caller: the row a targeted command acts on —
/// absent, hidden by a policy, or denied by the command's own, all answered alike — or, asking for a
/// command again by its id, a command this server does not hold for this caller.
/// </summary>
/// <remarks>
/// One answer for all of them on purpose. A row the caller may not see answering differently from one
/// that does not exist would say that it exists.
/// </remarks>
public sealed class ScryCommandNotFoundException(string message) :
    Exception(message)
{
    /// <summary>The message for a target that is not there for this caller.</summary>
    public const string TargetMessage = "The command's target was not found.";

    /// <summary>The message for a command this server does not hold for this caller.</summary>
    public const string CommandMessage = "The command is not one this server holds.";
}
