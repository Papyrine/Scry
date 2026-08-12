/// <summary>
/// Ambient context for <see cref="PayloadConverter"/>: non-null exactly while an envelope is being
/// read from bytes the caller still holds (see <c>ScryJson.DeserializeResponse(ReadOnlyMemory&lt;byte&gt;)</c>),
/// collecting the byte range of each payload the read skipped over. Null everywhere else, which is
/// what leaves every other caller — a string body, a hand-built response, the server's own writes —
/// on the parse-it-now path unchanged.
/// </summary>
/// <remarks>
/// Ranges accumulate in read order. JSON is read front to back, so for a batch that is the order its
/// entries appear in, which is what lets the reader pair them back up with the results. Sized for the
/// single-response case; a batch grows it once.
/// </remarks>
static class PayloadRangeScope
{
    [ThreadStatic]
    static List<(int Start, int End)>? ranges;

    /// <summary>Whether a payload read should skip rather than parse.</summary>
    public static bool Active => ranges is not null;

    public static void Begin() =>
        ranges = [];

    /// <summary>Records one payload's byte range, as offsets into the buffer the read started over.</summary>
    public static void Record(int start, int end) =>
        ranges!.Add((start, end));

    /// <summary>
    /// Ends the scope and returns what was recorded, in read order. Idempotent, so a reader can end it
    /// on the success path and again in a finally without the second call having to be guarded.
    /// </summary>
    public static List<(int Start, int End)> End()
    {
        var recorded = ranges;
        ranges = null;
        return recorded ?? [];
    }
}
