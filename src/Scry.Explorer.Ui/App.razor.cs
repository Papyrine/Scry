using System.Net;
using System.Text;
using System.Text.Json;
using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.CodeAnalysis;
using Microsoft.JSInterop;

namespace Scry;

public partial class App
{
    const string Sample = "Query.Employee.Where(_ => _.";

    StandaloneCodeEditor editor = null!;
    RoslynWorkspace? workspace;
    ScryIntrospection? introspection;
    SnippetExecutor? executor;
    IReadOnlyList<MetadataReference>? scryReferences;
    string? wireJson;
    string? resultJson;
    // The rows as a grid: the columns, the cells as rendered, and the server's own rows kept
    // alongside them — what the exports that keep a nested projection nested are written from, and
    // what an attachment reads its row's key out of.
    ResultTable? result;
    // The attachments these rows can be fetched by, one extra column each. Empty for every result
    // whose source declares none, which is every result in a model without attachments.
    IReadOnlyList<AttachmentLink> attachmentLinks = [];
    // What a fetch that produced no file has to say, by row and member. Kept beside the table rather
    // than raised as a page error: an attachment holding nothing, or one this caller may not have,
    // is an answer about that row and not a failure of the query.
    readonly Dictionary<(int Row, string Member), string> attachmentNotes = [];
    string? scalarResult;
    string? error;
    // Which Run or Show SQL is the latest. Both clear and reassign the panes across several awaits
    // with the buttons still live, so a second command can start under a first that has not answered;
    // whichever answers last would otherwise write over the other's output — or under its request.
    // Each command takes the next number, and after every await the one that no longer holds it stops.
    int generation;
    bool editorReady;
    bool registered;
    // Set once the schema is loaded and the editor's Roslyn providers are registered. Rendered as
    // data-ready on the shell: the page can answer a completion from this point on.
    bool ready;

    string themeMode = "system";
    bool resolvedDark;
    // Which copy last landed, and which the clipboard last refused. One at a time each: the note
    // beside a button says what happened to the most recent click, and goes away after a moment.
    string? copied;
    string? copyFailed;
    string? sqlText;
    string? initialCode;

    const string ThemeKey = "scry-theme";
    const string TabsKey = "tabs";
    const string PluginKey = "plugin";
    const string PluginFlexKey = "pluginFlex";
    const string SessionFlexKey = "sessionFlex";
    const string WireFlexKey = "wireFlex";

    readonly HistoryStore history = new();
    readonly TabStore tabs = new(Sample);

    // The three splits, as the first pane's share of its container. Defaults chosen so the schema
    // reads without wrapping, the query and its output get half the width each, and the wire request
    // is a strip rather than a second editor.
    readonly PaneState pluginPane = new(0.24, 0.15, 0.6);
    readonly PaneState sessionPane = new(0.5, 0.2, 0.85);
    readonly PaneState wirePane = new(0.62, 0.2, 0.9);

    PluginKind? visiblePlugin = PluginKind.Schema;
    OutputTab activeOutput = OutputTab.Result;
    bool wireExpanded = true;
    bool refetching;
    bool settingsOpen;
    bool shortKeysOpen;
    string? status;

    ScryCallbacks callbacks = null!;
    DotNetObjectReference<ScryCallbacks>? callbackReference;
    Debouncer? persist;
    // Assigned in OnInitialized, which runs before anything reads it. Only a host without an
    // in-process runtime would leave it null, and Blazor WebAssembly always has one.
    StorageService storage = null!;

    // Resolve the saved theme and any shared query synchronously, before the editor is created: the
    // theme has to be right at construction (no light-flash) and the editor takes its initial value
    // from the same options object. IJSInProcessRuntime is available on Blazor WASM.
    protected override void OnInitialized()
    {
        if (JS is IJSInProcessRuntime js)
        {
            storage = new(new JsStorageBackend(js));
            // Outside the namespace: the inline script in index.html reads this literal key before
            // first paint, which is what keeps a dark session from flashing light on the way in.
            themeMode = storage.RawGet(ThemeKey) ?? "system";
            resolvedDark = ResolveDark(js);
            js.InvokeVoid("scry.setDataTheme", themeMode);
            initialCode = ShareLinkCodec.Decode(js.Invoke<string?>("scry.hash"));
            LoadHistory();
            RestoreShell();
            persist = new();
        }

        // A shared link wins over the restored tabs: following one is a request to see that query,
        // not to resume where this browser left off.
        if (initialCode is not null)
        {
            tabs.Active.Query = initialCode;
        }
    }

    // The pane drag bars and the document-level shortcuts are wired once, after the first render has
    // put them in the DOM. Every bar is always rendered — hidden by CSS when its pane is closed — so
    // one pass here covers all three.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        callbacks = new();
        callbacks.PaneResize += OnPaneResize;
        callbacks.GlobalShortcut += OnGlobalShortcut;
        callbacks.Flush += () => _ = persist?.Flush();
        callbackReference = DotNetObjectReference.Create(callbacks);
        await JS.InvokeVoidAsync("scry.init", callbackReference);
        await JS.InvokeVoidAsync("scry.registerGlobalShortcuts", Shortcut.Serialize(shortcuts));
        await JS.InvokeVoidAsync("scry.trackPointer", "plugin-resizer", "plugin-resizer", "x");
        await JS.InvokeVoidAsync("scry.trackPointer", "session-resizer", "session-resizer", "x");
        await JS.InvokeVoidAsync("scry.trackPointer", "wire-resizer", "wire-resizer", "y");
    }

    /// <summary>
    /// Writes the current query into the URL fragment and copies the resulting link. The query is placed in
    /// the fragment rather than the query string so it is never sent to the server — a shared link
    /// cannot land in an access log on the way.
    /// </summary>
    async Task Share()
    {
        var code = await editor.GetValue();
        var url = await JS.InvokeAsync<string>("scry.setHash", ShareLinkCodec.Encode(code));
        await Copy(url, "share");
    }

    /// <summary>
    /// Whether there is anything to export. The exports are of the rows the result table is showing,
    /// so an empty result offers none of them — and CSV additionally needs <see cref="resultFlat"/>.
    /// </summary>
    bool CanExport => result is { Rows.Count: > 0 };

    /// <summary>Downloads the result table as CSV — the rows as rendered, in their displayed order.</summary>
    async Task DownloadCsv()
    {
        if (result is null)
        {
            return;
        }

        // The BOM is what makes Excel read the UTF-8 as UTF-8 rather than as the local codepage, which
        // otherwise mangles any non-ASCII value in an exported column.
        await Download(
            "csv",
            "text/csv;charset=utf-8",
            ResultExporter.Csv(result.Columns, result.Rows),
            bom: true);
    }

    /// <summary>
    /// Downloads the rows as JSON, exactly as the server sent them. Unlike CSV this keeps a nested
    /// projection nested, so it is offered for every result the table can render.
    /// </summary>
    async Task DownloadJson()
    {
        if (result is null)
        {
            return;
        }

        // No BOM: a leading U+FEFF is not valid JSON, and strict parsers reject it.
        await Download("json", "application/json", ResultExporter.Json(result.PayloadRows), bom: false);
    }

    /// <summary>
    /// Downloads the rows as XML — a <c>row</c> element each, with a child element per member. Like
    /// JSON, and unlike CSV, a nested projection stays nested.
    /// </summary>
    async Task DownloadXml()
    {
        if (result is null)
        {
            return;
        }

        await Download("xml", "application/xml", ResultExporter.Xml(result.PayloadRows), bom: false);
    }

    Task Download(string extension, string type, string text, bool bom) =>
        JS.InvokeVoidAsync("scry.download", $"scry-result.{extension}", text, type, bom).AsTask();

    /// <summary>
    /// Claims one row's attachment and hands the bytes to the browser as a download. This is the
    /// exchange <c>ScryAttachment.OpenAsync</c> performs on a generated client, built here from the
    /// row's own key columns — the explorer never materializes a row into a model, so there is no
    /// handle to open.
    /// </summary>
    async Task FetchAttachment(int row, AttachmentLink link)
    {
        if (introspection is null ||
            result is null)
        {
            return;
        }

        attachmentNotes.Remove((row, link.Member));

        var keys = new List<AttachmentKey>(link.KeyColumns.Count);
        foreach (var column in link.KeyColumns)
        {
            // The linker only offers a member whose keys are columns of the result, so a row missing
            // one is a row the server shaped differently than its schema describes.
            if (!result.PayloadRows[row].TryGetProperty(column, out var value))
            {
                attachmentNotes[(row, link.Member)] = "no key";
                return;
            }

            keys.Add(new(Key(value), Tag(value)));
        }

        try
        {
            // The same path ScryClient.ForHttp derives, from the same endpoint: one mapping covers
            // the query surface and its attachments, so a host that moved one moved both.
            var request = AttachmentRequest.Create(link.Root, link.Member, keys, introspection.SchemaStamp);
            using var content = new StringContent(ScryJson.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync($"{introspection.QueryEndpoint.TrimEnd('/')}/attachment", content);

            switch (response.StatusCode)
            {
                // The row was readable and its column holds nothing. Distinct from the 404 below,
                // which says the caller may not have it rather than that there is nothing to have.
                case HttpStatusCode.NoContent:
                    attachmentNotes[(row, link.Member)] = "empty";
                    return;
                // Refused, absent, and policy-filtered arrive as one status by design; the explorer
                // is in no position to tell them apart either.
                case HttpStatusCode.NotFound:
                    attachmentNotes[(row, link.Member)] = "unavailable";
                    return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                error = ScryJson.TryDeserializeError(body) is {Error.Length: > 0} failure ? failure.Error : body;
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();

            // What arrived, rather than what introspection predicted: a policy may have overridden the
            // member's declared type for this row, and the name below is the one prediction the link
            // had to make before asking.
            var contentType = response.Content.Headers.ContentType?.ToString() ?? AttachmentMedia.Default;
            await JS.InvokeVoidAsync(
                "scry.downloadBytes",
                FileName(link, keys, contentType),
                Convert.ToBase64String(bytes),
                contentType);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    /// <summary>What a fetch had to say about a row, or null where it has said nothing.</summary>
    string? AttachmentNote(int row, AttachmentLink link) =>
        attachmentNotes.GetValueOrDefault((row, link.Member));

    // The invariant string form the wire carries. A JSON string is already one; everything else is
    // written as it was received rather than reformatted, so a decimal or a long round-trips whole.
    static string? Key(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText()
        };

    // The shape the value arrived in. A hint only: the server parses a key into the key member's own
    // CLR type and never trusts the tag, so a Guid or a date travelling as a string is read as one.
    static ClrTypeTag Tag(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => ClrTypeTag.Null,
            JsonValueKind.True or JsonValueKind.False => ClrTypeTag.Boolean,
            JsonValueKind.Number when value.TryGetInt32(out _) => ClrTypeTag.Int32,
            JsonValueKind.Number when value.TryGetInt64(out _) => ClrTypeTag.Int64,
            JsonValueKind.Number => ClrTypeTag.Decimal,
            _ => ClrTypeTag.String
        };

    // Named after what it is and which row it came from, extended for what the bytes turned out to be.
    // The key values are the server's own data, so every character a file name cannot carry is
    // replaced rather than trusted; the extension comes from a fixed map and never from the header.
    static string FileName(AttachmentLink link, IReadOnlyList<AttachmentKey> keys, string? contentType) =>
        Safe($"{link.Root}-{link.Member}-{string.Join("-", keys.Select(_ => _.Value))}") +
        AttachmentMedia.Extension(contentType);

    static string Safe(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Asks the server for the SQL the current query would run. The same translation Run uses produces
    /// the request, so the SQL shown belongs to the query as written — and the server validates and
    /// policy-filters it exactly as it would a real one before reading the SQL back.
    /// </summary>
    async Task ShowSql()
    {
        if (introspection is null || scryReferences is null)
        {
            return;
        }

        var run = ++generation;
        try
        {
            error = null;
            sqlText = null;
            var code = await editor.GetValue();
            await EnsureCompiles(code);
            if (run != generation)
            {
                return;
            }

            executor ??= SnippetExecutor.Create(introspection, scryReferences);
            var json = ScryJson.Serialize(executor.Translate(code));

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync("sql", content);
            var body = await response.Content.ReadAsStringAsync();
            if (run != generation)
            {
                return;
            }

            using var document = JsonDocument.Parse(body);
            if (response.IsSuccessStatusCode)
            {
                sqlText = document.RootElement.GetProperty("sql").GetString();
            }
            else
            {
                error = document.RootElement.TryGetProperty("error", out var message)
                    ? message.GetString()
                    : body;
            }

            ShowOutput(OutputTab.Sql);
        }
        catch (Exception exception) when (run == generation)
        {
            error = exception.Message;
        }
        catch (Exception)
        {
            // A later command owns the panes; what this one failed at is no longer what is on screen.
        }

        StateHasChanged();
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var json = await Http.GetStringAsync("introspect");
            introspection = ScryJson.DeserializeIntrospection(json);
            scryReferences = await SnippetExecutor.FetchReferencesAsync(Http);
            workspace = RoslynWorkspace.Create(ModelSynthesizer.Synthesize(introspection), scryReferences);
            await TryRegister();
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    /// <summary>
    /// Throws unless the query compiles, so a query the editor has squiggled is refused rather than run.
    /// </summary>
    /// <remarks>
    /// <see cref="SnippetExecutor"/> strips a trailing collection terminal — arguments and all — before it
    /// compiles anything. That is right for the wire, where a key selector shapes rows client-side and has
    /// no representation at all, but it leaves nothing written inside <c>.ToDictionaryAsync(…)</c> ever
    /// compiled: a missing key selector, a member the allow-list excludes, or plain nonsense would be
    /// squiggled by the editor and then run perfectly happily, returning rows for a query that a real
    /// client project would not build. Checked here against the text as written, so the two agree.
    ///
    /// Diagnosed on demand rather than read off the debounced pass behind the squiggles: that one is
    /// best-effort by design — it may be mid-flight, superseded, or to have failed silently — and none of
    /// those should be what decides whether a query runs. For the same reason the buttons stay enabled:
    /// a refusal that names the error is worth more than one that greys out, and a background pass that
    /// quietly died must not leave Run dead with it.
    ///
    /// Thrown rather than returned so it lands in the caller's existing catch, beside the compile failures
    /// the executor raises for the code it does compile, and reads identically in the banner.
    /// </remarks>
    async Task EnsureCompiles(string code)
    {
        if (workspace is null)
        {
            return;
        }

        var errors = (await workspace.DiagnoseAsync(code))
            .Where(_ => _.IsError)
            .Select(_ => _.Message)
            .Take(3)
            .ToList();

        if (errors.Count > 0)
        {
            throw new($"Could not compile the query: {string.Join("; ", errors)}");
        }
    }

    async Task Run()
    {
        if (introspection is null || scryReferences is null)
        {
            return;
        }

        var run = ++generation;
        try
        {
            error = null;
            result = null;
            scalarResult = null;
            sqlText = null;
            attachmentLinks = [];
            attachmentNotes.Clear();
            // Cleared with the rest, so a run that never got as far as translating does not leave the
            // panes showing the previous query's request and response under the error explaining that
            // this one produced neither. Both are set again below the moment there is something to show,
            // which keeps the wire request on screen for a query the *server* rejected — there the
            // request is what the rejection is about.
            wireJson = null;
            resultJson = null;
            var code = await editor.GetValue();
            await EnsureCompiles(code);
            if (run != generation)
            {
                return;
            }

            executor ??= SnippetExecutor.Create(introspection, scryReferences);
            var request = executor.Translate(code);
            var json = ScryJson.Serialize(request);
            wireJson = Prettify(json);

            var started = Stopwatch.GetTimestamp();
            var (response, method) = await SendQuery(json, ScryClient.RequiresBody(request));
            using var owned = response;
            var elapsed = Stopwatch.GetElapsedTime(started);

            // A later run owns the panes: this response answers a query that is no longer the one on
            // screen, so it is dropped here rather than written over the newer run's rows — or, with
            // the request strip already showing the newer query, under them.
            if (run != generation)
            {
                return;
            }

            // Not read as a string: a result carrying [BinaryTransfer] values arrives as multipart,
            // and the reader folds its parts back into the envelope as base64 — so a diverted member
            // renders, exports, and tabulates as the byte[] it is either way.
            var body = await BinaryResponseReader.ReadAsync(response);
            if (run != generation)
            {
                return;
            }

            resultJson = Prettify(body);

            if (response.IsSuccessStatusCode)
            {
                history.Add(code);
                SaveHistory();
                var parsed = ScryJson.DeserializeResponse(body);
                switch (parsed.Kind)
                {
                    case ResultKind.List:
                        BuildTable(parsed.Payload);
                        break;
                    case ResultKind.Page:
                        BuildTable(parsed.Payload.GetProperty("items"));
                        break;
                    case ResultKind.Single:
                        BuildSingle(parsed.Payload);
                        break;
                    case ResultKind.Scalar:
                        scalarResult = parsed.Payload.GetRawText();
                        break;
                }

                // Rows only: a folded terminal has one value rather than a row to fetch by.
                if (result is not null)
                {
                    attachmentLinks = AttachmentLinker.Link(introspection, request);
                }
            }

            status = Status((int) response.StatusCode, method, elapsed);
            // A run that returned no rows still returned something; the response is what there is to
            // read, so it is what the column opens on.
            ShowOutput(result is not null || scalarResult is not null ? OutputTab.Result : OutputTab.Response);
        }
        catch (Exception exception) when (run == generation)
        {
            error = exception.Message;
        }
        catch (Exception)
        {
            // A later command owns the panes; what this one failed at is no longer what is on screen.
        }

        StateHasChanged();
    }

    // The outcome, the transport it took, how many rows came back, and how long it took. The method
    // is worth a word of its own: a short query goes as a URL and a long one as a body, and which one
    // a request took has never been visible anywhere else.
    string Status(int statusCode, string method, TimeSpan elapsed)
    {
        var parts = new List<string>(4)
        {
            statusCode.ToString(CultureInfo.InvariantCulture),
            method
        };

        if (result is not null)
        {
            parts.Add(result.Rows.Count == 1 ? "1 row" : $"{result.Rows.Count} rows");
        }

        parts.Add($"{elapsed.TotalMilliseconds:F0} ms");
        return string.Join(" · ", parts);
    }

    StandaloneEditorConstructionOptions EditorOptions(StandaloneCodeEditor _) => new()
    {
        Language = "csharp",
        Value = tabs.Active.Query,
        AutomaticLayout = true,
        Theme = resolvedDark ? "vs-dark" : "vs",
        Minimap = new EditorMinimapOptions { Enabled = false },
        ScrollBeyondLastLine = false,
        Padding = new EditorPaddingOptions
        {
            Top = 14,
            Bottom = 14
        }
    };

    async Task OnEditorInit()
    {
        editorReady = true;

        // The dropdown offers what Roslyn resolved against the allow-listed schema, or nothing. Monaco's
        // own fallback completes from the words already in the editor, which would have a query
        // completing to its own text — "Query", "Where", "Employee" — wherever Roslyn has nothing to say,
        // and an explorer whose claim is that completion *is* the allow-list should not mix one in. The
        // option is set from JS because the typed one does not reach Monaco; see scry.js.
        await JS.InvokeVoidAsync("scry.disableWordSuggestions");

        // The commands that belong to the editor, so they follow its focus and appear in its context
        // menu. The ones that do not — the panes, the dialogs — are document-level instead; see
        // App.Shell.cs.
        await editor.AddAction(new ActionDescriptor
        {
            Id = "scry-run",
            Label = "Run Scry query",
            ContextMenuGroupId = "navigation",
            Keybindings = [(int)KeyMod.CtrlCmd | (int)BlazorMonaco.KeyCode.Enter],
            Run = _ => InvokeAsync(Run)
        });

        await editor.AddAction(new ActionDescriptor
        {
            Id = "scry-prettify",
            Label = "Format Scry query",
            ContextMenuGroupId = "navigation",
            Keybindings = [(int) KeyMod.CtrlCmd | (int) KeyMod.Shift | (int) BlazorMonaco.KeyCode.KeyP],
            Run = _ => InvokeAsync(Prettify)
        });

        await editor.AddAction(new ActionDescriptor
        {
            Id = "scry-copy",
            Label = "Copy Scry query",
            ContextMenuGroupId = "navigation",
            Keybindings = [(int)KeyMod.CtrlCmd | (int)KeyMod.Shift | (int)BlazorMonaco.KeyCode.KeyC],
            Run = _ => InvokeAsync(CopyQuery)
        });


        await MoveCaretToEnd();
        await TryRegister();
    }

    /// <summary>
    /// Puts the caret at the end of the query. Text that arrives without anyone typing it — the sample the
    /// page opens on, a shared link, an entry picked out of the history — otherwise leaves the caret at the
    /// very start, which is neither where the writing continues nor where IntelliSense has anything to say.
    /// </summary>
    async Task MoveCaretToEnd()
    {
        var text = await editor.GetValue();
        var (line, column) = ToLineColumn(text, text.Length);
        await editor.SetPosition(
            new()
            {
                LineNumber = line,
                Column = column
            },
            "scry");
    }

    // Text only — the glyph beside it is the inline svg in App.razor.
    string ThemeLabel => themeMode switch { "light" => "Light", "dark" => "Dark", _ => "System" };

    bool ResolveDark(IJSInProcessRuntime js) =>
        themeMode == "dark" || (themeMode != "light" && js.Invoke<bool>("scry.systemDark"));

    // Cycle System → Light → Dark, persist, and retint both the page (data-theme) and Monaco (global).
    Task CycleTheme()
    {
        themeMode = themeMode switch { "system" => "light", "light" => "dark", _ => "system" };
        return ApplyTheme();
    }

    // Stores the chosen theme and retints for it.
    Task ApplyTheme()
    {
        if (JS is IJSInProcessRuntime)
        {
            storage.RawSet(ThemeKey, themeMode);
        }

        return Retint();
    }

    // Retints the page and the editor together: the page follows data-theme, Monaco follows its own
    // registered theme, and the two have to be set from the same decision or they disagree. Apart
    // from storing the choice, so that forgetting the stored data can retint without writing.
    async Task Retint()
    {
        if (JS is IJSInProcessRuntime js)
        {
            js.InvokeVoid("scry.setDataTheme", themeMode);
            resolvedDark = ResolveDark(js);
        }

        await BlazorMonaco.Editor.Global.SetTheme(JS, resolvedDark ? "vs-dark" : "vs");
    }

    /// <summary>
    /// Rewrites the query in the explorer's house style. Text that does not parse is reported rather
    /// than rewritten — a formatter that guesses at a half-typed query produces a differently
    /// half-typed one, and the caret would land somewhere neither of them explains.
    /// </summary>
    async Task Prettify()
    {
        var code = await editor.GetValue();
        if (!QueryPrinter.TryFormat(code, out var formatted, out var problem))
        {
            error = problem;
            StateHasChanged();
            return;
        }

        error = null;
        if (formatted != code)
        {
            await editor.SetValue(formatted);
            await MoveCaretToEnd();
        }

        StateHasChanged();
    }

    Task CopyQuery() =>
        editor.GetValue().ContinueWith(_ => Copy(_.Result, "query"), TaskScheduler.Current).Unwrap();

    async Task Copy(string text, string key)
    {
        // Whether it landed is the browser's answer, not an assumption: the clipboard refuses a
        // document without focus, and a permission the user declined.
        var landed = await JS.InvokeAsync<bool>("scry.copy", text);
        copied = landed ? key : null;
        copyFailed = landed ? null : key;
        StateHasChanged();
        await Task.Delay(1500);
        if (copied == key)
        {
            copied = null;
        }

        if (copyFailed == key)
        {
            copyFailed = null;
        }

        StateHasChanged();
    }

    // The label on a pane's copy button: what the last click did, or the offer.
    string CopyLabel(string key) =>
        copied == key
            ? "✓ Copied"
            : copyFailed == key
                ? "✗ Not copied"
                : "Copy";

    // Register the completion provider once both the workspace (schema) and the editor are ready.
    async Task TryRegister()
    {
        if (registered || !editorReady || workspace is null)
        {
            return;
        }

        registered = true;

        var provider = new CompletionItemProvider(["."], ProvideCompletions);
        await BlazorMonaco.Languages.Global.RegisterCompletionItemProvider(JS, "csharp", provider);
        await BlazorMonaco.Languages.Global.RegisterHoverProviderAsync(JS, "csharp", ProvideHover);

        // Registered here rather than with the other editor actions because it depends on both halves
        // this method waits for: the editor to attach to, and the contract to say whether the server
        // offers the preview at all. The editor does not carry a keybinding for a button that is not
        // there.
        if (introspection?.SqlPreview == true)
        {
            await editor.AddAction(new ActionDescriptor
            {
                Id = "scry-sql",
                Label = "Show Scry SQL",
                ContextMenuGroupId = "navigation",
                Keybindings = [(int) KeyMod.CtrlCmd | (int) KeyMod.Shift | (int) BlazorMonaco.KeyCode.KeyQ],
                Run = _ => InvokeAsync(ShowSql)
            });
        }

        ready = true;
        StateHasChanged();
    }

    async Task<CompletionList> ProvideCompletions(string modelUri, Position position, CompletionContext context)
    {
        // Monaco invokes this on every keystroke/trigger; never let an exception escape into the
        // JS interop boundary (it would surface as an unhandled Blazor error).
        try
        {
            if (workspace is null)
            {
                return new() { Suggestions = [] };
            }

            var model = await BlazorMonaco.Editor.Global.GetModel(JS, modelUri);
            var text = await model.GetValue(EndOfLinePreference.LF, false);
            var caret = ToOffset(text, position.LineNumber, position.Column);

            var items = await workspace.CompleteAsync(text, caret);
            return new()
            {
                Suggestions = items.Select(item => new CompletionItem
                {
                    LabelAsString = item.Label,
                    Kind = MapKind(item.Kind),
                    InsertText = item.Label,
                    RangeAsObject = ToRange(text, item.ReplaceStart, item.ReplaceEnd)
                }).ToList()
            };
        }
        catch
        {
            return new() { Suggestions = [] };
        }
    }

    async Task<Hover> ProvideHover(string modelUri, Position position, HoverContext context)
    {
        try
        {
            if (workspace is null)
            {
                return null!;
            }

            var model = await BlazorMonaco.Editor.Global.GetModel(JS, modelUri);
            var text = await model.GetValue(EndOfLinePreference.LF, false);
            var hover = await workspace.GetHoverAsync(text, ToOffset(text, position.LineNumber, position.Column));
            if (hover is null)
            {
                return null!;
            }

            return new Hover
            {
                Contents = [new MarkdownString { Value = hover.Text }],
                Range = ToRange(text, hover.Start, hover.End)
            };
        }
        catch
        {
            return null!;
        }
    }

    // A Roslyn pass costs far more than a keystroke, so this window is much shorter than the persist
    // one: long enough to coalesce a burst of typing, short enough that squiggles still feel live.
    readonly Debouncer diagnose = new(300);

    // Re-run diagnostics on edit and surface them as editor squiggles. Debounced so a burst of
    // keystrokes coalesces into a single Roslyn pass, and a superseded run is dropped.
    async Task OnContentChanged(ModelContentChangedEvent _)
    {
        if (editorReady)
        {
            tabs.Active.Query = await editor.GetValue();
            SchedulePersist();
        }

        diagnose.Run(async cancel =>
        {
            // Read once the window has closed rather than before it opened, so a workspace that
            // finished loading during a burst of typing still gets to diagnose it.
            if (workspace is not { } loaded)
            {
                return;
            }

            var text = await editor.GetValue();
            var diagnostics = await loaded.DiagnoseAsync(text);
            // The pass outlasts the window it started in, so the last run to begin is not the last to
            // finish. Only the run that is still current may write markers.
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            var markers = diagnostics.Select(diagnostic =>
            {
                var (startLine, startColumn) = ToLineColumn(text, diagnostic.Start);
                var (endLine, endColumn) = ToLineColumn(text, diagnostic.End);
                if (endLine == startLine && endColumn <= startColumn)
                {
                    endColumn = startColumn + 1;
                }

                return new MarkerData
                {
                    Message = diagnostic.Message,
                    Severity = diagnostic.IsError ? MarkerSeverity.Error : MarkerSeverity.Warning,
                    StartLineNumber = startLine,
                    StartColumn = startColumn,
                    EndLineNumber = endLine,
                    EndColumn = endColumn
                };
            }).ToList();

            var model = await editor.GetModel();
            await BlazorMonaco.Editor.Global.SetModelMarkers(JS, model, "scry", markers);
        });
    }

    // A value under the legacy key is a plain array of query strings from before entries carried
    // labels and favorites. It is adopted once and then rewritten in the current shape, so an upgrade
    // does not discard anyone's history.
    void LoadHistory()
    {
        var json = storage.Get(HistoryStore.Key);
        if (json is not null)
        {
            history.Load(json);
            return;
        }

        var legacy = storage.RawGet(HistoryStore.LegacyKey);
        if (legacy is null)
        {
            return;
        }

        history.LoadLegacy(legacy);
        SaveHistory();
        storage.RawRemove(HistoryStore.LegacyKey);
    }

    void SaveHistory() =>
        storage.Set(HistoryStore.Key, history.Serialize());

    void LabelHistory((string Query, string Label) edit)
    {
        history.SetLabel(edit.Query, edit.Label);
        SaveHistory();
    }

    void FavoriteHistory((string Query, bool Favorite) edit)
    {
        history.SetFavorite(edit.Query, edit.Favorite);
        SaveHistory();
    }

    void RemoveHistory(string query)
    {
        history.Remove(query);
        SaveHistory();
    }

    void ClearHistory()
    {
        history.Clear();
        SaveHistory();
    }

    async Task LoadQuery(string query)
    {
        await editor.SetValue(query);
        await MoveCaretToEnd();
    }

    /// <summary>
    /// Sends a query the way <c>ScryClient</c> would: as a body where the query compares a
    /// <c>[Sensitive]</c> member against a constant or is too long for a URL, and as a URL otherwise.
    /// The explorer does not send through the client — it translates the snippet and sends the result
    /// itself — so the choice has to be made here too, or the explorer would demonstrate a request
    /// shape production never uses. Both halves of it are the client's own: <c>RequiresBody</c> reads
    /// the same models, and the budget comes from introspection.
    /// </summary>
    /// <remarks>
    /// The budget comes from introspection rather than from a response header the way a client's does,
    /// because this app is built and embedded when Scry is: it can carry no per-deployment value of its
    /// own, and it has the whole contract in hand before it sends anything.
    /// </remarks>
    async Task<(HttpResponseMessage Response, string Method)> SendQuery(string json, bool requiresBody)
    {
        var utf8 = Encoding.UTF8.GetBytes(json);
        var encoded = QueryUrl.Encode(utf8);
        var endpoint = introspection!.QueryEndpoint;
        if (requiresBody ||
            !QueryUrl.WithinLimit(encoded, introspection.QueryUrlLimit))
        {
            return (await Http.PostAsync(endpoint, new StringContent(json, Encoding.UTF8, "application/json")), "POST");
        }

        var separator = endpoint.Contains('?') ? '&' : '?';
        return (await Http.GetAsync($"{endpoint}{separator}{QueryUrl.Parameter}={encoded}"), "GET");
    }

    static readonly JsonSerializerOptions indented = new() { WriteIndented = true };

    static string Prettify(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, indented);
        }
        catch
        {
            return json;
        }
    }

    void BuildTable(JsonElement payload) =>
        result = ResultTable.FromList(payload);

    // A Single result is one projected object (or null) — render it as a one-row table, reusing the
    // list-result markup; null / a bare scalar falls back to the scalar line.
    void BuildSingle(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Null)
        {
            scalarResult = "(no result)";
            return;
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            scalarResult = payload.GetRawText();
            return;
        }

        result = ResultTable.FromRow(payload);
    }

    static int ToOffset(string text, int line, int column)
    {
        var offset = 0;
        var currentLine = 1;
        while (currentLine < line && offset < text.Length)
        {
            if (text[offset] == '\n')
            {
                currentLine++;
            }

            offset++;
        }

        return offset + (column - 1);
    }

    static BlazorMonaco.Range ToRange(string text, int start, int end)
    {
        var (startLine, startColumn) = ToLineColumn(text, start);
        var (endLine, endColumn) = ToLineColumn(text, end);
        return new()
        {
            StartLineNumber = startLine,
            StartColumn = startColumn,
            EndLineNumber = endLine,
            EndColumn = endColumn
        };
    }

    static (int Line, int Column) ToLineColumn(string text, int offset)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }

    static CompletionItemKind MapKind(string tag) =>
        tag switch
        {
            "Property" => CompletionItemKind.Property,
            "Field" => CompletionItemKind.Field,
            "Method" => CompletionItemKind.Method,
            "Class" => CompletionItemKind.Class,
            "Structure" => CompletionItemKind.Struct,
            "Interface" => CompletionItemKind.Interface,
            "Enum" => CompletionItemKind.Enum,
            "EnumMember" => CompletionItemKind.EnumMember,
            "Keyword" => CompletionItemKind.Keyword,
            "Namespace" => CompletionItemKind.Module,
            "Local" or "Parameter" or "RangeVariable" => CompletionItemKind.Variable,
            _ => CompletionItemKind.Text
        };
}
