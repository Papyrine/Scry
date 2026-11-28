public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifierSettings.Inline(maxLines: 10, applyMaxLinesToExisting: true);

        // The wire vocabulary is snapshotted as an ES module the TypeScript client imports directly:
        // .mts rather than .ts, since nothing above it marks a .ts file as a module.
        EmptyFiles.FileExtensions.AddTextExtension("mts");
        VerifierSettings.InitializePlugins();

        // Cursors and schema stamps, scrubbed the same way in both test projects — see
        // SnapshotScrubbers for what each is and why a snapshot is better off without it.
        SnapshotScrubbers.Register();

        // A disclosure address is thirty-two bytes with no members to show, so a snapshot writes it
        // as the text it reads back from.
        VerifierSettings.AddExtraSettings(_ => _.Converters.Add(new DisclosureAddressConverter()));
    }
}
