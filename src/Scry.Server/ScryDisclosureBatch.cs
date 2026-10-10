namespace Scry;

/// <summary>
/// What a sink is asked to accept in one call: part of one event — its header, some of its units,
/// its close — or one review. A response held whole is one batch; a stream is several under one
/// event id, in sequence.
/// </summary>
/// <remarks>
/// <see cref="Serialize"/> and <see cref="Deserialize"/> are the one binary form every sink keeps a
/// batch in, so a journal file written by one version of a sink is read by another, and an adapter
/// chooses where to put the bytes and nothing else.
/// </remarks>
public sealed class ScryDisclosureBatch
{
    /// <summary>The event the batch belongs to, or the review's own id for a batch carrying one.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Which batch of the event this is, counted from zero. With the id, what makes a second delivery recognisable.</summary>
    public int Sequence { get; init; }

    /// <summary>The event's header, on its first batch.</summary>
    public ScryDisclosureEvent? Begin { get; init; }

    /// <summary>The shape of the event's rows, on its first batch, where it has one.</summary>
    public ScryDisclosureShape? Shape { get; init; }

    /// <summary>The units this batch adds, in order.</summary>
    public IReadOnlyList<ScryDisclosureUnit> Units { get; init; } = [];

    /// <summary>The rows those units were read from.</summary>
    public IReadOnlyList<ScryDisclosureEntity> Entities { get; init; } = [];

    /// <summary>
    /// The content this batch refers to that an earlier batch of the same event did not already
    /// carry. A store keeps each address once.
    /// </summary>
    public IReadOnlyList<ScryDisclosureContent> Contents { get; init; } = [];

    /// <summary>How the event ended, on its last batch.</summary>
    public ScryDisclosureClose? Close { get; init; }

    /// <summary>A reviewer's question, for a batch that records one rather than an event.</summary>
    public ScryDisclosureReview? Review { get; init; }

    /// <summary>Writes the batch in the form <see cref="Deserialize"/> reads.</summary>
    public void Serialize(IBufferWriter<byte> output) =>
        DisclosureBatchCodec.Write(this, output);

    /// <summary>
    /// Reads what <see cref="Serialize"/> wrote. The content is copied out of
    /// <paramref name="bytes"/>, so the batch outlives the buffer it was read from.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a batch this version reads.</exception>
    public static ScryDisclosureBatch Deserialize(ReadOnlySpan<byte> bytes) =>
        DisclosureBatchCodec.Read(bytes);
}
