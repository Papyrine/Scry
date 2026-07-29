public static partial class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifyDiffPlex.Initialize(OutputType.Compact);
        VerifierSettings.InitializePlugins();

        // Keyset cursors are HMAC-signed with a per-process key, so their value is non-deterministic.
        // They are opaque anyway — scrub them to a stable placeholder so snapshots stay meaningful.
        VerifierSettings.AddScrubber(ScrubCursors);

        // Every response carries the server's schema stamp, a hash of the whole queryable surface.
        // Leaving it in would make every result snapshot churn on any unrelated change to the test
        // model. ResponseStampTests asserts the real value; here it is noise.
        VerifierSettings.AddScrubber(ScrubStamps);
    }

    static void ScrubCursors(StringBuilder builder) =>
        Replace(builder, CursorValue(), "$1\"{scrubbed cursor}\"");

    static void ScrubStamps(StringBuilder builder) =>
        Replace(builder, StampValue(), "$1\"{scrubbed stamp}\"");

    static void Replace(StringBuilder builder, Regex regex, string replacement)
    {
        var scrubbed = regex.Replace(builder.ToString(), replacement);
        builder.Clear();
        builder.Append(scrubbed);
    }

    [GeneratedRegex("(\"cursor\":\\s*)\"[^\"]*\"")]
    private static partial Regex CursorValue();

    [GeneratedRegex("(\"stamp\":\\s*)\"[^\"]*\"")]
    private static partial Regex StampValue();
}
