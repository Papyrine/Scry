namespace Scry;

/// <summary>
/// The debug sidecar panel: lists the exchanges <see cref="ScrySidecarHandler"/> has captured,
/// toggled by <see cref="ScrySidecarOptions.ToggleShortcut"/>. Rendered once, above the router.
/// Renders nothing while closed.
/// </summary>
public partial class ScrySidecar :
    IAsyncDisposable
{
    bool open;
    bool toggleButton;
    int selectedId;
    int? selectedAttempt;
    ScrySidecarEvent? selectedEvent;
    HashSet<int> expanded = [];
    bool dirty;
    string? note;
    IJSObjectReference? module;
    DotNetObjectReference<ScrySidecar>? reference;
    HttpClient? fallbackDownloadClient;

    ScrySidecarEntry? Selected =>
        Store.Entries.FirstOrDefault(_ => _.Id == selectedId);

    ScrySidecarConnection? SelectedConnection =>
        selectedAttempt is { } attempt
            ? Selected?.Session?.Connections.FirstOrDefault(_ => _.Attempt == attempt)
            : null;

    ScrySidecarEvent? SelectedEvent => selectedEvent;

    protected override void OnInitialized()
    {
        Store.Changed += OnChanged;
        Store.SessionChanged += OnSessionChanged;
    }

    // Nothing of the log renders while the panel is shut, so nothing needs re-rendering for it.
    void OnChanged()
    {
        if (open)
        {
            InvokeAsync(StateHasChanged);
        }
    }

    // A live query reports every event it receives, which for a page holding several is far oftener
    // than a panel can usefully repaint. The tick below picks these up in batches.
    void OnSessionChanged() =>
        dirty = true;

    /// <summary>
    /// Repaints the open panel on a fixed beat rather than per event: a floor on how often, not a
    /// wait for quiet, because a live query answering steadily must still redraw. The beat also
    /// brings the times a row shows up to date, which nothing else would — a live query that has
    /// gone quiet is exactly the one whose "last event" needs to keep counting up.
    /// </summary>
    async Task Tick()
    {
        using var ticker = new PeriodicTimer(Options.LiveRefresh);
        try
        {
            while (open &&
                   await ticker.WaitForNextTickAsync())
            {
                if (!dirty &&
                    !Store.Entries.Any(_ => _.Session is {Open: true}))
                {
                    continue;
                }

                dirty = false;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception)
        {
            // The panel was torn down under a repaint it had already scheduled.
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Disabled means fully inert: no module, no key listener, nothing rendered — the same page
        // the app would show without the sidecar.
        if (!firstRender || !Options.Enabled)
        {
            return;
        }

        // The button is contextual — the option is a predicate so an app can key it off the
        // current user. Decided once, here; an answer that should change mid-session belongs on
        // the markup instead (render <ScrySidecar /> inside the condition).
        toggleButton = await Options.ToggleButton(Services);

        reference = DotNetObjectReference.Create(this);
        module = await JS.InvokeAsync<IJSObjectReference>(
            "import",
            "./_content/Scry.Client/Sidecar/ScrySidecar.razor.js");
        await module.InvokeVoidAsync("init", reference, Options.ToggleShortcut, toggleButton);
        if (toggleButton)
        {
            StateHasChanged();
        }
    }

    [JSInvokable]
    public Task Toggle() =>
        InvokeAsync(() =>
        {
            open = !open;
            note = null;
            StateHasChanged();
            if (open)
            {
                _ = Tick();
            }
        });

    void Select(int id)
    {
        selectedId = id;
        selectedAttempt = null;
        selectedEvent = null;
        note = null;
    }

    void Select(int id, int attempt)
    {
        selectedId = id;
        selectedAttempt = attempt;
        selectedEvent = null;
        note = null;
    }

    void Show(ScrySidecarEvent captured) =>
        selectedEvent = captured.Json is null || captured == selectedEvent ? null : captured;

    void Expand(int id)
    {
        if (!expanded.Add(id))
        {
            expanded.Remove(id);
            if (selectedId == id)
            {
                selectedAttempt = null;
            }
        }
    }

    bool Expanded(int id) =>
        expanded.Contains(id);

    string? Chosen(int id, int? attempt) =>
        selectedId == id && selectedAttempt == attempt ? "scry-sidecar-selected" : null;

    void Clear()
    {
        Store.Clear();
        selectedId = 0;
        selectedAttempt = null;
        selectedEvent = null;
        expanded.Clear();
        note = null;
    }

    // Newest first: a live query that has been asked again several times is read from what it is
    // doing now backwards. Its events stay in the order they arrived.
    static IEnumerable<ScrySidecarConnection> Newest(ScrySidecarSession session) =>
        session.Connections.Reverse();

    static IEnumerable<ScrySidecarEvent> Listed(ScrySidecarConnection connection) =>
        connection.Events;

    static string Progress(ScrySidecarSession session) =>
        session.State switch
        {
            ScrySubscriptionState.Connecting => "connecting",
            ScrySubscriptionState.Live => "live",
            ScrySubscriptionState.Reconnecting => session.Attempt > 1 ? $"retry ×{session.Attempt - 1}" : "retry",
            ScrySubscriptionState.Closed => "closed",
            _ => "failed"
        };

    static string StateClass(ScrySidecarSession session) =>
        session.State switch
        {
            ScrySubscriptionState.Live => "scry-sidecar-state scry-sidecar-state-live",
            ScrySubscriptionState.Faulted => "scry-sidecar-state scry-sidecar-status-error",
            ScrySubscriptionState.Reconnecting => "scry-sidecar-state scry-sidecar-state-retry",
            _ => "scry-sidecar-state"
        };

    // Answers, and how long since anything at all arrived. The second number is the one that says
    // whether a live query with nothing to report is idle or hung.
    static string Counts(ScrySidecarSession session) =>
        $"{session.Answers} · {Ago(session.LastEvent)}";

    static string Tally(ScrySidecarSession session) =>
        $"{Plural(session.Answers, "answer")}, {Plural(session.Pings, "ping")}, {session.Unchanged} unchanged · last event {Ago(session.LastEvent)} ago";

    static string Summary(ScrySidecarSession session)
    {
        var parts = new List<string>
        {
            session.Open ? $"Open {Ago(session.Started)}" : $"Ran {Ago(session.Started)}",
            Plural(session.Connections.Count + session.DroppedConnections, "connection"),
            Plural(session.Answers, "answer"),
            Plural(session.Pings, "ping"),
            $"{session.Unchanged} unchanged"
        };

        if (!session.OnTheWire)
        {
            parts.Add("reported by the client, which carries it somewhere this sidecar cannot watch");
        }

        return string.Join(" · ", parts);
    }

    static string Summary(ScrySidecarConnection connection)
    {
        var parts = new List<string> {connection.Status?.ToString() ?? "no status"};
        if (connection.ResumedFrom is { } resumed)
        {
            parts.Add($"resumed from {resumed}");
        }

        parts.Add(
            connection switch
            {
                {Ended: null} => $"open {Ago(connection.Started)}",
                {Duration: { } held} => $"{connection.Ended} after {Elapsed(held)}",
                var ended => ended.Ended!
            });

        return string.Join(" · ", parts);
    }

    static string Finish(ScrySidecarConnection connection) =>
        connection.Ended ?? "open";

    static string Size(int bytes) =>
        bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024d:0.#} KiB";

    static string Ago(DateTimeOffset? at)
    {
        if (at is not { } when)
        {
            return "—";
        }

        var since = DateTimeOffset.Now - when;
        return Elapsed(since < TimeSpan.Zero ? TimeSpan.Zero : since);
    }

    static string Elapsed(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:0}s" :
        span.TotalHours < 1 ? $"{(int) span.TotalMinutes}m {span.Seconds}s" :
        $"{(int) span.TotalHours}h {span.Minutes}m";

    static string Plural(int count, string what) =>
        count == 1 ? $"1 {what}" : $"{count} {what}s";

    // A live query's failure is the session's, and a command's its own; everything else carries its own.
    static string? Failure(ScrySidecarEntry entry) =>
        entry.Error ?? entry.Session?.Error ?? entry.Command?.Error;

    static string CommandState(ScrySidecarCommand command) =>
        command.State switch
        {
            ScryCommandActivityKind.Sent => "sent",
            ScryCommandActivityKind.Refused => "refused",
            ScryCommandActivityKind.Pending => "pending",
            ScryCommandActivityKind.Reattaching => $"retry ×{command.Attempt - 1}",
            ScryCommandActivityKind.Completed => "completed",
            ScryCommandActivityKind.Failed => "failed",
            _ => "unknown"
        };

    static string CommandClass(ScrySidecarCommand command) =>
        command.State switch
        {
            ScryCommandActivityKind.Completed => "scry-sidecar-state scry-sidecar-state-live",
            ScryCommandActivityKind.Pending or ScryCommandActivityKind.Reattaching => "scry-sidecar-state scry-sidecar-state-retry",
            ScryCommandActivityKind.Refused or ScryCommandActivityKind.Failed or ScryCommandActivityKind.Unknown => "scry-sidecar-state scry-sidecar-status-error",
            _ => "scry-sidecar-state"
        };

    static string Summary(ScrySidecarCommand command)
    {
        var parts = new List<string>
        {
            $"{CommandState(command)} {Ago(command.Updated)} ago",
            $"id {command.Id:D}",
            Plural(command.Attempt, "connection")
        };

        if (!command.OnTheWire)
        {
            parts.Add("reported by the client, which sends it somewhere this sidecar cannot watch");
        }

        return string.Join(" · ", parts);
    }

    async Task Copy(string text)
    {
        if (module is not null)
        {
            await module.InvokeVoidAsync("copy", text);
        }
    }

    /// <summary>
    /// The explorer deep link for a captured query: the wire request rendered back into the C#
    /// snippet dialect, carried the way the explorer's own Share does — base64url in the fragment,
    /// which never reaches a server. Null when the explorer is not routed, the entry carries no
    /// decoded request, or the request cannot be rendered (a sensitive constant, an unsupported
    /// terminal).
    /// </summary>
    string? ExplorerHref(ScrySidecarEntry entry)
    {
        if (Options.ExplorerRoute is not { } route ||
            entry.Request is null ||
            !ScryQueryRenderer.TryRender(entry.Request, out var code))
        {
            return null;
        }

        return $"{route}/#q={QueryUrl.Encode(Encoding.UTF8.GetBytes(code))}";
    }

    /// <summary>
    /// Re-sends the captured attachment request and hands the bytes to the browser as a download.
    /// Always re-asks the server rather than replaying a cached payload — the panel never holds
    /// attachment bytes, and the server's policies answer afresh.
    /// </summary>
    async Task Download(ScrySidecarEntry entry)
    {
        if (module is null ||
            entry.AttachmentRequestBody is not { } body)
        {
            return;
        }

        try
        {
            var client = Options.DownloadClient?.Invoke(Services) ?? (fallbackDownloadClient ??= new());
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new("application/json") {CharSet = "utf-8"};
            using var response = await client.PostAsync(entry.Url, content);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                note = "The attachment value is null.";
                return;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                note = "No attachment was returned.";
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                note = $"Download failed ({(int)response.StatusCode}).";
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();

            // Saved as whatever the server said it served — the member's declared type, or what its
            // policy overrode that with for this row. Nothing here re-decides from the bytes.
            var contentType = response.Content.Headers.ContentType?.ToString() ?? AttachmentMedia.Default;
            await module.InvokeVoidAsync(
                "downloadBytes",
                FileName(body, contentType),
                Convert.ToBase64String(bytes),
                contentType);
            note = null;
        }
        catch (Exception exception)
        {
            note = $"Download failed: {exception.Message}";
        }
        finally
        {
            StateHasChanged();
        }
    }

    static string FileName(byte[] attachmentRequest, string? contentType)
    {
        var extension = AttachmentMedia.Extension(contentType);
        try
        {
            var request = ScryJson.DeserializeAttachmentRequest(attachmentRequest);
            return $"{request.Root}.{request.Member}{extension}";
        }
        catch (ScryWireException)
        {
            return $"attachment{extension}";
        }
    }

    // Why an exchange shows no response body, for the two kinds whose body is never read here. A
    // live query's is read as it flows, so it has a session of its own to show instead.
    static string NotCaptured(ScrySidecarKind kind) =>
        kind switch
        {
            ScrySidecarKind.Stream => "streams are read row by row",
            ScrySidecarKind.Command => "its receipts are read by the client as they stream, and its state is what the client reports",
            _ => "attachment bytes are never cached; use Download"
        };

    static string Name(ScrySidecarEntry entry)
    {
        if (entry.Request is { } request)
        {
            return request.Root;
        }

        if (entry.Command is { } command)
        {
            return command.Name;
        }

        if (entry is
            {
                Kind: ScrySidecarKind.Attachment,
                AttachmentRequestBody: { } body
            })
        {
            try
            {
                var attachment = ScryJson.DeserializeAttachmentRequest(body);
                return $"{attachment.Root}.{attachment.Member}";
            }
            catch (ScryWireException)
            {
            }
        }

        if (new Uri(entry.Url, UriKind.RelativeOrAbsolute) is {IsAbsoluteUri: true} uri)
        {
            return uri.AbsolutePath;
        }

        return entry.Url;
    }

    public async ValueTask DisposeAsync()
    {
        open = false;
        Store.Changed -= OnChanged;
        Store.SessionChanged -= OnSessionChanged;
        reference?.Dispose();
        fallbackDownloadClient?.Dispose();
        if (module is not null)
        {
            try
            {
                await module.InvokeVoidAsync("dispose");
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is gone, and its listener with it.
            }
        }
    }
}
