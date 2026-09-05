/// <summary>
/// Reads a response body into the one array the JSON reader parses and the response then keeps.
/// </summary>
/// <remarks>
/// Left to HttpClient, a body reaches a byte array by two copies: the connection's bytes into a
/// stream of HttpClient's own, then that stream into the array. Asked for the headers only, it hands
/// the connection's stream over as it is, and a body that declared its length is read straight into
/// an array of that size. The length sizes the array and is never trusted for the read: a body that
/// ends short of it is reported, not padded. Past the ceiling a claim is worth, or with no length
/// declared at all, the buffer grows with what arrives and is copied out once — which is still one
/// copy fewer than before.
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
        if (content.Headers.ContentLength is > 0 and <= PresizeCeiling)
        {
            var declared = (int) content.Headers.ContentLength;
            var exact = new byte[declared];
            var read = await stream.ReadAtLeastAsync(exact, declared, throwOnEndOfStream: false, cancel);
            if (read == declared)
            {
                return exact;
            }

            throw new ScryWireException(
                $"The response ended after {read} of the {declared} bytes its Content-Length declared.");
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancel);
        return buffer.ToArray();
    }
}
