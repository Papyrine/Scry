public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifierSettings.Inline(maxLines: 10, applyMaxLinesToExisting: true);
        VerifierSettings.InitializePlugins();

        // Cursors and schema stamps, scrubbed the same way in both test projects — see
        // SnapshotScrubbers for what each is and why a snapshot is better off without it.
        SnapshotScrubbers.Register();

        // A disclosure address is thirty-two bytes with no members to show, so a snapshot writes it
        // as the text it reads back from.
        VerifierSettings.AddExtraSettings(_ => _.Converters.Add(new DisclosureAddressConverter()));
    }
}
