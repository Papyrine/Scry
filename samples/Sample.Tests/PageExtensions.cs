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
}
