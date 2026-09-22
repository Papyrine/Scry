namespace Scry;

/// <summary>
/// A consumer's word that a command a Scry server published is done: published once its consumers
/// have finished, and consumed by every node that serves commands — the one that dispatched it
/// finishes it, and the rest hold nothing it names.
/// </summary>
/// <remarks>
/// Plain values only, so the bus's serializer never has to understand a Scry type. Published rather
/// than sent, so neither end needs an endpoint convention for it.
/// </remarks>
public sealed class MassTransitCommandCompleted
{
    /// <summary>The command's id, as the client gave it.</summary>
    public Guid Id { get; set; }

    /// <summary>Whether its consumers finished without throwing.</summary>
    public bool Succeeded { get; set; }

    /// <summary>What the consumer answered with, as JSON, for a command that has a result.</summary>
    public string? Result { get; set; }

    /// <summary>Why it failed: only ever what a caller may read.</summary>
    public string? Error { get; set; }
}
