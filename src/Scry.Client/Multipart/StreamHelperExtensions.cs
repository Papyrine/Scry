// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Vendored from dotnet/aspnetcore, src/Http/WebUtilities, and adapted to this project's conventions.

using System.Buffers;

/// <summary>HTTP extension methods for <see cref="Stream"/>.</summary>
static class StreamHelperExtensions
{
    const int maxReadBufferSize = 1024 * 4;

    /// <summary>Reads the specified <paramref name="stream"/> to the end.</summary>
    public static Task DrainAsync(this Stream stream, Cancel cancellationToken) =>
        stream.DrainAsync(limit: null, cancellationToken);

    /// <summary>
    /// Reads the specified <paramref name="stream"/> to the end, throwing if it is larger than
    /// <paramref name="limit"/>.
    /// </summary>
    public static async Task DrainAsync(this Stream stream, long? limit, Cancel cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = ArrayPool<byte>.Shared.Rent(maxReadBufferSize);
        long total = 0;
        try
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            while (read > 0)
            {
                // Not all streams support cancellation directly.
                cancellationToken.ThrowIfCancellationRequested();
                if (limit.HasValue &&
                    limit.GetValueOrDefault() - total < read)
                {
                    throw new InvalidDataException($"The stream exceeded the data limit {limit.GetValueOrDefault()}.");
                }

                total += read;
                read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
