/// <summary>
/// One server-sent event as the framer read it. Its data points into a buffer the framer reuses, so
/// it is valid for the call that was handed it and no longer — copy anything worth keeping.
/// </summary>
/// <param name="Name">The event's name, or <c>message</c> where it named none.</param>
/// <param name="Id">What the server called this event, which only an answer carries.</param>
/// <param name="Data">As much of the event's data as the framer keeps.</param>
/// <param name="Truncated">Whether the data was longer than the framer keeps.</param>
/// <param name="Bytes">The event's size on the wire, whatever was kept of its data.</param>
readonly record struct SseFrame(
    string Name,
    string? Id,
    ReadOnlyMemory<byte> Data,
    bool Truncated,
    int Bytes);

/// <summary>
/// Enough of the server-sent-events format to say what went past a live query's connection: the event
/// names, their identifiers and their sizes, read from wherever the reads happen to fall.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than the platform's <c>SseParser</c>, which pulls a <see cref="Stream"/>:
/// feeding one a copy of a stream being read elsewhere needs a pipe between them, and a pipe either
/// applies backpressure — which would let a debug log stall the app's read, the one thing this must
/// never do — or buffers without a bound. This is a state machine with no I/O in it, which is what
/// makes it safe to run on the read path.
/// </para>
/// <para>
/// It dispatches exactly where <c>SseParser</c> does: on a blank line, and only where the event
/// carried at least one data field, empty or not. The server writes one on every event including the
/// two that have nothing to say (see <see cref="ScryLive"/>), and a panel whose count disagreed with
/// what the client acted on would be worse than a panel with no count.
/// </para>
/// <para>
/// Nothing accumulates past one event, and that one is capped. A connection held open for a week
/// costs what a single event costs.
/// </para>
/// </remarks>
sealed class SseFramer :
    IDisposable
{
    // Room for a field name and its colon on top of the data a line can carry.
    const int lineOverhead = 64;

    int maxDataBytes;
    byte[] line;
    byte[] data;
    int lineLength;
    int dataLength;
    bool dataFound;
    bool truncated;
    bool pendingReturn;
    bool disposed;
    bool startOfStream = true;
    string? name;
    string? id;
    int bytes;

    public SseFramer(int maxDataBytes)
    {
        this.maxDataBytes = Math.Max(maxDataBytes, 0);
        line = ArrayPool<byte>.Shared.Rent(Math.Min(this.maxDataBytes + lineOverhead, 4096));
        data = ArrayPool<byte>.Shared.Rent(Math.Clamp(this.maxDataBytes, 1, 4096));
    }

    public void Append(ReadOnlySpan<byte> chunk, Action<SseFrame> onFrame)
    {
        while (!chunk.IsEmpty)
        {
            // The other half of a CRLF the read happened to split. It ends no line of its own.
            if (pendingReturn)
            {
                pendingReturn = false;
                if (chunk[0] == (byte) '\n')
                {
                    bytes++;
                    chunk = chunk[1..];
                    continue;
                }
            }

            var end = chunk.IndexOfAny((byte) '\n', (byte) '\r');
            if (end < 0)
            {
                Keep(chunk);
                bytes += chunk.Length;
                return;
            }

            Keep(chunk[..end]);

            // A carriage return and the newline after it are one ending. Resolving it here rather
            // than on the next line keeps the event's size exact: a line ending read after the
            // blank one that ends an event would otherwise be counted against the event after it.
            var ending = 1;
            if (chunk[end] == (byte) '\r')
            {
                if (end + 1 < chunk.Length)
                {
                    ending = chunk[end + 1] == (byte) '\n' ? 2 : 1;
                }
                else
                {
                    pendingReturn = true;
                }
            }

            bytes += end + ending;
            chunk = chunk[(end + ending)..];
            EndLine(onFrame);
        }
    }

    void Keep(ReadOnlySpan<byte> part)
    {
        var room = line.Length - lineLength;
        if (part.Length > room)
        {
            if (line.Length < maxDataBytes + lineOverhead)
            {
                Grow(lineLength + part.Length);
                room = line.Length - lineLength;
            }

            if (part.Length > room)
            {
                // Longer than the framer keeps. The size is still counted exactly, from the chunks
                // rather than from what was stored.
                truncated = true;
                part = part[..Math.Max(room, 0)];
            }
        }

        if (part.IsEmpty)
        {
            return;
        }

        part.CopyTo(line.AsSpan(lineLength));
        lineLength += part.Length;
    }

    void Grow(int wanted)
    {
        var size = Math.Min(Math.Max(wanted, line.Length * 2), maxDataBytes + lineOverhead);
        if (size <= line.Length)
        {
            return;
        }

        var grown = ArrayPool<byte>.Shared.Rent(size);
        line.AsSpan(0, lineLength).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(line);
        line = grown;
    }

    void EndLine(Action<SseFrame> onFrame)
    {
        var content = line.AsSpan(0, lineLength);
        lineLength = 0;

        // A byte-order mark leads the stream, not every line of it.
        if (startOfStream)
        {
            startOfStream = false;
            ReadOnlySpan<byte> mark = [0xEF, 0xBB, 0xBF];
            if (content.StartsWith(mark))
            {
                content = content[3..];
            }
        }

        if (content.IsEmpty)
        {
            Dispatch(onFrame);
            return;
        }

        // A comment, which is how a server that is not this one keeps a connection alive.
        if (content[0] == (byte) ':')
        {
            return;
        }

        var colon = content.IndexOf((byte) ':');
        var field = colon < 0 ? content : content[..colon];
        var value = colon < 0 ? default : content[(colon + 1)..];
        if (!value.IsEmpty &&
            value[0] == (byte) ' ')
        {
            value = value[1..];
        }

        if (field.SequenceEqual("event"u8))
        {
            name = EventName(value);
            return;
        }

        if (field.SequenceEqual("id"u8))
        {
            id = Encoding.UTF8.GetString(value);
            return;
        }

        if (field.SequenceEqual("data"u8))
        {
            AppendData(value);
        }

        // Anything else — a retry interval, or a field from a server newer than this client — is
        // counted towards the event's size and otherwise left alone.
    }

    void AppendData(ReadOnlySpan<byte> value)
    {
        // Several data lines in one event are joined by the newlines that separated them.
        if (dataFound &&
            dataLength < maxDataBytes)
        {
            data[dataLength++] = (byte) '\n';
        }

        dataFound = true;
        var room = maxDataBytes - dataLength;
        if (value.Length > room)
        {
            truncated = true;
            value = value[..Math.Max(room, 0)];
        }

        if (value.IsEmpty)
        {
            return;
        }

        if (dataLength + value.Length > data.Length)
        {
            var grown = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(dataLength + value.Length, data.Length * 2), maxDataBytes));
            data.AsSpan(0, dataLength).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(data);
            data = grown;
        }

        value.CopyTo(data.AsSpan(dataLength));
        dataLength += value.Length;
    }

    void Dispatch(Action<SseFrame> onFrame)
    {
        if (dataFound)
        {
            // "message" is what the format calls an event that did not name itself. This server
            // always names them, so it stands for one from a server newer than this client.
            onFrame(new(name ?? "message", id, data.AsMemory(0, dataLength), truncated, bytes));
        }

        name = null;
        id = null;
        dataLength = 0;
        dataFound = false;
        truncated = false;
        bytes = 0;
    }

    // The names this server writes, matched without allocating one to compare against.
    static string EventName(ReadOnlySpan<byte> value) =>
        value.SequenceEqual("result"u8) ? ScryLive.Result :
        value.SequenceEqual("unchanged"u8) ? ScryLive.Unchanged :
        value.SequenceEqual("ping"u8) ? ScryLive.Ping :
        value.SequenceEqual("error"u8) ? ScryLive.Error :
        value.SequenceEqual("end"u8) ? ScryLive.End :
        Encoding.UTF8.GetString(value);

    // Disposed twice in the ordinary case: the consumer gives up the stream, and then the response
    // that carried it is disposed in turn.
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ArrayPool<byte>.Shared.Return(line);
        ArrayPool<byte>.Shared.Return(data);
        line = [];
        data = [];
    }
}
