using Microsoft.JSInterop;

/// <summary>
/// The single JS-to-C# callback hub, handed to <c>scry.init</c> as a DotNetObjectReference. The
/// events are raised off the document, or the window, rather than out of a component, so they are
/// routed here rather than bound in markup.
/// </summary>
public sealed class ScryCallbacks
{
    public event Action<string, double, double>? PaneResize;
    public event Action<string>? GlobalShortcut;
    /// <summary>The page is going away: whatever is waiting to be written is written now.</summary>
    public event Action? Flush;

    [JSInvokable]
    public void OnPaneResize(string resizerId, double fraction, double size) =>
        PaneResize?.Invoke(resizerId, fraction, size);

    [JSInvokable]
    public void OnGlobalShortcut(string id) =>
        GlobalShortcut?.Invoke(id);

    [JSInvokable]
    public void OnFlush() =>
        Flush?.Invoke();
}
