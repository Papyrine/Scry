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
    /// Opens the explorer and waits until it can answer a completion: Monaco mounted, the schema fetched,
    /// and the editor's Roslyn providers registered — which is what the shell's <c>data-ready</c> marks.
    /// Downloading and booting the WASM runtime is slow on a cold load, hence the long wait.
    /// </summary>
    public static async Task GoToExplorerAsync(this IPage page, string baseUrl)
    {
        await page.GotoAsync($"{baseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("main[data-ready]", 90);
    }

    /// <summary>
    /// Opens Monaco's completion dropdown at the caret and returns the labels it is showing, in order.
    /// </summary>
    /// <remarks>
    /// The dropdown is the explorer's completion surface, so driving it the way a user does is what proves
    /// Roslyn answered inside WASM.
    ///
    /// The widget virtualizes: only the rows on screen are in the DOM, roughly a dozen of them. Assert
    /// against a list narrowed by a typed prefix, or against a name near the front of the order, rather
    /// than against a whole member list — anything past the fold is simply not in the markup to find.
    ///
    /// The first call on a page pays for Roslyn's cold pass in the interpreter, which is slow, hence the
    /// long default.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> SuggestAsync(this IPage page, int seconds = 90)
    {
        await page.EvaluateAsync(
            """
            () => {
                const editor = monaco.editor.getEditors()[0];
                editor.focus();
                editor.trigger('test', 'editor.action.triggerSuggest', {});
            }
            """);

        await page.WaitForSelectorAsync(".suggest-widget .monaco-list-row", seconds);
        return await page.SuggestionsAsync();
    }

    /// <summary>The labels the completion dropdown is showing, without asking for it to be opened.</summary>
    public static async Task<IReadOnlyList<string>> SuggestionsAsync(this IPage page) =>
        await page.EvaluateAsync<string[]>(
            """
            () => Array.from(document.querySelectorAll('.suggest-widget .monaco-list-row'))
                // A row's own text carries the type column beside the label; the label is its own element.
                .map(row => row.querySelector('.label-name'))
                .filter(label => label)
                .map(label => label.textContent.trim())
            """);

    /// <summary>The label of the suggestion the dropdown has focused — the one Enter would accept.</summary>
    public static Task<string> FocusedSuggestionAsync(this IPage page) =>
        page.EvaluateAsync<string>(
            "() => document.querySelector('.suggest-widget .monaco-list-row.focused .label-name').textContent.trim()");

    /// <summary>Accepts the focused suggestion, as a user pressing Enter on the open dropdown does.</summary>
    public static Task AcceptSuggestionAsync(this IPage page) =>
        page.Keyboard.PressAsync("Enter");

    /// <summary>
    /// Sets the Monaco editor's content and leaves the caret at the end of it, which is where a user who
    /// typed the query would have left it. The caret matters because completion is the one for the caret's
    /// position: dropping text in without placing it would complete against the start of a query nobody
    /// wrote from the start. The query travels as a Playwright argument rather than being embedded in the
    /// evaluated JS, so a long query is never wrapped into a (syntactically invalid) multi-line JS string
    /// literal by the formatter.
    /// </summary>
    /// <summary>
    /// Selects one of the output column's tabs. The panes are mutually exclusive now, and only the
    /// selected one is in the DOM, so a test reading the response has to ask for it first.
    /// </summary>
    public static async Task SelectOutputTabAsync(this IPage page, string tab)
    {
        var button = page.Locator($"[data-testid='output-tab-{tab}']");
        await button.WaitForAsync(new() {Timeout = 60_000});
        await button.ClickAsync();
    }

    /// <summary>
    /// Opens the history pane if it is not already showing. The rail's panes are one at a time and the
    /// explorer opens on the schema, so anything asserting about the history has to ask for it — and
    /// idempotently, because a test may run several queries.
    /// </summary>
    public static async Task ShowHistoryAsync(this IPage page)
    {
        if (await page.Locator("[data-testid='history-pane']").CountAsync() == 0)
        {
            await page.Locator("[data-testid='rail-history']").ClickAsync();
        }
    }

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
