// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Vendored from dotnet/aspnetcore, src/Http/WebUtilities, and adapted to this project's conventions.

/// <summary>
/// A Stream that wraps another stream and allows reading lines. The data is buffered in memory.
/// </summary>
sealed class BufferedReadStream :
    Stream
{
    const byte cr = (byte)'\r';
    const byte lf = (byte)'\n';

    readonly Stream inner;
    readonly byte[] buffer;
    readonly ArrayPool<byte> bytePool;
    int bufferOffset;
    int bufferCount;
    bool disposed;

    /// <summary>Creates a new stream.</summary>
    public BufferedReadStream(Stream inner, int bufferSize)
    {
        this.inner = inner;
        bytePool = ArrayPool<byte>.Shared;
        buffer = bytePool.Rent(bufferSize);
    }

    /// <summary>The currently buffered data.</summary>
    public ArraySegment<byte> BufferedData =>
        new(buffer, bufferOffset, bufferCount);

    public override bool CanRead => inner.CanRead || bufferCount > 0;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanTimeout => inner.CanTimeout;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position - bufferCount;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Position must be positive.");
            }

            if (value == Position)
            {
                return;
            }

            // Backwards?
            if (value <= inner.Position)
            {
                // Forward within the buffer?
                var innerOffset = (int)(inner.Position - value);
                if (innerOffset <= bufferCount)
                {
                    // Yes, just skip some of the buffered data.
                    bufferOffset += bufferCount - innerOffset;
                    bufferCount = innerOffset;
                }
                else
                {
                    // No, reset the buffer.
                    bufferOffset = 0;
                    bufferCount = 0;
                    inner.Position = value;
                }
            }
            else
            {
                // Forward, reset the buffer.
                bufferOffset = 0;
                bufferCount = 0;
                inner.Position = value;
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin == SeekOrigin.Begin)
        {
            Position = offset;
        }
        else if (origin == SeekOrigin.Current)
        {
            Position += offset;
        }
        else
        {
            Position = Length + offset;
        }

        return Position;
    }

    public override void SetLength(long value) =>
        inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            bytePool.Return(buffer);

            if (disposing)
            {
                inner.Dispose();
            }
        }
    }

    public override void Flush() =>
        inner.Flush();

    public override Task FlushAsync(Cancel cancel) =>
        inner.FlushAsync(cancel);

    public override void Write(byte[] buffer, int offset, int count) =>
        inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, Cancel cancel) =>
        inner.WriteAsync(buffer, cancel);

    public override Task WriteAsync(byte[] buffer, int offset, int count, Cancel cancel) =>
        inner.WriteAsync(buffer, offset, count, cancel);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        // Drain buffer.
        if (bufferCount > 0)
        {
            var toCopy = Math.Min(bufferCount, count);
            Buffer.BlockCopy(this.buffer, bufferOffset, buffer, offset, toCopy);
            bufferOffset += toCopy;
            bufferCount -= toCopy;
            return toCopy;
        }

        return inner.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, Cancel cancel) =>
        ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, Cancel cancel)
    {
        // Drain buffer.
        if (bufferCount > 0)
        {
            var toCopy = Math.Min(bufferCount, buffer.Length);
            this.buffer.AsMemory(bufferOffset, toCopy).CopyTo(buffer);
            bufferOffset += toCopy;
            bufferCount -= toCopy;
            return toCopy;
        }

        return await inner.ReadAsync(buffer, cancel);
    }

    /// <summary>Ensures that the buffer is not empty.</summary>
    public bool EnsureBuffered()
    {
        if (bufferCount > 0)
        {
            return true;
        }

        // Downshift to make room.
        bufferOffset = 0;
        bufferCount = inner.Read(buffer, 0, buffer.Length);
        return bufferCount > 0;
    }

    /// <summary>Ensures that the buffer is not empty.</summary>
    public async Task<bool> EnsureBufferedAsync(Cancel cancel)
    {
        if (bufferCount > 0)
        {
            return true;
        }

        // Downshift to make room.
        bufferOffset = 0;
        bufferCount = await inner.ReadAsync(buffer.AsMemory(), cancel);
        return bufferCount > 0;
    }

    /// <summary>Ensures that a minimum amount of buffered data is available.</summary>
    public bool EnsureBuffered(int minCount)
    {
        if (minCount > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(minCount), minCount, "The value must be smaller than the buffer size: " + buffer.Length);
        }

        while (bufferCount < minCount)
        {
            // Downshift to make room.
            if (bufferOffset > 0)
            {
                if (bufferCount > 0)
                {
                    Buffer.BlockCopy(buffer, bufferOffset, buffer, 0, bufferCount);
                }

                bufferOffset = 0;
            }

            var read = inner.Read(buffer, bufferOffset + bufferCount, buffer.Length - bufferCount - bufferOffset);
            bufferCount += read;
            if (read == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Ensures that a minimum amount of buffered data is available.</summary>
    public async Task<bool> EnsureBufferedAsync(int minCount, Cancel cancel)
    {
        if (minCount > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(minCount), minCount, "The value must be smaller than the buffer size: " + buffer.Length);
        }

        while (bufferCount < minCount)
        {
            // Downshift to make room.
            if (bufferOffset > 0)
            {
                if (bufferCount > 0)
                {
                    Buffer.BlockCopy(buffer, bufferOffset, buffer, 0, bufferCount);
                }

                bufferOffset = 0;
            }

            var read = await inner.ReadAsync(buffer.AsMemory(bufferOffset + bufferCount, buffer.Length - bufferCount - bufferOffset), cancel);
            bufferCount += read;
            if (read == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a line. A line is defined as a sequence of characters followed by a carriage return
    /// immediately followed by a line feed. The resulting string does not contain the terminating
    /// carriage return and line feed.
    /// </summary>
    public string ReadLine(int lengthLimit)
    {
        CheckDisposed();
        using var builder = new MemoryStream(200);
        bool foundCr = false, foundCrlf = false;
        var lineLength = 0;

        while (!foundCrlf && EnsureBuffered())
        {
            ProcessLineChar(builder, ref lineLength, lengthLimit, ref foundCr, ref foundCrlf);
        }

        return DecodeLine(builder, foundCrlf);
    }

    /// <summary>
    /// Reads a line. A line is defined as a sequence of characters followed by a carriage return
    /// immediately followed by a line feed. The resulting string does not contain the terminating
    /// carriage return and line feed.
    /// </summary>
    public async Task<string> ReadLineAsync(int lengthLimit, Cancel cancel)
    {
        CheckDisposed();
        using var builder = new MemoryStream(200);
        bool foundCr = false, foundCrlf = false;
        var lineLength = 0;

        while (!foundCrlf && await EnsureBufferedAsync(cancel))
        {
            ProcessLineChar(builder, ref lineLength, lengthLimit, ref foundCr, ref foundCrlf);
        }

        return DecodeLine(builder, foundCrlf);
    }

    void ProcessLineChar(MemoryStream builder, ref int lineLength, int lengthLimit, ref bool foundCr, ref bool foundCrlf)
    {
        var writeCount = 0;
        while (bufferCount > 0)
        {
            var b = buffer[bufferOffset];
            bufferOffset++;
            bufferCount--;
            writeCount++;
            if (b == lf && foundCr)
            {
                builder.Write(buffer.AsSpan(bufferOffset - writeCount, writeCount));
                lineLength += writeCount;
                foundCrlf = true;
                return;
            }

            foundCr = b == cr;

            // lineLength is the cumulative length of the line accumulated by previous invocations of
            // this method (one per buffer refill), and writeCount holds the bytes consumed from the
            // current buffer that have not been flushed yet. Comparing the cumulative total against
            // the limit ensures the limit is enforced even when a single line spans multiple buffers.
            if (lineLength + writeCount > lengthLimit)
            {
                throw new InvalidDataException($"Line length limit {lengthLimit} exceeded.");
            }
        }

        builder.Write(buffer.AsSpan(bufferOffset - writeCount, writeCount));
        lineLength += writeCount;
    }

    static string DecodeLine(MemoryStream builder, bool foundCrlf)
    {
        // Drop the final CRLF, if any.
        var length = foundCrlf ? builder.Length - 2 : builder.Length;
        return Encoding.UTF8.GetString(builder.GetBuffer(), 0, (int)length);
    }

    void CheckDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}
