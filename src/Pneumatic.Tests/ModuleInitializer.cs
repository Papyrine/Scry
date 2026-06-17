using System.Runtime.CompilerServices;
using VerifyTests.DiffPlex;

namespace Pneumatic.Tests;

public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifyDiffPlex.Initialize(OutputType.Compact);
        VerifierSettings.InitializePlugins();
    }
}
