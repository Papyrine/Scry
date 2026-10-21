namespace Scry;

/// <summary>
/// A segment that is about to be dropped, handed to
/// <see cref="ScrySqlServerDisclosureOptions.Retire"/> to be kept somewhere else first.
/// </summary>
/// <remarks>
/// What it gives is every batch the segment was made from, as each arrived and in the order the
/// chain links them. A batch is what every sink takes, so a copy can be put into another store and
/// asked the same questions, and each batch can be checked against the link the chain still holds
/// for it.
/// </remarks>
public sealed class ScryDisclosureArchive
{
    ScrySqlServerDisclosureStore store;

    internal ScryDisclosureArchive(ScrySqlServerDisclosureStore store, ScryDisclosureSegment segment)
    {
        this.store = store;
        Segment = segment;
    }

    /// <summary>The segment, with how much it holds and the span of time it covers.</summary>
    public ScryDisclosureSegment Segment { get; }

    /// <summary>
    /// Every batch of the segment, in the order the chain links them, each with the shape it named
    /// and the content it carried. Content that was erased comes back by its address alone.
    /// </summary>
    public IAsyncEnumerable<ScryDisclosureBatch> Batches(Cancel cancel = default) =>
        store.Batches(Segment, cancel);

    /// <summary>
    /// Writes every batch of the segment to <paramref name="stream"/>, each behind its length, which
    /// <see cref="Read"/> reads back.
    /// </summary>
    public async Task WriteTo(Stream stream, Cancel cancel = default)
    {
        var bytes = new ArrayBufferWriter<byte>();
        var length = new byte[sizeof(int)];
        await foreach (var batch in Batches(cancel))
        {
            bytes.ResetWrittenCount();
            batch.Serialize(bytes);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.WrittenCount);
            await stream.WriteAsync(length, cancel);
            await stream.WriteAsync(bytes.WrittenMemory, cancel);
        }
    }

    /// <summary>Reads back the batches <see cref="WriteTo"/> wrote, in the order it wrote them.</summary>
    /// <exception cref="FormatException">The stream ends part-way through a batch, or holds one that does not read.</exception>
    public static async IAsyncEnumerable<ScryDisclosureBatch> Read(Stream stream, [EnumeratorCancellation] Cancel cancel = default)
    {
        var length = new byte[sizeof(int)];
        while (true)
        {
            var read = await stream.ReadAtLeastAsync(length, length.Length, throwOnEndOfStream: false, cancel);
            if (read == 0)
            {
                yield break;
            }

            var size = BinaryPrimitives.ReadInt32LittleEndian(length);
            if (read < length.Length ||
                size < 0)
            {
                throw new FormatException("The archive ends part-way through a batch.");
            }

            var bytes = new byte[size];
            if (await stream.ReadAtLeastAsync(bytes, size, throwOnEndOfStream: false, cancel) < size)
            {
                throw new FormatException("The archive ends part-way through a batch.");
            }

            yield return ScryDisclosureBatch.Deserialize(bytes);
        }
    }
}
