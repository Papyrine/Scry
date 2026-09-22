namespace Scry;

/// <summary>
/// Where commands are sent, under the pattern <c>MapScry</c> was given. A command that has not finished
/// within the server's sync window is answered as server-sent events in the framing
/// <see cref="ScryLive"/> describes, each <see cref="ScryLive.Result"/> carrying a
/// <see cref="CommandReceipt"/> — <see cref="CommandStatus.Pending"/> first, then the final one, after
/// which the server closes the stream.
/// </summary>
/// <remarks>
/// A stream that closes before a final receipt with neither <see cref="ScryLive.Error"/> nor
/// <see cref="ScryLive.End"/> was cut, and a client asks for the command again by its id at
/// <c>{pattern}/{Route}/{id}</c>, which answers with the same shapes for as long as the server holds it.
/// </remarks>
public static class ScryCommandProtocol
{
    /// <summary>The route a command is sent to, and under which one is asked for again by its id.</summary>
    public const string Route = "command";

    /// <summary>The route answering with the commands this caller may send, as <see cref="CommandCapabilities"/>.</summary>
    public const string CapabilitiesRoute = "capabilities";
}
