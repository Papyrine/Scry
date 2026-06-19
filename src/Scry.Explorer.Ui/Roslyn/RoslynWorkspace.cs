using System.Collections.Immutable;
using System.Reflection;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace Scry.Explorer.Ui.Roslyn;

/// <summary>A completion offered by Roslyn: its label, Roslyn tag (kind), and the span it replaces.</summary>
sealed record ScryCompletion(string Label, string Kind, int ReplaceStart, int ReplaceEnd);

/// <summary>A Roslyn diagnostic within the user's code: message, span (in editor coordinates), severity.</summary>
sealed record ScryDiagnostic(string Message, int Start, int End, bool IsError);

/// <summary>Hover (QuickInfo) text for the symbol at a position, plus the span it covers (editor coords).</summary>
sealed record ScryHover(string Text, int Start, int End);


/// <summary>
/// An in-browser Roslyn workspace over the synthesized query models. The user's query expression is
/// wrapped in a method body so the C# <see cref="CompletionService"/> can offer members against the
/// allow-listed surface (e.g. <c>Query.Employee.Where(e =&gt; e.</c> → Active, Name, Status, ...).
/// </summary>
sealed class RoslynWorkspace
{
    // The user's expression is spliced between these so it is a legal method body. The usings make
    // LINQ operators (System.Linq) and the synthesized models/enums (Scry.Generated) resolve.
    const string Header =
        "using System;\nusing System.Linq;\nusing System.Collections.Generic;\nusing Scry.Generated;\nnamespace Scry.Editor;\nstatic class Editor\n{\n    static object Run(global::Scry.Generated.ScryQuery Query)\n    {\n        return ";
    const string Footer = ";\n    }\n}\n";

    readonly AdhocWorkspace workspace;
    readonly DocumentId editorDocumentId;

    RoslynWorkspace(AdhocWorkspace workspace, DocumentId editorDocumentId)
    {
        this.workspace = workspace;
        this.editorDocumentId = editorDocumentId;
    }

    public static RoslynWorkspace Create(string generatedSource)
    {
        var workspace = new AdhocWorkspace(CreateHost());

        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name: "ScryEditor",
            assemblyName: "ScryEditor",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithConcurrentBuild(false),
            metadataReferences: Net100.References.All);

        var project = workspace.AddProject(projectInfo);
        workspace.AddDocument(project.Id, "Generated.cs", SourceText.From(generatedSource));
        var editor = workspace.AddDocument(project.Id, "Editor.cs", SourceText.From(Header + Footer));

        return new(workspace, editor.Id);
    }

    /// <summary>Returns the completions offered at <paramref name="caret"/> within <paramref name="code"/>.</summary>
    public async Task<IReadOnlyList<ScryCompletion>> CompleteAsync(string code, int caret)
    {
        var solution = workspace.CurrentSolution.WithDocumentText(
            editorDocumentId,
            SourceText.From(Header + code + Footer));

        var document = solution.GetDocument(editorDocumentId)!;
        var service = CompletionService.GetService(document);
        if (service is null)
        {
            return [];
        }

        var completions = await service.GetCompletionsAsync(document, Header.Length + caret);

        // The range to replace is the identifier currently being typed (empty right after a '.').
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
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
        var solution = workspace.CurrentSolution.WithDocumentText(
            editorDocumentId,
            SourceText.From(Header + code + Footer));

        var document = solution.GetDocument(editorDocumentId)!;
        var model = await document.GetSemanticModelAsync();
        if (model is null)
        {
            return [];
        }

        var userStart = Header.Length;
        var userEnd = userStart + code.Length;
        var diagnostics = new List<ScryDiagnostic>();
        foreach (var diagnostic in model.GetDiagnostics())
        {
            if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            {
                continue;
            }

            var span = diagnostic.Location.SourceSpan;
            // Only surface diagnostics anchored in the user's expression, not the generated wrapper.
            if (span.Start < userStart || span.Start > userEnd)
            {
                continue;
            }

            diagnostics.Add(new(
                diagnostic.GetMessage(),
                Math.Clamp(span.Start - userStart, 0, code.Length),
                Math.Clamp(span.End - userStart, 0, code.Length),
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
        var solution = workspace.CurrentSolution.WithDocumentText(
            editorDocumentId,
            SourceText.From(Header + code + Footer));

        var document = solution.GetDocument(editorDocumentId)!;
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null)
        {
            return null;
        }

        var token = root.FindToken(Header.Length + caret);
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
            Math.Clamp(token.SpanStart - Header.Length, 0, code.Length),
            Math.Clamp(token.Span.End - Header.Length, 0, code.Length));
    }

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
            .Distinct()
            .ToImmutableArray();

        return MefHostServices.Create(assemblies);
    }
}
