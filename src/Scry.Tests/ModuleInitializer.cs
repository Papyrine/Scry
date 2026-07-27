using System.Text;
using System.Text.RegularExpressions;
using VerifyTests.DiffPlex;

namespace Scry.Tests;

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
    }

    static void ScrubCursors(StringBuilder builder)
    {
        var scrubbed = CursorValue().Replace(builder.ToString(), "$1\"{scrubbed cursor}\"");
        builder.Clear();
        builder.Append(scrubbed);
    }

    [GeneratedRegex("(\"cursor\":\\s*)\"[^\"]*\"")]
    private static partial Regex CursorValue();
}
