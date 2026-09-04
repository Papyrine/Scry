

/// <summary>
/// The scrubbers every snapshot in this repository shares. Compiled into both test projects rather
/// than written once per project: they exist to keep unrelated churn out of verified files, and two
/// copies that drifted would put that churn back into whichever one fell behind.
/// </summary>
/// <remarks>
/// <para>
/// Both values scrubbed here are ones a snapshot cannot assert anything useful about. A keyset cursor
/// is HMAC-signed with a per-process key, so it is not even stable between runs. A schema stamp is
/// stable, but it is a hash over the <b>whole</b> queryable surface — so any change to the model, of
/// any kind, anywhere, rewrites it in every snapshot that carries it. That churn is worse than
/// useless: re-accepting a baseline that moved for a reason unrelated to what the test asserts is how
/// a reviewer learns to accept received files without reading them, and in this repository the
/// verified files are the specification.
/// </para>
/// <para>
/// The stamp's real value is asserted where it means something. <c>ResponseStampTests</c> pins that a
/// response carries the server's own stamp, and <c>IntrospectionTests.Describe</c> keeps its literal
/// one — that snapshot spells out the entire queryable surface directly above it, so there the stamp
/// is a checksum over content the same file already shows, and it is the one place proving the stamp
/// tracks the surface rather than being arbitrary. It survives this scrubber because Verify writes an
/// object's members unquoted, and every pattern below requires the quotes of JSON.
/// </para>
/// </remarks>
static partial class SnapshotScrubbers
{
    /// <summary>Registers every shared scrubber. Called from each project's module initializer.</summary>
    public static void Register()
    {
        VerifierSettings.AddScrubber(ScrubCursors);
        VerifierSettings.AddScrubber(ScrubStamps);

        // Registered last so that it runs first: each scrubber goes to the front of the list, so the
        // order they execute in is the reverse of the order they are added. That order matters here and
        // only here — a query asked as a URL carries the whole request base64url-encoded, and the stamp
        // inside it is unreachable while it stays encoded. Decoded before the scrubbers above run, the
        // stamp is scrubbed like any other and the request is legible again; decoded after, every model
        // change would rewrite the blob in every recorded exchange.
        VerifierSettings.AddScrubber(DecodeUrlQueries);

        // The stamp reaches a snapshot one further way, as the HTTP header on a recorded exchange, and
        // that one a scrubber cannot reach: a scrubber is handed each string value on its own, so a
        // header's value arrives with nothing of its name attached to match against. Named here
        // instead, which keeps the header itself in the snapshot — that the server advertises a stamp
        // at all is worth showing — and masks only what it carries.
        VerifierSettings.ScrubMember(WireFormat.SchemaStampHeader);
    }

    /// <summary>
    /// Replaces an encoded request in a recorded URL with the JSON it decodes to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched by its own shape rather than by the parameter it sits in, because a scrubber is handed
    /// each string value on its own — and a recorded URL reaches one already split, so the encoded
    /// request arrives with no <c>q=</c> in front of it to anchor on.
    /// </para>
    /// <para>
    /// The opening <c>eyJ</c> is base64url of <c>{"</c>, which is not enough on its own: a keyset
    /// cursor opens the same way, and decoding one would corrupt the value the cursor scrubber is
    /// about to replace. So what it decoded to has to look like a request before it is used, and
    /// anything else — a cursor, a coincidence, something that does not decode at all — is left
    /// exactly as it was rather than half-rewritten.
    /// </para>
    /// </remarks>
    static void DecodeUrlQueries(StringBuilder builder) =>
        Replace(
            builder,
            UrlQuery(),
            _ =>
            {
                try
                {
                    var utf8 = Base64Url.DecodeFromChars(_.Value);
                    var json = Encoding.UTF8.GetString(utf8);
                    if (json.StartsWith("{\"version\":", StringComparison.Ordinal))
                    {
                        return json;
                    }

                    return _.Value;
                }
                catch (FormatException)
                {
                    return _.Value;
                }
            });

    static void ScrubCursors(StringBuilder builder) =>
        Replace(builder, CursorValue(), "$1\"{scrubbed cursor}\"");

    // Two spellings reach a snapshot as JSON text: the response and request member, and the
    // introspection document's own name for it. The header is handled in Register.
    static void ScrubStamps(StringBuilder builder) =>
        Replace(builder, StampValue(), "$1\"{scrubbed stamp}\"");

    static void Replace(StringBuilder builder, Regex regex, string replacement)
    {
        var scrubbed = regex.Replace(builder.ToString(), replacement);
        builder.Clear();
        builder.Append(scrubbed);
    }

    static void Replace(StringBuilder builder, Regex regex, MatchEvaluator evaluator)
    {
        var scrubbed = regex.Replace(builder.ToString(), evaluator);
        builder.Clear();
        builder.Append(scrubbed);
    }

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{40,}")]
    private static partial Regex UrlQuery();

    [GeneratedRegex("(\"cursor\":\\s*)\"[^\"]*\"")]
    private static partial Regex CursorValue();

    [GeneratedRegex("(\"(?:schemaStamp|stamp)\":\\s*)\"[^\"]*\"")]
    private static partial Regex StampValue();
}
