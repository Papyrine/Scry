// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Vendored from dotnet/aspnetcore, src/Http/WebUtilities, and adapted to this project's conventions.

/// <summary>A multipart section read by <see cref="MultipartReader"/>.</summary>
sealed class MultipartSection
{
    /// <summary>The value of the <c>Content-Type</c> header, or null.</summary>
    public string? ContentType =>
        Headers is not null && Headers.TryGetValue("Content-Type", out var value)
            ? value
            : null;

    /// <summary>The value of the <c>Content-Length</c> header, or null. Advisory — used to preallocate.</summary>
    public int? ContentLength =>
        Headers is not null &&
        Headers.TryGetValue("Content-Length", out var value) &&
        int.TryParse(value, out var length) &&
        length >= 0
            ? length
            : null;

    public Dictionary<string, string>? Headers { get; set; }

    public Stream Body { get; set; } = default!;
}
