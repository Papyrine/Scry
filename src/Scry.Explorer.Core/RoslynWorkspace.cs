using System.Collections.Immutable;
using System.Reflection;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace Scry.Explorer.Core;

/// <summary>A completion offered by Roslyn: its label, Roslyn tag (kind), and the span it replaces.</summary>
public sealed record ScryCompletion(string Label, string Kind, int ReplaceStart, int ReplaceEnd);

/// <summary>A Roslyn diagnostic within the user's code: message, span (in editor coordinates), severity.</summary>
public sealed record ScryDiagnostic(string Message, int Start, int End, bool IsError);

/// <summary>Hover (QuickInfo) text for the symbol at a position, plus the span it covers (editor coords).</summary>
public sealed record ScryHover(string Text, int Start, int End);


/// <summary>
/// An in-browser Roslyn workspace over the synthesized query models. The user's query expression is
/// wrapped in a method body so the C# <see cref="CompletionService"/> can offer members against the
/// allow-listed surface (e.g. <c>Query.Employee.Where(e =&gt; e.</c> → Active, Name, Status, ...).
/// </summary>
public sealed class RoslynWorkspace
{
    // The user's expression is spliced between these so it is a legal method body. The usings make
    // LINQ operators (System.Linq), the synthesized models/enums (Scry.Generated), and the Scry
    // terminal operators (Scry.Client: ToListAsync/FirstAsync/CountAsync/...) resolve —
    // so completion offers them and diagnostics do not falsely flag them.
    const string header =
        """
        using System;
        using System.Linq;
        using System.Collections.Generic;
        using Scry.Generated;
        using Scry.Client;
        namespace Scry.Editor;
        static class Editor
        {
            static object Run(global::Scry.Generated.ScryQuery Query)
            {
                return 
        """;

    const string Footer =
        """
        ;
            }
        }

        """;

    readonly AdhocWorkspace workspace;
    readonly DocumentId editorDocumentId;

    RoslynWorkspace(AdhocWorkspace workspace, DocumentId editorDocumentId)
    {
        this.workspace = workspace;
        this.editorDocumentId = editorDocumentId;
    }

    /// <param name="scryReferences">
    /// Scry.Client + Scry.Wire metadata references, so the terminal operators (extension methods on
    /// <see cref="IQueryable{T}"/>) resolve for completion and diagnostics.
    /// </param>
    public static RoslynWorkspace Create(string generatedSource, IReadOnlyList<MetadataReference> scryReferences)
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
            metadataReferences: [.. Net100.References.All, .. scryReferences]);

        var project = workspace.AddProject(projectInfo);
        workspace.AddDocument(project.Id, "Generated.cs", SourceText.From(generatedSource));
        var editor = workspace.AddDocument(project.Id, "Editor.cs", SourceText.From(header + Footer));

        return new(workspace, editor.Id);
    }

    /// <summary>Returns the completions offered at <paramref name="caret"/> within <paramref name="code"/>.</summary>
    public async Task<IReadOnlyList<ScryCompletion>> CompleteAsync(string code, int caret)
    {
        var solution = workspace.CurrentSolution.WithDocumentText(
            editorDocumentId,
            SourceText.From(header + code + Footer));

        var document = solution.GetDocument(editorDocumentId)!;
        var service = CompletionService.GetService(document);
        if (service is null)
        {
            return [];
        }

        var completions = await service.GetCompletionsAsync(document, header.Length + caret);

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
        // A trailing ';' is harmless to Run (the executor strips it) but would otherwise splice into
        // "return <code>;" as an empty, unreachable statement and surface a spurious warning.
        code = code.TrimEnd().TrimEnd(';').TrimEnd();

        var solution = workspace.CurrentSolution.WithDocumentText(
            editorDocumentId,
            SourceText.From(header + code + Footer));

        var document = solution.GetDocument(editorDocumentId)!;
        var model = await document.GetSemanticModelAsync();
        if (model is null)
        {
            return [];
        }

        var userStart = header.Length;
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
            SourceText.From(header + code + Footer));

        var document = solution.GetDocument(editorDocumentId)!;
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null)
        {
            return null;
        }

        var token = root.FindToken(header.Length + caret);
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
            Math.Clamp(token.SpanStart - header.Length, 0, code.Length),
            Math.Clamp(token.Span.End - header.Length, 0, code.Length));
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