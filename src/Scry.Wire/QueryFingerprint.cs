namespace Scry;

/// <summary>
/// The fingerprint of a request as it travels: SHA-256 over the exact UTF-8 bytes of the serialized
/// request, truncated to 12 bytes and base64url-encoded.
/// </summary>
/// <remarks>
/// <para>
/// Computed by the client over the bytes it is about to send and carried in
/// <see cref="WireFormat.QueryHashHeader"/>, so a server holding an identity for the request has not
/// had to hash the body itself. The server can always recompute it from the body it received, because
/// the input is defined as those bytes rather than as a canonical form of the AST.
/// </para>
/// <para>
/// A fingerprint of the <b>bytes</b>, deliberately. Two byte-different serializations of the same query
/// fingerprint differently, which is the safe direction for a cache key — it costs a miss, never a wrong
/// hit — and it is what lets the client's value and the server's recomputation agree without either side
/// needing a canonical form to maintain.
/// </para>
/// <para>
/// <b>Never trusted.</b> It arrives from a hostile client, so it identifies a request only as far as that
/// client is honest. It may be recorded, and it may be compared against a value this server itself
/// minted; it must never become a key into anything shared between clients. A client that sends a wrong
/// fingerprint can only mislead itself.
/// </para>
/// </remarks>
public static class QueryFingerprint
{
    /// <summary>
    /// Bytes of the digest kept, base64url-encoded into a 16-character value. 96 bits matches the schema
    /// stamp and the cursor's order stamp, and 12 divides by 3 so the base64 needs no padding — but the
    /// reasoning differs from theirs. Those are compared pairwise, so their birthday bound does not
    /// apply; a fingerprint keying a cache is looked up in a set, so it does. Over one client's cache of
    /// n queries the collision odds are about n²/2^97 — around 10^-21 at a hundred thousand entries — and
    /// the cost of losing that bet is one client reading its own stale row, never another's.
    /// </summary>
    const int stampBytes = 12;

    /// <summary>The longest header value accepted as a fingerprint. See <see cref="TryRead"/>.</summary>
    const int maxLength = 64;

    /// <summary>Fingerprints the serialized bytes of a request.</summary>
    public static string Compute(ReadOnlySpan<byte> utf8)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8, hash);

        return Base64Url.EncodeToString(hash[..stampBytes]);
    }

    /// <summary>
    /// Reads a fingerprint a client sent, or null when it sent none. Over-long values are dropped rather
    /// than read: the value is attacker-controlled and ends up in telemetry, so bounding it here keeps a
    /// client from writing an arbitrary payload into a trace through a header this server chose to read.
    /// </summary>
    public static string? TryRead(string? header) =>
        header is {Length: > 0 and <= maxLength} ? header : null;
}
