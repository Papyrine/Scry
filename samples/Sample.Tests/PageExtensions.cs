/// <summary>Playwright conveniences for the browser tests.</summary>
static class PageExtensions
{
    /// <summary>
    /// Waits for <paramref name="selector"/> to appear, timing out after <paramref name="seconds"/>
    /// seconds — a terser overload of Playwright's millisecond options-object form.
    /// </summary>
    public static Task<IElementHandle?> WaitForSelectorAsync(this IPage page, string selector, int seconds) =>
        page.WaitForSelectorAsync(
            selector,
            new()
            {
                Timeout = seconds * 1000
            });

    /// <summary>
    /// Sets the Monaco editor's content. The query travels as a Playwright argument rather than being
    /// embedded in the evaluated JS, so a long query is never wrapped into a (syntactically invalid)
    /// multi-line JS string literal by the formatter.
    /// </summary>
    public static Task SetEditorValueAsync(this IPage page, string query) =>
        page.EvaluateAsync("query => monaco.editor.getEditors()[0].setValue(query)", query);
}
