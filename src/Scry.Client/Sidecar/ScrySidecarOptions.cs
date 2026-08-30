namespace Scry;

/// <summary>
/// Options for the debug sidecar: the <see cref="ScrySidecar"/> panel and the
/// <see cref="ScrySidecarHandler"/> capture. Configured through
/// <see cref="ScrySidecarServiceExtensions.AddScrySidecar"/>.
/// </summary>
public sealed class ScrySidecarOptions
{
    // begin-snippet: sidecarOptions
    /// <summary>
    /// Whether exchanges are captured and the panel responds to its shortcut. On by default —
    /// turn it off for builds where a query log over the wire traffic is unwanted.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The keyboard shortcut that opens and hides the panel, as modifier tokens plus a key
    /// (for example <c>"Ctrl+Shift+D"</c>). An unrecognized value falls back to the default.
    /// </summary>
    public string ToggleShortcut { get; set; } = "Alt+Q";

    /// <summary>
    /// Decides whether the small floating button is shown in the page's corner while the panel
    /// is closed, as a clickable alternative to the shortcut. Shown to everyone by default —
    /// set <see cref="Never"/> to rely on the shortcut alone, or an own predicate to decide from
    /// the current context (the signed-in user, say). Evaluated once, when the panel first loads.
    /// </summary>
    public Func<IServiceProvider, ValueTask<bool>> ToggleButton { get; set; } = Always;

    /// <summary>
    /// Where the query explorer is mapped, for the "open in explorer" action on a captured
    /// query. Null hides the action.
    /// </summary>
    public string? ExplorerRoute { get; set; } = "/scry";

    /// <summary>Captured entries kept; the oldest is evicted beyond this.</summary>
    public int MaxEntries { get; set; } = 100;

    /// <summary>
    /// The client the attachment download action re-sends with. Defaults to a plain
    /// <see cref="HttpClient"/>, which is enough because captured URLs are absolute — supply
    /// one when the fetch needs the app's handler pipeline (an auth header, say).
    /// </summary>
    public Func<IServiceProvider, HttpClient>? DownloadClient { get; set; }
    // end-snippet

    /// <summary>Shows the toggle button to everyone. The default.</summary>
    public static ValueTask<bool> Always(IServiceProvider services) =>
        ValueTask.FromResult(true);

    /// <summary>Never shows the toggle button — the shortcut is the only way in.</summary>
    public static ValueTask<bool> Never(IServiceProvider services) =>
        ValueTask.FromResult(false);
}
