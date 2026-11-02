/// <summary>
/// Reads a response body into the one array the JSON reader parses and the response then keeps.
/// </summary>
/// <remarks>
/// Left to HttpClient, a body reaches a byte array by two copies: the connection's bytes into a
/// stream of HttpClient's own, then that stream into the array. Asked for the headers only, it hands
/// the connection's stream over as it is, and a body that declared its length is read straight into
/// an array of that size. The length sizes the array and is never trusted for the read: a body that
/// ends short of it is reported, not padded, and one that goes on past it is read to its end. Past
/// the ceiling a claim is worth, with no length declared at all, or with a content encoding — whose
/// length is the encoded body's, where the stream read is the decoded one — the buffer grows with
/// what arrives and is copied out once, which is still one copy fewer than before.
/// </remarks>
static class ResponseBody
{
    /// <summary>
    /// The most a declared length pre-sizes the array. A server's claim about its own response is
    /// worth an allocation up to here; past it the buffer grows with the bytes that actually arrive,
    /// so a length that lies costs at most what it goes on to send.
    /// </summary>
    internal const int PresizeCeiling = 64 * 1024 * 1024;

    public static async Task<byte[]> ReadAsync(HttpContent content, Cancel cancel)
    {
        await using var stream = await content.ReadAsStreamAsync(cancel);
        if (content.Headers.ContentLength is > 0 and <= PresizeCeiling &&
            content.Headers.ContentEncoding.Count == 0)
        {
            var declared = (int) content.Headers.ContentLength;
            var exact = new byte[declared];
            var read = await stream.ReadAtLeastAsync(exact, declared, throwOnEndOfStream: false, cancel);
            if (read < declared)
            {
                throw new ScryWireException(
                    $"The response ended after {read} of the {declared} bytes its Content-Length declared.");
            }

            // Asked once more, which at the end of a body that kept to its length answers nothing
            // at once. A body longer than its declared length — decoded by a handler that left the
            // encoded length in place — is read to its end rather than cut where the length said.
            var next = new byte[1];
            if (await stream.ReadAsync(next, cancel) == 0)
            {
                return exact;
            }

            using var longer = new MemoryStream(declared * 2);
            longer.Write(exact);
            longer.Write(next);
            await stream.CopyToAsync(longer, cancel);
            return longer.ToArray();
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancel);
        return buffer.ToArray();
    }
}
