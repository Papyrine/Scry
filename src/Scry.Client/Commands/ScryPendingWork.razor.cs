using Microsoft.AspNetCore.Components;

namespace Scry;

/// <summary>
/// The commands the client stopped waiting for while the server was still handling them, in a panel
/// that opens as one goes pending and shows each to its end: how long it has taken, and how it came out.
/// Render it once, beside the router. Renders nothing but its stylesheet while there is nothing to show.
/// </summary>
/// <remarks>
/// A command the server decides within <see cref="ScryClient.CommandWait"/> never appears here — its
/// sender has the outcome in hand. What a command wrote reaches the page through the live queries that
/// read it, not through this panel, which says only what became of the command.
/// </remarks>
public partial class ScryPendingWork :
    IDisposable
{
    bool open;
    int pendingSeen;
    CancelSource? ticking;
    ScryPendingWorkStore? work;

    // What the store listed at its last change. Taken once per change rather than read from the store
    // as the markup draws, which would copy it for every read and could draw a count and rows that
    // disagree. The elapsed times tick without it: each row reads its own.
    IReadOnlyList<ScryPendingCommand> items = [];

    bool Showing => items.Count > 0;

    int PendingCount => items.Count(_ => _.Status == ScryCommandStatus.Pending);

    [Inject]
    IServiceProvider Services { get; set; } = null!;

    /// <summary>
    /// The store to list: the client's <see cref="ScryClient.PendingWork"/>. The one registered beside
    /// the client by <c>AddScryClient</c> unless set.
    /// </summary>
    [Parameter]
    public ScryPendingWorkStore? Store { get; set; }

    /// <summary>Whether the panel opens by itself when a command goes pending. On unless set.</summary>
    [Parameter]
    public bool AutoOpen { get; set; } = true;

    /// <summary>
    /// The stylesheet the panel is drawn with, as <see cref="ScryCommandStyles.Href"/>: the package's
    /// own unless set, and null to link none.
    /// </summary>
    [Parameter]
    public string? StylesHref { get; set; } = ScryCommandStyles.DefaultHref;

    protected override void OnParametersSet()
    {
        var next = Store ??
                   Services.GetService<ScryPendingWorkStore>() ??
                   throw new InvalidOperationException(
                       "ScryPendingWork needs a ScryPendingWorkStore: pass the client's PendingWork as its Store, or register the client with AddScryClient, which registers the store beside it.");
        if (ReferenceEquals(next, work))
        {
            return;
        }

        work?.Changed -= OnChanged;
        work = next;
        work.Changed += OnChanged;
        items = work.Items;
        pendingSeen = PendingCount;
        Tick();
    }

    // Raised where the command was sent, which in a component is the renderer's own context already;
    // InvokeAsync makes it so wherever it was.
    void OnChanged() =>
        InvokeAsync(
            () =>
            {
                items = work!.Items;
                var pending = PendingCount;
                if (AutoOpen &&
                    pending > pendingSeen)
                {
                    open = true;
                }

                pendingSeen = pending;
                Tick();
                StateHasChanged();
            });

    // The elapsed times count up while anything is pending, and the clock stops once nothing is.
    void Tick()
    {
        if (ticking is not null ||
            work is not {PendingCount: > 0})
        {
            return;
        }

        ticking = new();
        _ = Run(ticking);
    }

    async Task Run(CancelSource source)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(source.Token))
            {
                await InvokeAsync(StateHasChanged);
                if (work is not {PendingCount: > 0})
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (ObjectDisposedException)
        {
            // The renderer went first.
        }
        finally
        {
            if (ReferenceEquals(ticking, source))
            {
                ticking = null;
            }

            source.Dispose();
        }
    }

    void Open() =>
        open = true;

    void Close() =>
        open = false;

    void ClearFinished() =>
        work?.ClearFinished();

    static string Status(ScryPendingCommand item) =>
        item.Status switch
        {
            ScryCommandStatus.Pending => "pending",
            ScryCommandStatus.Completed => "done",
            ScryCommandStatus.Failed => "failed",
            _ => "unknown"
        };

    static string Name(ScryPendingCommand item)
    {
        if (item.Target is not { } target)
        {
            return item.Command;
        }

        return $"{item.Command} · {target} {string.Join(", ", item.Keys)}";
    }

    static string Elapsed(ScryPendingCommand item)
    {
        var span = item.Elapsed;
        if (span.TotalMinutes < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.0}s");
        }

        return $"{(int) span.TotalMinutes}m {span.Seconds}s";
    }

    public void Dispose()
    {
        work?.Changed -= OnChanged;
        ticking?.Cancel();
    }
}
