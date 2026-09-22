namespace Scry;

/// <summary>Where a command is: still being handled, or finished one way or the other.</summary>
/// <remarks>The names are the wire contract, as every wire enum's are.</remarks>
public enum CommandStatus
{
    /// <summary>Accepted and handed to its handler, which has not finished.</summary>
    Pending,

    /// <summary>The handler finished, and its result, if it has one, is on the receipt.</summary>
    Completed,

    /// <summary>The command could not be carried out. The receipt's error says why, as far as the server will.</summary>
    Failed
}
