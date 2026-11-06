/// <summary>
/// The hash chain's arithmetic, shared by what writes a link and what checks one.
/// </summary>
static class DisclosureChain
{
    /// <summary>
    /// How a link's digest was made, kept on the link: a later way of making one then leaves the
    /// links made this way checkable as they were.
    /// </summary>
    public const byte Version = 1;

    /// <summary>
    /// A digest of what the record keeps of a batch: its event, its units and the rows they were
    /// read from, its shape, its close, its review. Not its content, which is kept apart, can be
    /// erased, and is covered already — a unit's address is a hash of what it carried.
    /// </summary>
    /// <remarks>
    /// Taken over the batch as every sink writes it, with the content and the shape's members left out
    /// — the event names its shape by an address that is a hash of them — and everything that has no
    /// order of its own put in one. So the same digest can be made again from the tables alone.
    /// </remarks>
    public static byte[] Digest(ScryDisclosureBatch batch)
    {
        var review = batch.Review;
        if (review is not null)
        {
            review = review with
            {
                Events = [.. review.Events.Distinct().Order()]
            };
        }

        var kept = new ScryDisclosureBatch
        {
            EventId = batch.EventId,
            Sequence = batch.Sequence,
            Begin = batch.Begin,
            Units = [.. batch.Units.OrderBy(_ => _.Ordinal)],
            Entities = [.. batch.Entities.OrderBy(_ => _.Ordinal).ThenBy(_ => _.Slot)],
            Close = batch.Close,
            Review = review
        };
        var bytes = new ArrayBufferWriter<byte>();
        kept.Serialize(bytes);
        return SHA256.HashData(bytes.WrittenSpan);
    }

    /// <summary>A link's hash: over the link before it and its own digest.</summary>
    public static byte[] Hash(byte[] previous, byte[] digest)
    {
        Span<byte> joined = stackalloc byte[previous.Length + digest.Length];
        previous.CopyTo(joined);
        digest.CopyTo(joined[previous.Length..]);
        return SHA256.HashData(joined);
    }
}
