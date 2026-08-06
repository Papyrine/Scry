/// <summary>
/// The body of an attachment response, tied to the response that carries it. An attachment is read
/// unbuffered so a large value never lands in memory whole, which means the response has to outlive
/// the call that returned it — disposing this disposes both.
/// </summary>
sealed class AttachmentStream(Stream inner, HttpResponseMessage response) :
    Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) =>
        inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, Cancel cancel) =>
        inner.ReadAsync(buffer, offset, count, cancel);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, Cancel cancel = default) =>
        inner.ReadAsync(buffer, cancel);

    public override void Flush() =>
        inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        response.Dispose();
        await base.DisposeAsync();
    }
}
