// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Vendored from dotnet/aspnetcore, src/Http/WebUtilities, and adapted to this project's conventions.

sealed class MultipartBoundary
{
    readonly byte[] boundaryBytes;
    bool expectLeadingCrlf;

    public MultipartBoundary(string boundary)
    {
        expectLeadingCrlf = false;
        boundaryBytes = Encoding.UTF8.GetBytes("\r\n--" + boundary);

        // Include the final '--' terminator.
        FinalBoundaryLength = BoundaryBytes.Length + 2;
    }

    public void ExpectLeadingCrlf() =>
        expectLeadingCrlf = true;

    // Lets MultipartReaderStream throw a more specific error when reading any preamble data.
    public bool BeforeFirstBoundary() =>
        !expectLeadingCrlf;

    // Either "--{boundary}" or "\r\n--{boundary}" depending on whether we're looking for the end of a section.
    public ReadOnlySpan<byte> BoundaryBytes =>
        boundaryBytes.AsSpan(expectLeadingCrlf ? 0 : 2);

    public int FinalBoundaryLength { get; }
}
