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

    /// <summary>
    /// Captured entries kept; the oldest is evicted beyond this. A live query still open is never
    /// the one evicted — its row is the one thing still being written to.
    /// </summary>
    public int MaxEntries { get; set; } = 100;

    /// <summary>
    /// How often a live query's row may redraw while it is open, and how often the times it shows
    /// are brought up to date. A floor on the work, not a wait for quiet: a live query answering
    /// steadily still redraws at this rate rather than never.
    /// </summary>
    public TimeSpan LiveRefresh { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Events kept per connection of a live query, heartbeats included; the oldest is dropped
    /// beyond this, and the row says how many went.
    /// </summary>
    public int MaxSubscriptionEvents { get; set; } = 200;

    /// <summary>
    /// Answers whose body is kept for display, per connection, most recent first. A connection that
    /// has been superseded keeps only its last — what an older connection answered is history the
    /// moment a newer one has answered too.
    /// </summary>
    public int MaxRetainedAnswers { get; set; } = 3;

    /// <summary>
    /// The largest answer whose body is kept. A longer one is listed with its size, and the panel
    /// says so rather than showing part of it as though it were the whole.
    /// </summary>
    public int MaxRetainedAnswerBytes { get; set; } = 16 * 1024;

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
