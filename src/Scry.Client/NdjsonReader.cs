/// <summary>
/// Reads newline-delimited JSON off a stream a line at a time, as the UTF-8 the line arrived as.
/// </summary>
/// <remarks>
/// A <see cref="StreamReader"/> would hand back a string per line, which for a streamed result means
/// a UTF-16 copy of every row — transcoded from bytes the reader already had, only for the JSON
/// reader to transcode it back. The returned memory points into this reader's own buffer and is valid
/// until the next read, which is exactly as long as the row it holds is being materialized.
/// </remarks>
sealed class NdjsonReader(Stream stream) :
    IDisposable
{
    byte[]? buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);

    // What is buffered and not yet handed out, as [start, end) within the buffer.
    int start;
    int end;
    bool ended;

    /// <summary>
    /// Whether the line last handed out was the stream's final one and carried no newline — cut
    /// off, or written by a sender that ends without one. Which of the two is for its content to say.
    /// </summary>
    public bool Unterminated { get; private set; }

    /// <summary>The next line, or null at the end of the stream. Empty lines are returned as empty.</summary>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadLineAsync(Cancel cancel)
    {
        while (true)
        {
            var current = buffer ?? throw new ObjectDisposedException(nameof(NdjsonReader));
            var newline = current.AsSpan(start, end - start).IndexOf((byte) '\n');
            if (newline >= 0)
            {
                var line = current.AsMemory(start, newline);
                start += newline + 1;
                return Trim(line);
            }

            if (ended)
            {
                // A final line the sender did not terminate. The stream's closing marker is what tells
                // a caller the result is complete, so an unterminated last line is still handed over
                // and judged on its content rather than dropped here.
                if (end == start)
                {
                    return null;
                }

                var last = current.AsMemory(start, end - start);
                start = end;
                Unterminated = true;
                return Trim(last);
            }

            await Fill(cancel);
        }
    }

    // A line written by a Windows host may carry the CR the sender's newline pairs with.
    static ReadOnlyMemory<byte> Trim(ReadOnlyMemory<byte> line)
    {
        if (line.Length > 0 && line.Span[^1] == (byte) '\r')
        {
            return line[..^1];
        }

        return line;
    }

    async ValueTask Fill(Cancel cancel)
    {
        var current = buffer!;

        // Slide what is left to the front, and only grow once a whole line genuinely does not fit —
        // so the buffer settles at the width of the widest row rather than at the size of the result.
        if (start > 0)
        {
            current.AsSpan(start, end - start).CopyTo(current);
            end -= start;
            start = 0;
        }

        if (end == current.Length)
        {
            var grown = ArrayPool<byte>.Shared.Rent(current.Length * 2);
            current.AsSpan(0, end).CopyTo(grown);
            buffer = grown;
            ArrayPool<byte>.Shared.Return(current);
            current = grown;
        }

        var read = await stream.ReadAsync(current.AsMemory(end), cancel);
        if (read == 0)
        {
            ended = true;
            return;
        }

        end += read;
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
