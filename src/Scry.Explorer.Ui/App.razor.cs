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
    IReadOnlyList<string>? completions;
    string? wireJson;
    string? resultJson;
    List<string>? resultColumns;
    List<List<string>>? resultRows;
    // The same rows unflattened, as the server sent them — what the exports that keep a nested
    // projection nested are written from.
    List<JsonElement>? payloadRows;
    // Whether the result is a flat grid: every cell a scalar. Projecting into a navigation nests an
    // object inside the row instead, and a tree has no faithful CSV.
    bool resultFlat;
    string? scalarResult;
    string? error;
    bool editorReady;
    bool registered;

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
            var code = await editor.GetValue();
            executor ??= SnippetExecutor.Create(introspection, scryReferences);
            var request = executor.Translate(code);
            var json = ScryJson.Serialize(request);
            wireJson = Prettify(json);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(introspection.QueryEndpoint, content);
            var body = await response.Content.ReadAsStringAsync();
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

        // Ctrl/Cmd+Enter runs the query from inside the editor.
        await editor.AddAction(new ActionDescriptor
        {
            Id = "scry-run",
            Label = "Run Scry query",
            ContextMenuGroupId = "navigation",
            Keybindings = [(int)KeyMod.CtrlCmd | (int)BlazorMonaco.KeyCode.Enter],
            Run = _ => InvokeAsync(Run)
        });

        await TryRegister();
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

        // Surface completions immediately (and gives the tests a deterministic hook).
        await Complete();
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

    async Task Complete()
    {
        if (workspace is null)
        {
            return;
        }

        try
        {
            var text = await editor.GetValue();
            var items = await workspace.CompleteAsync(text, text.Length);
            completions = items.Select(_ => _.Label).ToList();
            StateHasChanged();
        }
        catch (Exception exception)
        {
            error = exception.Message;
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

    Task LoadQuery(string query) => editor.SetValue(query);

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
