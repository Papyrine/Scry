namespace Scry;

/// <summary>
/// One segment of the disclosure record as SQL Server keeps it: a schema holding every table of the
/// record for a stretch of it, written to until the next is started and dropped whole when it is
/// retired.
/// </summary>
/// <param name="Number">Which segment, counted from one.</param>
/// <param name="Schema">The schema its tables are in, while it is kept.</param>
/// <param name="Opened">When it was started.</param>
public sealed record ScryDisclosureSegment(int Number, string Schema, DateTimeOffset Opened)
{
    /// <summary>When the next segment was started, or null for the one being written to.</summary>
    public DateTimeOffset? Sealed { get; init; }

    /// <summary>The last link the chain had when the next segment was started.</summary>
    public long? Link { get; init; }

    /// <summary>That link's hash: where the chain stood when this segment was left.</summary>
    public ScryDisclosureAddress? Head { get; init; }

    /// <summary>
    /// Whether its tables are ledger tables: the record is kept whole in them, and it is never retired.
    /// </summary>
    public bool Ledgered { get; init; }

    /// <summary>Whether it is being retired: still read, and no longer written to.</summary>
    public bool Retiring { get; init; }

    /// <summary>When it was dropped, or null for one that is kept.</summary>
    public DateTimeOffset? Retired { get; init; }

    /// <summary>Whether it was handed to <see cref="ScrySqlServerDisclosureOptions.Retire"/> before it was dropped.</summary>
    public bool HandedOn { get; init; }

    /// <summary>How many batches it held when it was retired.</summary>
    public long? Batches { get; init; }

    /// <summary>How many answers it held when it was retired.</summary>
    public long? Events { get; init; }

    /// <summary>How much of the database it took when it was retired, in bytes.</summary>
    public long? Bytes { get; init; }

    /// <summary>When the first answer in it was given.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>When the last answer in it was given.</summary>
    public DateTimeOffset? To { get; init; }
}

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

/// <summary>
/// When the disclosure record starts a new segment, and how many it keeps, as the record holds them.
/// </summary>
/// <param name="RollAfter">How long a segment is written to, or null for no limit.</param>
/// <param name="RollAtBytes">How large a segment grows, or null for no limit.</param>
/// <param name="Keep">How many segments are kept, or null for all of them.</param>
/// <param name="HandsOn">Whether a segment is handed on before it is dropped. No node without something to hand it to retires one.</param>
public sealed record ScryDisclosureRetention(TimeSpan? RollAfter, long? RollAtBytes, int? Keep, bool HandsOn)
{
    /// <summary>How long a segment is kept once it is rolled away from, or null for no limit.</summary>
    public TimeSpan? KeepFor { get; init; }
}
