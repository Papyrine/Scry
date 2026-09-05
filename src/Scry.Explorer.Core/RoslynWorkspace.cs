namespace Scry;

/// <summary>
/// An in-browser Roslyn workspace over the synthesized query models. The user's snippet — the query
/// expression, and any variables declared ahead of it — is wrapped in a method body so the C#
/// <see cref="CompletionService"/> can offer members against the allow-listed surface (e.g.
/// <c>Query.Employee.Where(e =&gt; e.</c> → Active, Name, Status, ...).
/// </summary>
public sealed class RoslynWorkspace :
    IDisposable
{
    // One MEF composition for the process. Composing one loads and scans every feature assembly,
    // and a re-fetched schema wants a new workspace, not a new composition.
    static readonly Lazy<MefHostServices> host = new(CreateHost);

    // The user's snippet is spliced between these so it is a legal method body. The usings make
    // LINQ operators (System.Linq), the synthesized models/enums (Scry.Generated), and the Scry
    // terminal operators (Scry: ToListAsync/FirstAsync/CountAsync/...) resolve —
    // so completion offers them and diagnostics do not falsely flag them.
    const string header =
        """
        using System;
        using System.Linq;
        using System.Collections.Generic;
        using Scry.Generated;
        using Scry;
        namespace Scry;
        static class Editor
        {
            static object Run(global::Scry.Generated.ScryQuery Query)
            {

        """;

    // Goes between the snippet's declarations and its query, which is why the snippet is not one run
    // of text in the document: an offset past the split sits this much further along than one before it.
    const string middle = "        return ";

    const string footer =
        """
        ;
            }
        }

        """;

    /// <summary>
    /// The snippet spliced into a compilable document, with the arithmetic to get back out of it.
    /// Roslyn answers in document coordinates while the editor asks and reads in the snippet's own,
    /// and the two differ by a different amount either side of the <see cref="middle"/>.
    /// </summary>
    readonly record struct Splice(SnippetLayout Layout, string Text)
    {
        public static Splice Of(string code)
        {
            var layout = SnippetLayout.Of(code);
            return new(layout, header + layout.Preamble + middle + layout.Expression + footer);
        }

        public int ToDocument(int offset)
        {
            if (offset < Layout.Split)
            {
                return header.Length + offset;
            }

            return header.Length + middle.Length + offset;
        }

        public int ToSnippet(int offset)
        {
            var body = offset - header.Length;
            return Math.Clamp(
                body <= Layout.Split ? body : body - middle.Length,
                0,
                Layout.Code.Length);
        }

        /// <summary>Whether an offset lands in the snippet rather than in the wrapper spliced around it.</summary>
        /// <remarks>
        /// Two regions, not one: between them sits the inserted <see cref="middle"/>, which is nobody's
        /// code. An empty editor is the case that makes the distinction worth drawing — the compiler
        /// anchors its complaint about the returned nothing on the inserted keyword, and reporting that
        /// would squiggle a snippet that has not been written yet.
        /// </remarks>
        public bool Covers(int offset)
        {
            var body = offset - header.Length;
            return body >= 0 && body < Layout.Split ||
                   body >= Layout.Split + middle.Length &&
                   body <= Layout.Code.Length + middle.Length;
        }
    }

    AdhocWorkspace workspace;
    DocumentId editorDocumentId;

    RoslynWorkspace(AdhocWorkspace workspace, DocumentId editorDocumentId)
    {
        this.workspace = workspace;
        this.editorDocumentId = editorDocumentId;
    }

    /// <param name="generatedSource">
    /// The synthesized query models, as <see cref="ModelSynthesizer"/> emitted them.
    /// </param>
    /// <param name="scryReferences">
    /// Scry.Client + Scry.Wire metadata references, so the terminal operators (extension methods on
    /// <see cref="IQueryable{T}"/>) resolve for completion and diagnostics.
    /// </param>
    public static RoslynWorkspace Create(string generatedSource, IReadOnlyList<MetadataReference> scryReferences)
    {
        var workspace = new AdhocWorkspace(host.Value);

        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name: "ScryEditor",
            assemblyName: "ScryEditor",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithConcurrentBuild(false),
            metadataReferences: [.. Net100.References.All, .. scryReferences]);

        var project = workspace.AddProject(projectInfo);
        workspace.AddDocument(project.Id, "Generated.cs", SourceText.From(generatedSource));
        var editor = workspace.AddDocument(project.Id, "Editor.cs", SourceText.From(Splice.Of("").Text));

        return new(workspace, editor.Id);
    }

    /// <summary>
    /// Builds the compilation once, so that every request after it is an edit to it rather than a
    /// build of its own.
    /// </summary>
    /// <remarks>
    /// Each request forks the current solution with the snippet's text and asks the fork for a
    /// semantic model. A fork derives its compilation from the one its base has cached — one syntax
    /// tree replaced, the references and their symbols shared — but only when the base has one. Until
    /// then, nothing does: every fork starts from nothing, binding the whole of the BCL and the models
    /// again for one completion, one diagnostic pass, or one hover. Called once, after the workspace
    /// is created, and the first request pays for nothing but itself.
    /// </remarks>
    public async Task WarmAsync()
    {
        var project = workspace.CurrentSolution.GetProject(editorDocumentId.ProjectId)!;
        await project.GetCompilationAsync();
    }

    /// <summary>
    /// Whether the base compilation is built and in hand, which is what a request's fork derives its
    /// own from. False until <see cref="WarmAsync"/> has run.
    /// </summary>
    public bool IsWarm =>
        workspace.CurrentSolution.GetProject(editorDocumentId.ProjectId)!.TryGetCompilation(out _);

    /// <summary>Returns the completions offered at <paramref name="caret"/> within <paramref name="code"/>.</summary>
    public async Task<List<ScryCompletion>> CompleteAsync(string code, int caret)
    {
        var splice = Splice.Of(code);
        var solution = workspace.CurrentSolution.WithDocumentText(
            editorDocumentId,
            SourceText.From(splice.Text));

        var document = solution.GetDocument(editorDocumentId)!;
        var service = CompletionService.GetService(document);
        if (service is null)
        {
            return [];
        }

        var completions = await service.GetCompletionsAsync(document, splice.ToDocument(caret));

        // The range to replace is the identifier currently being typed (empty right after a '.').
        var start = caret;
        while (start > 0 &&
               (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
        {
            start--;
        }

        return completions.ItemsList
            .Select(_ => new ScryCompletion(_.DisplayText, _.Tags.FirstOrDefault() ?? "", start, caret))
            .ToList();
    }

    /// <summary>Returns errors/warnings within the user's code (offsets in <paramref name="code"/> coordinates).</summary>
    public async Task<IReadOnlyList<ScryDiagnostic>> DiagnoseAsync(string code)
    {
        // A trailing ';' is harmless to Run (the executor strips it) but would otherwise splice into
        // "return <code>;" as an empty, unreachable statement and surface a spurious warning.
        code = code.TrimEnd().TrimEnd(';').TrimEnd();

        var splice = Splice.Of(code);
        var solution = workspace.CurrentSolution.WithDocumentText(
            editorDocumentId,
            SourceText.From(splice.Text));

        var document = solution.GetDocument(editorDocumentId)!;
        var model = await document.GetSemanticModelAsync();
        if (model is null)
        {
            return [];
        }

        var diagnostics = new List<ScryDiagnostic>();

        // A preamble holding more than declarations is still a legal method body, so the compiler has
        // nothing to say about it. Reported here instead, which puts it under the same squiggle and
        // through the same refusal every other error already travels by.
        if (splice.Layout.Problem is { } problem)
        {
            diagnostics.Add(problem);
        }

        foreach (var diagnostic in model.GetDiagnostics())
        {
            if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            {
                continue;
            }

            var span = diagnostic.Location.SourceSpan;
            // Only surface diagnostics anchored in the user's snippet, not the generated wrapper.
            if (!splice.Covers(span.Start))
            {
                continue;
            }

            diagnostics.Add(new(
                diagnostic.GetMessage(),
                splice.ToSnippet(span.Start),
                splice.ToSnippet(span.End),
                diagnostic.Severity == DiagnosticSeverity.Error));
        }

        return diagnostics;
    }

    /// <summary>
    /// Returns hover text for the symbol at <paramref name="caret"/>, or null. Uses the semantic model
    /// directly (not QuickInfoService) because QuickInfo touches Roslyn's persistent storage, which
    /// throws PlatformNotSupportedException on WebAssembly (Process.GetCurrentProcess is unavailable).
    /// </summary>
    public async Task<ScryHover?> GetHoverAsync(string code, int caret)
    {
        var splice = Splice.Of(code);
        var solution = workspace.CurrentSolution.WithDocumentText(
            editorDocumentId,
            SourceText.From(splice.Text));

        var document = solution.GetDocument(editorDocumentId)!;
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null)
        {
            return null;
        }

        var token = root.FindToken(splice.ToDocument(caret));
        if (token.Parent is not { } node)
        {
            return null;
        }

        var info = model.GetSymbolInfo(node);
        var symbol = info.Symbol
                     ?? info.CandidateSymbols.FirstOrDefault()
                     ?? model.GetDeclaredSymbol(node);
        if (symbol is null)
        {
            return null;
        }

        return new(
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            splice.ToSnippet(token.SpanStart),
            splice.ToSnippet(token.Span.End));
    }

    public void Dispose() =>
        workspace.Dispose();

    static MefHostServices CreateHost()
    {
        // The C# completion providers live in the *.Features assemblies, which are not always in the
        // default MEF composition — load them explicitly alongside the defaults.
        var assemblies = MefHostServices.DefaultAssemblies
            .Concat(
            [
                Assembly.Load("Microsoft.CodeAnalysis.Features"),
                Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")
            ])
            .Distinct();

        return MefHostServices.Create(assemblies);
    }
}