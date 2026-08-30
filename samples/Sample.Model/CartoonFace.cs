namespace Sample.Model;

/// <summary>
/// Draws the cartoon face that stands in for an employee's photo. Deterministic from the name, so
/// every machine seeds the same faces and the sample's snapshots do not move.
/// </summary>
/// <remarks>
/// A real photo is tens or hundreds of kilobytes, which is the whole argument for
/// <c>[Attachment]</c>: a value nothing wants travelling with every row of every query. These are a
/// few hundred bytes of SVG — enough to render, small enough to seed.
/// </remarks>
public static class CartoonFace
{
    static readonly string[] backgrounds = ["#dcecff", "#ffe3d4", "#dff3e0", "#efe1ff"];
    static readonly string[] skins = ["#f6d5b8", "#e0ac7e", "#c68642", "#8d5524"];
    static readonly string[] hairColors = ["#2f2a26", "#7b4a2d", "#c9a227", "#5b5f6b"];

    // Top of the head, drawn over the skin: a full cap, a receding sweep, and a spiked crop.
    static readonly string[] hairStyles =
    [
        "M13 36a19 19 0 0 1 38 0z",
        "M15 32a17 17 0 0 1 34 0q-9-7-17-7t-17 7z",
        "M13 33a19 19 0 0 1 38 0l-5-5-4 5-5-5-5 5-5-5-4 5-5-5z"
    ];

    // Open, and creased-shut the way a drawn smile does it.
    static readonly string[] eyeStyles =
    [
        """<circle cx="25" cy="35" r="2.4" fill="#2f2a26"/><circle cx="39" cy="35" r="2.4" fill="#2f2a26"/>""",
        """<path d="M22 36q3-4 6 0M36 36q3-4 6 0" fill="none" stroke="#2f2a26" stroke-width="2" stroke-linecap="round"/>"""
    ];

    static readonly string[] mouthStyles =
    [
        """<path d="M25 44q7 6 14 0" fill="none" stroke="#8c4a3f" stroke-width="2" stroke-linecap="round"/>""",
        """<path d="M25 43h14a7 7 0 0 1-14 0z" fill="#8c4a3f"/>""",
        """<path d="M27 45h10" fill="none" stroke="#8c4a3f" stroke-width="2" stroke-linecap="round"/>"""
    ];

    /// <summary>The bytes stored on the row: one SVG document, chosen by the name alone.</summary>
    public static byte[] For(string name)
    {
        var background = Pick(backgrounds, name, "background");
        var skin = Pick(skins, name, "skin");
        var hairColor = Pick(hairColors, name, "hairColor");
        var hairStyle = Pick(hairStyles, name, "hairStyle");
        var eyes = Pick(eyeStyles, name, "eyes");
        var mouth = Pick(mouthStyles, name, "mouth");

        var svg =
            $"""
             <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img"><title>{name}</title><circle cx="32" cy="32" r="32" fill="{background}"/><circle cx="12" cy="37" r="3.5" fill="{skin}"/><circle cx="52" cy="37" r="3.5" fill="{skin}"/><circle cx="32" cy="36" r="19" fill="{skin}"/><path d="{hairStyle}" fill="{hairColor}"/>{eyes}{mouth}</svg>
             """;

        return Encoding.UTF8.GetBytes(svg);
    }

    // Each feature draws from a hash of its own name, so two employees who happen to share a hair
    // colour are not thereby given the same eyes as well.
    static string Pick(string[] choices, string name, string feature) =>
        choices[Hash($"{name}/{feature}") % (uint) choices.Length];

    // FNV-1a. The framework's string hash is randomized per process, and a face that changed between
    // runs would be a face no snapshot could hold still.
    static uint Hash(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return hash;
    }
}
