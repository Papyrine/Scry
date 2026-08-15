/// <summary>
/// The buffer a buffered response is written into, plus the point at which it stops being one. An
/// envelope that finishes under <paramref name="threshold"/> is written once with a
/// <c>Content-Length</c>, exactly as it was before; one that outgrows it is drained to the response as
/// it is written, so what is resident is bounded by the threshold rather than by the result.
/// </summary>
/// <remarks>
/// Draining is a permission, not a default: <see cref="AllowSpill"/> has to grant it per result, from
/// the projection plan, before the first row is written. A result whose plan diverts
/// <c>[BinaryTransfer]</c> values never grants it — the raw parts have to precede the JSON that
/// references them, so nothing can go out until the whole envelope exists. Everything that does not
/// ask stays fully buffered, which is what it already was.
/// </remarks>
sealed class ResponseSpill(HttpContext context, int threshold) :
    IDisposable
{
    readonly PooledBufferWriter buffer = new();
    bool allowed;
    bool committed;

    /// <summary>
    /// The sink the <see cref="Utf8JsonWriter"/> is built over. Stable for the life of the envelope:
    /// draining resets the buffer rather than replacing it, so the writer never has to be re-pointed.
    /// </summary>
    public IBufferWriter<byte> Output => buffer;

    /// <summary>What has been written but not yet sent — the whole envelope unless it has drained.</summary>
    public ReadOnlyMemory<byte> Pending => buffer.WrittenMemory;

    /// <summary>
    /// Whether the first byte is on the wire. Past this the status and headers are fixed, so a failure
    /// can only be reported by truncating, and the caller must not try to write an error instead.
    /// </summary>
    public bool Committed => committed;

    /// <summary>
    /// Grants or withholds permission to drain, from what the projection plan says before any row is
    /// read. Withheld by default, so a path that never asks stays fully buffered.
    /// </summary>
    public void AllowSpill(bool value) =>
        allowed = value;

    /// <summary>
    /// Whether the envelope has reached the threshold, counting <paramref name="pending"/> — the bytes
    /// the JSON writer is still holding, which are part of the response but not yet in the buffer.
    /// </summary>
    public bool ShouldDrain(int pending) =>
        allowed &&
        buffer.WrittenCount + pending >= threshold;

    /// <summary>
    /// Sends what has accumulated and takes the buffer back to empty. The caller must have flushed its
    /// JSON writer first: until it has, the writer holds a span into this buffer that the reset would
    /// invalidate.
    /// </summary>
    /// <remarks>
    /// A no-op without permission, so the fail-closed direction of a caller draining when it should not
    /// is that the response stays whole — which is what it was before this type existed.
    /// </remarks>
    public async ValueTask DrainAsync(Cancel cancel)
    {
        if (!allowed)
        {
            return;
        }

        if (!committed)
        {
            context.Response.ContentType = "application/json";
            committed = true;
        }

        // Awaited before the reset: the write reads out of the rented array that the reset hands
        // straight back for overwriting.
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, cancel);
        buffer.Reset();
    }

    /// <summary>Sends the rest of the envelope, declaring its length when it is the whole of one.</summary>
    /// <remarks>
    /// Nothing committed means nothing has gone out, which is the one condition under which the pending
    /// bytes are the entire body and a length can be declared. Kestrel never infers one from what an
    /// application buffered, so a response that says nothing here is chunked.
    /// </remarks>
    public async Task CompleteAsync(Cancel cancel)
    {
        if (!committed)
        {
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = buffer.WrittenCount;
        }

        await context.Response.Body.WriteAsync(buffer.WrittenMemory, cancel);
    }

    public void Dispose() =>
        buffer.Dispose();
}
