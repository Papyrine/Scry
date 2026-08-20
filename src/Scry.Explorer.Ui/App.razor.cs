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
    List<string>? resultColumns;
    List<List<string>>? resultRows;
    // The same rows unflattened, as the server sent them — what the exports that keep a nested
    // projection nested are written from, and what an attachment reads its row's key out of.
    List<JsonElement>? payloadRows;
    // The attachments these rows can be fetched by, one extra column each. Empty for every result
    // whose source declares none, which is every result in a model without attachments.
    IReadOnlyList<AttachmentLink> attachmentLinks = [];
    // What a fetch that produced no file has to say, by row and member. Kept beside the table rather
    // than raised as a page error: an attachment holding nothing, or one this caller may not have,
    // is an answer about that row and not a failure of the query.
    readonly Dictionary<(int Row, string Member), string> attachmentNotes = [];
    // Whether the result is a flat grid: every cell a scalar. Projecting into a navigation nests an
    // object inside the row instead, and a tree has no faithful CSV.
    bool resultFlat;
    string? scalarResult;
    string? error;
    bool editorReady;
    bool registered;
    // Set once the schema is loaded and the editor's Roslyn providers are registered. Rendered as
    // data-ready on the shell: the page can answer a completion from this point on.
    bool ready;

    string themeMode = "system";
    bool resolvedDark;
    string? copied;
    string? sqlText;
    string? initialCode;

    const string HistoryKey = "scry-history";
    const string SharePrefix = "#q=";
    List<string> history = [];

    // Resolve the saved theme and any shared query synchronously, before the editor is created: the
    // theme has to be right at construction (no light-flash) and the editor takes its initial value
    // from the same options object. IJSInProcessRuntime is available on Blazor WASM.
    protected override void OnInitialized()
    {
        if (JS is IJSInProcessRuntime js)
        {
            themeMode = js.Invoke<string?>("localStorage.getItem", "scry-theme") ?? "system";
            resolvedDark = ResolveDark(js);
            js.InvokeVoid("scry.setDataTheme", themeMode);
            initialCode = SharedQuery(js.Invoke<string?>("scry.hash"));
        }
    }

    /// <summary>
    /// The query carried by a <c>#q=</c> fragment, or null. A shared link is untrusted input like any
    /// other URL, so anything that does not decode is ignored rather than surfaced — the explorer opens
    /// on its sample query instead of on an error.
    /// </summary>
    static string? SharedQuery(string? hash)
    {
        if (hash is null ||
            !hash.StartsWith(SharePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var encoded = Uri.UnescapeDataString(hash[SharePrefix.Length..]);
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            // base64url drops the padding; Convert requires it.
            padded = padded.PadRight(padded.Length + (3 - ((padded.Length + 3) % 4)), '=');
            var code = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return code.Length == 0 ? null : code;
        }
        catch
        {
            return null;
        }
    }

    // base64url of the UTF-8 text: URL-safe, unpadded, and stable across the round trip above.
    static string Encode(string code) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(code))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>
    /// Writes the current query into the URL fragment and copies the resulting link. The query is placed in
    /// the fragment rather than the query string so it is never sent to the server — a shared link
    /// cannot land in an access log on the way.
    /// </summary>
    async Task Share()
    {
        var code = await editor.GetValue();
        var url = await JS.InvokeAsync<string>("scry.setHash", SharePrefix + Encode(code));
        await Copy(url, "share");
    }

    /// <summary>
    /// Whether there is anything to export. The exports are of the rows the result table is showing,
    /// so an empty result offers none of them — and CSV additionally needs <see cref="resultFlat"/>.
    /// </summary>
    bool CanExport => resultRows is { Count: > 0 };

    /// <summary>Downloads the result table as CSV — the rows as rendered, in their displayed order.</summary>
    async Task DownloadCsv()
    {
        if (resultColumns is null ||
            resultRows is null)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", resultColumns.Select(Csv)));
        foreach (var row in resultRows)
        {
            builder.AppendLine(string.Join(",", row.Select(Csv)));
        }

        // The BOM is what makes Excel read the UTF-8 as UTF-8 rather than as the local codepage, which
        // otherwise mangles any non-ASCII value in an exported column.
        await Download("csv", "text/csv;charset=utf-8", builder.ToString(), bom: true);
    }

    /// <summary>
    /// Downloads the rows as JSON, exactly as the server sent them. Unlike CSV this keeps a nested
    /// projection nested, so it is offered for every result the table can render.
    /// </summary>
    async Task DownloadJson()
    {
        if (payloadRows is null)
        {
            return;
        }

        // No BOM: a leading U+FEFF is not valid JSON, and strict parsers reject it.
        await Download("json", "application/json", JsonSerializer.Serialize(payloadRows, indented), bom: false);
    }

    /// <summary>
    /// Downloads the rows as XML — a <c>row</c> element each, with a child element per member. Like
    /// JSON, and unlike CSV, a nested projection stays nested.
    /// </summary>
    async Task DownloadXml()
    {
        if (payloadRows is null)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        builder.AppendLine("<results>");
        foreach (var row in payloadRows)
        {
            WriteXml(builder, "row", row, depth: 1);
        }

        builder.Append("</results>");
        await Download("xml", "application/xml", builder.ToString(), bom: false);
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
            payloadRows is null)
        {
            return;
        }

        attachmentNotes.Remove((row, link.Member));

        var keys = new List<AttachmentKey>(link.KeyColumns.Count);
        foreach (var column in link.KeyColumns)
        {
            // The linker only offers a member whose keys are columns of the result, so a row missing
            // one is a row the server shaped differently than its schema describes.
            if (!payloadRows[row].TryGetProperty(column, out var value))
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
            await JS.InvokeVoidAsync(
                "scry.downloadBytes",
                FileName(link, keys),
                Convert.ToBase64String(bytes),
                ScryBinary.PartContentType);
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

    // Named after what it is and which row it came from. The key values are the server's own data, so
    // every character a file name cannot carry is replaced rather than trusted.
    static string FileName(AttachmentLink link, IReadOnlyList<AttachmentKey> keys) =>
        Safe($"{link.Root}-{link.Member}-{string.Join("-", keys.Select(_ => _.Value))}") + ".bin";

    static string Safe(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        return builder.ToString();
    }

    // RFC 4180: a field containing a comma, a quote, or a newline is quoted, and quotes inside it are
    // doubled. Everything else is written as-is.
    static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    static void WriteXml(StringBuilder builder, string name, JsonElement value, int depth)
    {
        var indent = new string(' ', depth * 2);

        // An absent value stays an empty element rather than being dropped, so every row keeps the
        // same shape.
        if (value.ValueKind == JsonValueKind.Null)
        {
            builder.Append(indent).Append('<').Append(name).AppendLine(" />");
            return;
        }

        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            builder.Append(indent).Append('<').Append(name).Append('>')
                .Append(Xml(value.ToString()))
                .Append("</").Append(name).AppendLine(">");
            return;
        }

        builder.Append(indent).Append('<').Append(name).AppendLine(">");
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                WriteXml(builder, XmlName(property.Name), property.Value, depth + 1);
            }
        }
        else
        {
            foreach (var item in value.EnumerateArray())
            {
                WriteXml(builder, "item", item, depth + 1);
            }
        }

        builder.Append(indent).Append("</").Append(name).AppendLine(">");
    }

    // Text content: the three characters that cannot appear literally are escaped, and the control
    // characters XML 1.0 forbids outright are dropped — a value that came out of a column should not
    // be able to produce a document no parser will open.
    static string Xml(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                default:
                    if (character is '\t' or '\n' or '\r' ||
                        character >= ' ')
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    // Member names come from the caller's own C# identifiers, so they already are valid XML names —
    // but the rows are the server's response, and an export should never be able to emit a name that
    // does not parse. Anything outside a name character becomes '_'.
    static string XmlName(string name)
    {
        if (name.Length == 0)
        {
            return "_";
        }

        var builder = new StringBuilder(name.Length);
        builder.Append(char.IsLetter(name[0]) || name[0] == '_' ? name[0] : '_');
        foreach (var character in name.AsSpan(1))
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_');
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

        try
        {
            error = null;
            sqlText = null;
            var code = await editor.GetValue();
            await EnsureCompiles(code);
            executor ??= SnippetExecutor.Create(introspection, scryReferences);
            var json = ScryJson.Serialize(executor.Translate(code));

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync("sql", content);
            var body = await response.Content.ReadAsStringAsync();

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
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        StateHasChanged();
    }

    protected override async Task OnInitializedAsync()
    {
        history = await LoadHistory();
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

        try
        {
            error = null;
            resultColumns = null;
            resultRows = null;
            payloadRows = null;
            resultFlat = false;
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
            executor ??= SnippetExecutor.Create(introspection, scryReferences);
            var request = executor.Translate(code);
            var json = ScryJson.Serialize(request);
            wireJson = Prettify(json);

            using var response = await SendQuery(json, ScryClient.RequiresBody(request));

            // Not read as a string: a result carrying [BinaryTransfer] values arrives as multipart,
            // and the reader folds its parts back into the envelope as base64 — so a diverted member
            // renders, exports, and tabulates as the byte[] it is either way.
            var body = await BinaryResponseReader.ReadAsync(response);
            resultJson = Prettify(body);

            if (response.IsSuccessStatusCode)
            {
                AddHistory(code);
                await SaveHistory();
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
                if (resultRows is not null)
                {
                    attachmentLinks = AttachmentLinker.Link(introspection, request);
                }
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        StateHasChanged();
    }

    StandaloneEditorConstructionOptions EditorOptions(StandaloneCodeEditor _) => new()
    {
        Language = "csharp",
        Value = initialCode ?? Sample,
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

        // Ctrl/Cmd+Enter runs the query from inside the editor.
        await editor.AddAction(new ActionDescriptor
        {
            Id = "scry-run",
            Label = "Run Scry query",
            ContextMenuGroupId = "navigation",
            Keybindings = [(int)KeyMod.CtrlCmd | (int)BlazorMonaco.KeyCode.Enter],
            Run = _ => InvokeAsync(Run)
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
    async Task CycleTheme()
    {
        themeMode = themeMode switch { "system" => "light", "light" => "dark", _ => "system" };
        if (JS is IJSInProcessRuntime js)
        {
            js.InvokeVoid("localStorage.setItem", "scry-theme", themeMode);
            js.InvokeVoid("scry.setDataTheme", themeMode);
            resolvedDark = ResolveDark(js);
        }

        await BlazorMonaco.Editor.Global.SetTheme(JS, resolvedDark ? "vs-dark" : "vs");
    }

    async Task Copy(string text, string key)
    {
        await JS.InvokeVoidAsync("scry.copy", text);
        copied = key;
        StateHasChanged();
        await Task.Delay(1500);
        if (copied == key)
        {
            copied = null;
            StateHasChanged();
        }
    }

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

    CancellationTokenSource? diagnosticsCts;

    // Re-run diagnostics on edit and surface them as editor squiggles. Debounced so a burst of
    // keystrokes coalesces into a single Roslyn pass, and a superseded run is dropped.
    async Task OnContentChanged(ModelContentChangedEvent _)
    {
        if (workspace is null)
        {
            return;
        }

        diagnosticsCts?.Cancel();
        var cts = diagnosticsCts = new CancellationTokenSource();

        try
        {
            await Task.Delay(300, cts.Token);

            var text = await editor.GetValue();
            var diagnostics = await workspace.DiagnoseAsync(text);
            if (cts.Token.IsCancellationRequested)
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
        }
        catch
        {
            // Diagnostics are best-effort; never disrupt typing.
        }
    }

    async Task<List<string>> LoadHistory()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", HistoryKey);
            return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    Task SaveHistory() =>
        JS.InvokeVoidAsync("localStorage.setItem", HistoryKey, JsonSerializer.Serialize(history)).AsTask();

    void AddHistory(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return;
        }

        history.Remove(query);
        history.Insert(0, query);
        if (history.Count > 10)
        {
            history.RemoveRange(10, history.Count - 10);
        }
    }

    // Removal is by text rather than by index: entries are deduped on exactly that, so it identifies
    // one, and it survives the list having moved under a render the click raced.
    Task RemoveHistory(string query)
    {
        history.Remove(query);
        return SaveHistory();
    }

    Task ClearHistory()
    {
        history.Clear();
        return SaveHistory();
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
    Task<HttpResponseMessage> SendQuery(string json, bool requiresBody)
    {
        var utf8 = Encoding.UTF8.GetBytes(json);
        var encoded = QueryUrl.Encode(utf8);
        var endpoint = introspection!.QueryEndpoint;
        if (requiresBody ||
            !QueryUrl.WithinLimit(encoded, introspection.QueryUrlLimit))
        {
            return Http.PostAsync(endpoint, new StringContent(json, Encoding.UTF8, "application/json"));
        }

        var separator = endpoint.Contains('?') ? '&' : '?';
        return Http.GetAsync($"{endpoint}{separator}{QueryUrl.Parameter}={encoded}");
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

    void BuildTable(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var columns = new List<string>();
        var payloads = new List<JsonElement>();
        foreach (var row in payload.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (columns.Count == 0)
            {
                columns.AddRange(row.EnumerateObject().Select(_ => _.Name));
            }

            payloads.Add(row);
        }

        Publish(columns, payloads);
    }

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

        Publish(payload.EnumerateObject().Select(_ => _.Name).ToList(), [payload]);
    }

    // Renders the rows for display and classifies them. A cell that is itself an object or an array
    // came from a projection into a navigation: the result is a tree rather than a grid, which is
    // what decides whether CSV is on offer.
    void Publish(List<string> columns, List<JsonElement> rows)
    {
        resultColumns = columns;
        payloadRows = rows;
        resultRows = rows
            .Select(_ => _.EnumerateObject().Select(property => property.Value.ToString()).ToList())
            .ToList();
        resultFlat = rows.All(row => row.EnumerateObject()
            .All(_ => _.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)));
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
