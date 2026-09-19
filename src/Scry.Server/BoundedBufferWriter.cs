/// <summary>
/// A <see cref="PooledBufferWriter"/> that refuses to grow past a limit. What a live query's answer is
/// written into: the whole of it has to be in hand before it can be compared with the one before, so
/// how much is held is bounded while it is being written rather than measured afterwards.
/// </summary>
/// <remarks>
/// The refusal is a <see cref="ScryValidationException"/>, since what it says is the client's to act
/// on — ask for less. It is thrown once: the JSON writer above this flushes as it is disposed, and a
/// second throw from there would replace the first with one that says nothing new. After the first,
/// what is written goes to a scratch array and is dropped.
/// </remarks>
sealed class BoundedBufferWriter(int limit) :
    IBufferWriter<byte>,
    IDisposable
{
    PooledBufferWriter inner = new();
    byte[]? scratch;

    public ReadOnlyMemory<byte> WrittenMemory => inner.WrittenMemory;

    public void Advance(int count)
    {
        if (scratch is null)
        {
            inner.Advance(count);
        }
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (Overflowing(sizeHint) is { } dropped)
        {
            return dropped;
        }

        // Sliced to what is left, so a writer handed more room than it asked for cannot advance past
        // the limit without coming back here first.
        var memory = inner.GetMemory(sizeHint);
        return memory[..Math.Min(memory.Length, limit - inner.WrittenCount)];
    }

    public Span<byte> GetSpan(int sizeHint = 0) =>
        GetMemory(sizeHint).Span;

    byte[]? Overflowing(int sizeHint)
    {
        var wanted = Math.Max(sizeHint, 1);
        if (scratch is not null)
        {
            if (scratch.Length < wanted)
            {
                scratch = new byte[wanted];
            }

            return scratch;
        }

        if (inner.WrittenCount + wanted <= limit)
        {
            return null;
        }

        scratch = new byte[wanted];
        throw new ScryValidationException(
            $"The result is larger than a live query may hold ({limit} bytes). Ask for fewer rows or fewer members, or page it.");
    }

    public void Dispose() =>
        inner.Dispose();
}
