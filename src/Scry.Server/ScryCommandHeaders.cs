namespace Scry;

/// <summary>
/// The headers a dispatcher carries a command under, so whatever handles it at the other end can report
/// its outcome, and knows who asked.
/// </summary>
public static class ScryCommandHeaders
{
    /// <summary>The command's id, formatted <c>"D"</c>. Its outcome is reported under this.</summary>
    public const string CommandId = "scry-command-id";

    /// <summary>Who sent the command, as <see cref="ScryOptions.Caller"/> said. Absent for an anonymous caller.</summary>
    public const string Caller = "scry-caller";
}
