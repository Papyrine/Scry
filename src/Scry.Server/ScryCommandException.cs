namespace Scry;

/// <summary>
/// Thrown by a command handler to fail the command with a message the client is shown — "that name is
/// taken", "the order has shipped". Anything else a handler throws fails the command with a fixed
/// message, since an exception's text is written for a developer and may say things a caller must not
/// read.
/// </summary>
/// <remarks>The message is cut to <see cref="MaxMessageLength"/> characters on its way to the client.</remarks>
public sealed class ScryCommandException(string message) :
    Exception(message)
{
    /// <summary>The longest message a client is shown.</summary>
    public const int MaxMessageLength = 1024;
}
