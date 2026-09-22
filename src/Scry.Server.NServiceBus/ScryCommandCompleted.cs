namespace Scry;

/// <summary>
/// A worker's word that a command it was sent is done: the reply to it, sent to the Scry server that
/// dispatched it, which finishes the command there — its outcome to whoever is waiting, its audit
/// entry, and the change to its target.
/// </summary>
/// <remarks>
/// <para>
/// Plain values only — the id, whether it succeeded, the result as the JSON <see cref="ScryJson"/>
/// wrote, the message a caller may be shown — so the endpoint's serializer never has to understand a
/// Scry type.
/// </para>
/// <para>
/// Not short-lived, unlike <see cref="ScryChanged"/>: a change missed is one the poll catches anyway,
/// but a completion is the only word the server gets, and without it the command stays pending until
/// the server gives up on it.
/// </para>
/// </remarks>
public sealed class ScryCommandCompleted :
    IMessage
{
    /// <summary>The command's id, as the client gave it.</summary>
    public Guid Id { get; set; }

    /// <summary>Whether the handler finished without throwing.</summary>
    public bool Succeeded { get; set; }

    /// <summary>What the handler answered with, as JSON, for a command that has a result.</summary>
    public string? Result { get; set; }

    /// <summary>Why it failed: only ever what a caller may read.</summary>
    public string? Error { get; set; }
}
