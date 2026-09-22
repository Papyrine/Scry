namespace Scry;

/// <summary>
/// Thrown where a command is refused because the server, or the caller's share of it, already has as
/// many commands in flight as it allows. Nothing ran, so the same command is safe to send again.
/// </summary>
public sealed class ScryCommandLimitException(string message, bool perCaller) :
    Exception(message)
{
    /// <summary>True where the caller's own limit was reached rather than the server's.</summary>
    public bool PerCaller { get; } = perCaller;
}
