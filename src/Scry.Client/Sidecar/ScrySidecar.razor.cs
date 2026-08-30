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
    string? note;
    IJSObjectReference? module;
    DotNetObjectReference<ScrySidecar>? reference;
    HttpClient? fallbackDownloadClient;

    ScrySidecarEntry? Selected =>
        Store.Entries.FirstOrDefault(_ => _.Id == selectedId);

    protected override void OnInitialized() =>
        Store.Changed += OnChanged;

    void OnChanged() =>
        InvokeAsync(StateHasChanged);

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
        });

    void Select(int id)
    {
        selectedId = id;
        note = null;
    }

    void Clear()
    {
        Store.Clear();
        selectedId = 0;
        note = null;
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
            await module.InvokeVoidAsync(
                "downloadBytes",
                FileName(body),
                Convert.ToBase64String(bytes),
                "application/octet-stream");
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

    static string FileName(byte[] attachmentRequest)
    {
        try
        {
            var request = ScryJson.DeserializeAttachmentRequest(attachmentRequest);
            return $"{request.Root}.{request.Member}.bin";
        }
        catch (ScryWireException)
        {
            return "attachment.bin";
        }
    }

    static string Name(ScrySidecarEntry entry)
    {
        if (entry.Request is { } request)
        {
            return request.Root;
        }

        if (entry.Kind == ScrySidecarKind.Attachment &&
            entry.AttachmentRequestBody is { } body)
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

        return new Uri(entry.Url, UriKind.RelativeOrAbsolute) is {IsAbsoluteUri: true} uri
            ? uri.AbsolutePath
            : entry.Url;
    }

    public async ValueTask DisposeAsync()
    {
        Store.Changed -= OnChanged;
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
