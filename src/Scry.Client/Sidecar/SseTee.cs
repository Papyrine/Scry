/// <summary>
/// A live query's response body on its way to the client, watched as it goes past. Every read is
/// handed back exactly as it arrived; a copy of the same bytes goes into <see cref="SseFramer"/> so
/// the sidecar can show what the connection carried.
/// </summary>
/// <remarks>
/// <para>
/// A live query's body has no end to read to, so it is the one Scry response that cannot be buffered
/// to be shown. Watching it as it flows is the alternative, and it comes with one absolute rule:
/// nothing here may make the consumer wait. No I/O, no lock the panel can hold, no handoff to
/// another thread — the framing is a synchronous pass over bytes that were just read, and it must
/// stay one. A future addition here that parses, logs or marshals would turn the debug panel into a
/// throttle on the app's live queries.
/// </para>
/// <para>
/// Reads are never anticipated. What the consumer has not asked for is not read, so the panel shows
/// what the app saw rather than what the socket held — which matches the pull semantics
/// <see cref="LivePump"/> is built on.
/// </para>
/// </remarks>
sealed class SseTee(
    Stream inner,
    HttpContent original,
    ScrySidecarStore store,
    ScrySidecarSession session,
    ScrySidecarConnection connection,
    ScrySidecarOptions options) :
    Stream
{
    SseFramer framer = new(options.MaxRetainedAnswerBytes);
    bool closed;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    // A live query's response is chunked, so there is no length to forward. StreamContent only asks
    // a seekable stream, which this never is.
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, Cancel cancel = default)
    {
        int read;
        try
        {
            read = await inner.ReadAsync(buffer, cancel);
        }
        catch (Exception exception)
        {
            Cut(exception);
            throw;
        }

        Observe(buffer.Span[..read], read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, Cancel cancel) =>
        ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

    public override int Read(Span<byte> buffer)
    {
        int read;
        try
        {
            read = inner.Read(buffer);
        }
        catch (Exception exception)
        {
            Cut(exception);
            throw;
        }

        Observe(buffer[..read], read);
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    // CopyTo and CopyToAsync are deliberately not overridden: Stream's own are loops over Read, so
    // they are watched for free. Forwarding them to the inner stream would read straight past this.

    /// <summary>
    /// Watched before the bytes are handed back, because once they are the caller owns the buffer
    /// and may write over it.
    /// </summary>
    void Observe(ReadOnlySpan<byte> chunk, int read)
    {
        if (closed || session.Detached)
        {
            return;
        }

        if (read == 0)
        {
            // Ran out with no closing event, so the connection was cut rather than ended. The pump
            // asks again, and the panel says which of the two happened.
            Close("cut", error: null, reconnect: true);
            return;
        }

        try
        {
            framer.Append(chunk, Framed);
        }
        catch
        {
            // A log that cannot frame what it saw stops framing. It does not fail the read.
            closed = true;
        }
    }

    void Framed(SseFrame frame)
    {
        var captured = new ScrySidecarEvent
        {
            At = DateTimeOffset.Now,
            Name = frame.Name,
            EventId = frame.Id,
            Bytes = frame.Bytes,
            Json = Kept(frame)
        };

        connection.Add(captured);
        session.Observe(connection, captured);

        // Exactly one of these two closes a stream the server chose to close. Anything after one is
        // a server this client does not understand, and is recorded without being acted on.
        if (frame.Name == ScryLive.End)
        {
            Ended(frame);
        }
        else if (frame.Name == ScryLive.Error)
        {
            Failed(frame);
        }

        store.Touch();
    }

    void Ended(SseFrame frame)
    {
        try
        {
            var end = ScryJson.DeserializeLiveEnd(frame.Data.Span);
            Close(end.Reason ?? ScryLive.End, error: null, end.Reconnect);
        }
        catch (Exception)
        {
            // A closing event this client cannot read still closed the connection.
            Close(ScryLive.End, error: null, reconnect: false);
        }
    }

    void Failed(SseFrame frame)
    {
        var said = "The server ended the live query on a failure.";
        try
        {
            if (ScryJson.TryDeserializeError(frame.Data.Span) is {Error: { } error})
            {
                said = error;
            }
        }
        catch (Exception)
        {
        }

        Close(ScryLive.Error, said, reconnect: false);
    }

    // Kept only for what carries something worth reading, and only when all of it arrived: half a
    // payload shown as if it were whole would be worse than a size on its own.
    static string? Kept(SseFrame frame)
    {
        if (frame.Truncated ||
            frame.Data.IsEmpty ||
            frame.Name is ScryLive.Ping or ScryLive.Unchanged)
        {
            return null;
        }

        return SidecarJson.Prettify(frame.Data.Span);
    }

    // Whatever ended the read is the caller's to classify — LivePump decides what is worth asking
    // again. This only says that it happened.
    void Cut(Exception exception) =>
        Close("cut", exception.Message, reconnect: true);

    void Close(string ended, string? error, bool reconnect)
    {
        if (closed)
        {
            return;
        }

        closed = true;
        try
        {
            session.Close(connection, ended, error, reconnect);
            store.Touch();
        }
        catch
        {
            // Recording the ending is the last thing this does and the least important.
        }
    }

    public override void Flush() =>
        inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    // Disposed twice in the ordinary case: the consumer gives up the stream it was reading, and then
    // the response that carried it is disposed in turn. Both land here, and both must be harmless.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close("cut", error: null, reconnect: true);
            framer.Dispose();
            inner.Dispose();
            original.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Close("cut", error: null, reconnect: true);
        framer.Dispose();
        await inner.DisposeAsync();
        original.Dispose();
        await base.DisposeAsync();
    }
}
