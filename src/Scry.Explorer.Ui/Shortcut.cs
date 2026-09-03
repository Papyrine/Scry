/// <summary>
/// One document-level shortcut, in the shape <c>registerGlobalShortcuts</c> in scry.js reads. These
/// are the commands that live outside the editor, so Monaco's own keybindings cannot carry them.
/// </summary>
sealed record Shortcut(string Id, string Key, bool Ctrl, bool Shift, bool Alt, bool Meta)
{
    // camelCase because the reader is JavaScript, which is where these are matched: the default
    // PascalCase leaves every property undefined on the other side, and a listener comparing
    // undefined matches nothing at all.
    static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(IReadOnlyList<Shortcut> shortcuts) =>
        JsonSerializer.Serialize(shortcuts, options);
}
