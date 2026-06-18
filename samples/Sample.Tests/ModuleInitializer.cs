using System.Runtime.CompilerServices;
using VerifyTests.DiffPlex;

namespace Sample.Tests;

public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifyBunit.Initialize();
        // Downloads the Chromium build on first run so the UI tests work on a clean machine / CI.
        VerifyPlaywright.Initialize(installPlaywright: true);
        VerifyDiffPlex.Initialize(OutputType.Compact);
        VerifierSettings.InitializePlugins();
    }
}
