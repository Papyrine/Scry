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
    /// Sets the Monaco editor's content and leaves the caret at the end of it, which is where a user who
    /// typed the query would have left it. The caret matters because the explorer's completion list is the
    /// one for the caret's position: dropping text in without placing it would complete against the start
    /// of a query nobody wrote from the start. The query travels as a Playwright argument rather than being
    /// embedded in the evaluated JS, so a long query is never wrapped into a (syntactically invalid)
    /// multi-line JS string literal by the formatter.
    /// </summary>
    public static Task SetEditorValueAsync(this IPage page, string query) =>
        page.EvaluateAsync(
            """
            query => {
                const editor = monaco.editor.getEditors()[0];
                editor.setValue(query);
                const model = editor.getModel();
                const line = model.getLineCount();
                editor.setPosition({ lineNumber: line, column: model.getLineMaxColumn(line) });
            }
            """,
            query);
}
