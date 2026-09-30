public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifySourceGenerators.Initialize();
        VerifierSettings.Inline(maxLines: 10, applyMaxLinesToExisting: true);
        VerifierSettings.InitializePlugins();
    }
}
