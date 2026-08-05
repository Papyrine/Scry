// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Vendored from dotnet/aspnetcore, src/Http/WebUtilities, and adapted to this project's conventions.

using System.Buffers;
using System.Diagnostics;

sealed class MultipartReaderStream :
    Stream
{
    readonly MultipartBoundary boundary;
    readonly BufferedReadStream innerStream;
    readonly ArrayPool<byte> bytePool;

    readonly long innerOffset;
    long position;
    long observedLength;
    bool finished;

    /// <summary>Creates a stream that reads until it reaches the given boundary pattern.</summary>
    public MultipartReaderStream(BufferedReadStream stream, MultipartBoundary boundary)
        : this(stream, boundary, ArrayPool<byte>.Shared)
    {
    }

    /// <summary>Creates a stream that reads until it reaches the given boundary pattern.</summary>
    public MultipartReaderStream(BufferedReadStream stream, MultipartBoundary boundary, ArrayPool<byte> bytePool)
    {
        this.bytePool = bytePool;
        innerStream = stream;
        innerOffset = innerStream.CanSeek ? innerStream.Position : 0;
        this.boundary = boundary;
    }

    public bool FinalBoundaryFound { get; private set; }

    public long? LengthLimit { get; set; }

    public override bool CanRead => true;

    public override bool CanSeek => innerStream.CanSeek;

    public override bool CanWrite => false;

    public override long Length => observedLength;

    public override long Position
    {
        get => position;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The Position must be positive.");
            }

            if (value > observedLength)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The Position must be less than length.");
            }

            position = value;
            if (position < observedLength)
            {
                finished = false;
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
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, Cancel cancellationToken = default) =>
        throw new NotSupportedException();

    public override Task WriteAsync(byte[] buffer, int offset, int count, Cancel cancellationToken) =>
        throw new NotSupportedException();

    public override void Flush() =>
        throw new NotSupportedException();

    void PositionInnerStream()
    {
        if (innerStream.CanSeek &&
            innerStream.Position != innerOffset + position)
        {
            innerStream.Position = innerOffset + position;
        }
    }

    int UpdatePosition(int read)
    {
        position += read;
        if (observedLength < position)
        {
            observedLength = position;
            if (LengthLimit.HasValue &&
                LengthLimit.GetValueOrDefault() is var lengthLimit &&
                observedLength > lengthLimit)
            {
                // If we hit the limit before the first boundary then we're using the header length
                // limit, not the body length limit.
                if (boundary.BeforeFirstBoundary())
                {
                    throw new InvalidDataException($"Multipart header length limit {lengthLimit} exceeded. Too much data before the first boundary.");
                }

                throw new InvalidDataException($"Multipart body length limit {lengthLimit} exceeded.");
            }
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (finished)
        {
            return 0;
        }

        PositionInnerStream();
        if (!innerStream.EnsureBuffered(boundary.FinalBoundaryLength))
        {
            throw new IOException("Unexpected end of Stream, the content may have already been read by another component. ");
        }

        var bufferedData = innerStream.BufferedData;

        var index = bufferedData.AsSpan().IndexOf(boundary.BoundaryBytes);
        if (index >= 0)
        {
            // There is data before the boundary, we should return it to the user.
            if (index != 0)
            {
                // Sync, it's already buffered.
                var slice = buffer.AsSpan(offset, Math.Min(count, index));

                var readAmount = innerStream.Read(slice);
                return UpdatePosition(readAmount);
            }

            return ReadBoundary(this, boundary.BoundaryBytes.Length);
        }

        // Scan for a partial boundary match.
        int read;
        if (SubMatch(bufferedData, boundary.BoundaryBytes, out var matchOffset, out var matchCount))
        {
            // We found a possible match, return any data before it.
            if (matchOffset > bufferedData.Offset)
            {
                read = innerStream.Read(buffer, offset, Math.Min(count, matchOffset - bufferedData.Offset));
                return UpdatePosition(read);
            }

            Debug.Assert(matchCount == boundary.BoundaryBytes.Length);

            return ReadBoundary(this, boundary.BoundaryBytes.Length);
        }

        // No possible boundary match within the buffered data, return the data from the buffer.
        read = innerStream.Read(buffer, offset, Math.Min(count, bufferedData.Count));
        return UpdatePosition(read);

        static int ReadBoundary(MultipartReaderStream stream, int length)
        {
            // "The boundary may be followed by zero or more characters of linear whitespace. It is
            // then terminated by either another CRLF" or -- for the final boundary.
            var boundary = stream.bytePool.Rent(length);
            var read = stream.innerStream.Read(boundary, 0, length);
            stream.bytePool.Return(boundary);
            // It should have all been buffered.
            Debug.Assert(read == length);

            // Whitespace may exceed the buffer.
            var remainder = stream.innerStream.ReadLine(lengthLimit: 100).AsSpan();
            remainder = remainder.Trim();
            if (remainder.Equals("--", StringComparison.Ordinal))
            {
                stream.FinalBoundaryFound = true;
            }

            if (!stream.FinalBoundaryFound &&
                !remainder.IsEmpty)
            {
                throw new IOException("Unexpected data found on the boundary line.");
            }

            stream.finished = true;
            return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, Cancel cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, Cancel cancellationToken)
    {
        if (finished)
        {
            return 0;
        }

        PositionInnerStream();
        if (!await innerStream.EnsureBufferedAsync(boundary.FinalBoundaryLength, cancellationToken))
        {
            throw new IOException("Unexpected end of Stream, the content may have already been read by another component. ");
        }

        var bufferedData = innerStream.BufferedData;

        var index = bufferedData.AsSpan().IndexOf(boundary.BoundaryBytes);

        if (index >= 0)
        {
            // There is data before the boundary, we should return it to the user.
            if (index != 0)
            {
                var slice = buffer[..Math.Min(buffer.Length, index)];

                // Sync, it's already buffered.
                var readAmount = innerStream.Read(slice.Span);
                return UpdatePosition(readAmount);
            }

            return await ReadBoundaryAsync(this, boundary.BoundaryBytes.Length, cancellationToken);
        }

        // Scan for a boundary match, full or partial.
        int read;
        if (SubMatch(bufferedData, boundary.BoundaryBytes, out var matchOffset, out var matchCount))
        {
            // We found a possible match, return any data before it.
            if (matchOffset > bufferedData.Offset)
            {
                var slice = buffer[..Math.Min(buffer.Length, matchOffset - bufferedData.Offset)];

                // Sync, it's already buffered.
                read = innerStream.Read(slice.Span);
                return UpdatePosition(read);
            }

            Debug.Assert(matchCount == boundary.BoundaryBytes.Length);

            return await ReadBoundaryAsync(this, boundary.BoundaryBytes.Length, cancellationToken);
        }

        // No possible boundary match within the buffered data, return the data from the buffer.
        read = innerStream.Read(buffer.Span[..Math.Min(buffer.Length, bufferedData.Count)]);
        return UpdatePosition(read);

        static async Task<int> ReadBoundaryAsync(MultipartReaderStream stream, int length, Cancel cancellationToken)
        {
            // "The boundary may be followed by zero or more characters of linear whitespace. It is
            // then terminated by either another CRLF" or -- for the final boundary.
            var boundary = stream.bytePool.Rent(length);
            var read = stream.innerStream.Read(boundary, 0, length);
            stream.bytePool.Return(boundary);
            // It should have all been buffered.
            Debug.Assert(read == length);

            // Whitespace may exceed the buffer.
            var remainder = await stream.innerStream.ReadLineAsync(lengthLimit: 100, cancellationToken);
            remainder = remainder.Trim();
            if (string.Equals("--", remainder, StringComparison.Ordinal))
            {
                stream.FinalBoundaryFound = true;
            }

            if (!stream.FinalBoundaryFound &&
                !string.Equals(string.Empty, remainder, StringComparison.Ordinal))
            {
                throw new IOException("Unexpected data found on the boundary line.");
            }

            stream.finished = true;
            return 0;
        }
    }

    // Does segment1 end with the start of matchBytes?
    // 1: AAAAABBB
    // 2:      BBBBB
    static bool SubMatch(ArraySegment<byte> segment1, ReadOnlySpan<byte> matchBytes, out int matchOffset, out int matchCount)
    {
        matchOffset = Math.Max(segment1.Offset, segment1.Offset + segment1.Count - matchBytes.Length);
        var segmentEnd = segment1.Offset + segment1.Count;

        matchCount = 0;
        for (; matchOffset < segmentEnd; matchOffset++)
        {
            var countLimit = segmentEnd - matchOffset;
            for (matchCount = 0; matchCount < matchBytes.Length && matchCount < countLimit; matchCount++)
            {
                if (matchBytes[matchCount] != segment1.Array![matchOffset + matchCount])
                {
                    matchCount = 0;
                    break;
                }
            }

            if (matchCount > 0)
            {
                break;
            }
        }

        return matchCount > 0;
    }

    public override void CopyTo(Stream destination, int bufferSize)
    {
        bufferSize = Math.Max(4096, bufferSize);
        base.CopyTo(destination, bufferSize);
    }

    public override Task CopyToAsync(Stream destination, int bufferSize, Cancel cancellationToken)
    {
        // Set a minimum buffer size of 4K since the base Stream implementation has weird behavior
        // when the stream is seekable *and* the length is 0 (it passes in a buffer size of 1).
        bufferSize = Math.Max(4096, bufferSize);
        return base.CopyToAsync(destination, bufferSize, cancellationToken);
    }
}
