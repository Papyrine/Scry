public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifyDiffPlex.Initialize(OutputType.Compact);
        VerifierSettings.Inline(maxLines: 10, applyMaxLinesToExisting: true);
        VerifierSettings.InitializePlugins();

        // Cursors and schema stamps, scrubbed the same way in both test projects — see
        // SnapshotScrubbers for what each is and why a snapshot is better off without it.
        SnapshotScrubbers.Register();
    }
}
