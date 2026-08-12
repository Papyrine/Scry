public static class ModuleInitializer
{
    /// <summary>
    /// Blazor's component boundary marker, and the source indentation trailing it. The marker says
    /// nothing about the rendered UI and there is one per binding; dropping it alone would leave that
    /// whitespace behind as a blank line, so the pair goes together. Safe to take the whitespace with
    /// it because every html snapshot here is pretty printed, which lays the markup out afresh.
    /// </summary>
    static readonly Regex componentMarkers = new("<!--!-->\\s*", RegexOptions.Compiled);

    [ModuleInitializer]
    public static void Init()
    {
        VerifyBunit.Initialize();
        // Downloads the Chromium build on first run so the UI tests work on a clean machine / CI.
        VerifyPlaywright.Initialize(installPlaywright: true);
        VerifyDiffPlex.Initialize(OutputType.Compact);
        VerifierSettings.UseSsimForPng();
        VerifierSettings.AddScrubber("html", Scrub);
        VerifierSettings.InitializePlugins();

        // Cursors and schema stamps, scrubbed the same way as in Scry.Tests — see SnapshotScrubbers
        // for what each is and why a snapshot is better off without it. The stamp matters here in
        // particular: these snapshots record a whole HTTP exchange and the introspection document, and
        // every one of them carried a hash of the entire model that rewrote itself whenever the sample
        // model gained a member.
        SnapshotScrubbers.Register();
    }

    static void Scrub(StringBuilder builder)
    {
        var scrubbed = componentMarkers.Replace(builder.ToString(), "");
        builder.Clear();
        builder.Append(scrubbed);
    }
}
