namespace Scry;

/// <summary>
/// A handler's word that a command a Scry server sent is done: the reply to it, sent to the server's
/// input queue, which finishes the command there.
/// </summary>
/// <remarks>
/// Plain values only, so the bus's serializer never has to understand a Scry type.
/// </remarks>
public sealed class RebusCommandCompleted
{
    /// <summary>The command's id, as the client gave it.</summary>
    public Guid Id { get; init; }

    /// <summary>Whether its handlers finished without throwing.</summary>
    public bool Succeeded { get; init; }

    /// <summary>What the handler answered with, as JSON, for a command that has a result.</summary>
    public string? Result { get; init; }

    /// <summary>
    /// The rows the handler said the result was made from, as <see cref="ScryDisclosureRow.Serialize"/>
    /// wrote them. Null where it said none, and from a worker older than this property.
    /// </summary>
    public string? Rows { get; init; }

    /// <summary>Why it failed: only ever what a caller may read.</summary>
    public string? Error { get; init; }
}
