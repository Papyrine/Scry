/// <summary>
/// A <see cref="PooledBufferWriter"/> that refuses to grow past a limit. What a live query's answer is
/// written into: the whole of it has to be in hand before it can be compared with the one before, so
/// how much is held is bounded while it is being written rather than measured afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Counted in <see cref="Advance"/>, the one point at which bytes are actually written, rather than in
/// <see cref="GetMemory"/>: a JSON writer asks for room ahead of what it writes — three bytes for every
/// character of a string it might have to transcode, and never less than a few kilobytes — so refusing
/// on the request would turn away an answer well inside the limit, and every answer within a few
/// kilobytes of it. The room it asks for is handed out in full, since a writer must get at least what
/// it asked for; what it then commits is what is held to the limit.
/// </para>
/// <para>
/// The refusal is a <see cref="ScryValidationException"/>, since what it says is the client's to act
/// on — ask for less. It is thrown once: the JSON writer above this flushes as it is disposed, and a
/// second throw from there would replace the first with one that says nothing new. After the first,
/// what is committed is dropped, and the buffer holds what was accepted before it.
/// </para>
/// </remarks>
sealed class BoundedBufferWriter(int limit) :
    IBufferWriter<byte>,
    IDisposable
{
    PooledBufferWriter inner = new();
    bool refused;

    public ReadOnlyMemory<byte> WrittenMemory => inner.WrittenMemory;

    public void Advance(int count)
    {
        if (refused)
        {
            return;
        }

        if (inner.WrittenCount + count > limit)
        {
            refused = true;
            throw new ScryValidationException(
                $"The result is larger than a live query may hold ({limit} bytes). Ask for fewer rows or fewer members, or page it.");
        }

        inner.Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0) =>
        inner.GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) =>
        inner.GetSpan(sizeHint);

    public void Dispose() =>
        inner.Dispose();
}
