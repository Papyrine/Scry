/// <summary>
/// The buffer a buffered response is written into: array-pool backed, so a request that produced one
/// leaves nothing behind for the collector. Grows by doubling like
/// <see cref="ArrayBufferWriter{T}"/>, but each intermediate goes back to the pool rather than
/// becoming garbage — and it starts at a size most responses never have to grow past, where the
/// framework's own writer starts at 256 bytes and reaches a large response through a run of
/// ever-larger discarded arrays, the last of which are big enough to land on the large object heap.
/// </summary>
/// <remarks>
/// <see cref="WrittenMemory"/> points into the rented array and is only valid until
/// <see cref="Dispose"/>, so a caller must finish writing it out before the writer leaves scope.
/// Nothing beyond <see cref="WrittenCount"/> is ever exposed, so a rented array's previous contents
/// stay unreadable through this and are never returned cleared — the same bet the framework's own
/// pooled writers make.
/// </remarks>
sealed class PooledBufferWriter :
    IBufferWriter<byte>,
    IDisposable
{
    // Comfortably past a single-row or small-list response, so those never grow at all, while still
    // being a size the pool serves from its own buckets rather than allocating for.
    const int defaultCapacity = 16 * 1024;

    byte[]? buffer = ArrayPool<byte>.Shared.Rent(defaultCapacity);
    int written;

    public ReadOnlyMemory<byte> WrittenMemory => Buffer.AsMemory(0, written);

    public int WrittenCount => written;

    byte[] Buffer =>
        buffer ?? throw new ObjectDisposedException(nameof(PooledBufferWriter));

    public void Advance(int count) =>
        written += count;

    /// <summary>
    /// Drops what has been written, keeping the array. The streaming writer's per-row reset: every row
    /// overwrites the last, so one rented buffer serves the whole stream and settles at the width of
    /// its widest row.
    /// </summary>
    public void Reset() =>
        written = 0;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return Buffer.AsMemory(written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return Buffer.AsSpan(written);
    }

    void Ensure(int sizeHint)
    {
        if (sizeHint < 1)
        {
            sizeHint = 1;
        }

        var current = Buffer;
        if (current.Length - written >= sizeHint)
        {
            return;
        }

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(current.Length * 2, written + sizeHint));
        current.AsSpan(0, written).CopyTo(grown);
        buffer = grown;
        ArrayPool<byte>.Shared.Return(current);
    }

    public void Dispose()
    {
        if (buffer is not { } rented)
        {
            return;
        }

        buffer = null;
        ArrayPool<byte>.Shared.Return(rented);
    }
}
