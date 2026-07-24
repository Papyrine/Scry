using System.Reflection;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Scry.Client;

namespace Scry.Explorer.Core;

/// <summary>
/// Compiles a user's query expression in the browser, runs it against a capturing client to build
/// the LINQ expression tree, and reuses <see cref="ScryQueryableExtensions.ToScryRequest{T}"/> to
/// produce the wire <see cref="QueryRequest"/> — the same translation the production client performs.
/// A trailing terminal operator (e.g. <c>.ToListAsync()</c>, <c>.FirstAsync()</c>,
/// <c>.CountAsync()</c>, or plain LINQ <c>.ToList()</c>) is recognised and folded into the wire
/// request as its <see cref="QueryOp"/> terminal.
/// </summary>
public sealed class SnippetExecutor
{
    readonly IReadOnlyList<MetadataReference> references;
    readonly string generatedSource;

    SnippetExecutor(IReadOnlyList<MetadataReference> references, string generatedSource)
    {
        this.references = references;
        this.generatedSource = generatedSource;
    }

    /// <summary>
    /// Fetches the (Webcil-disabled, unfingerprinted) Scry.Client + Scry.Wire PE images from
    /// _framework as metadata references. Shared by the executor (to compile snippets against the real
    /// client/wire types) and by the Roslyn workspace (so terminal operators resolve for completion).
    /// </summary>
    public static async Task<IReadOnlyList<MetadataReference>> FetchReferencesAsync(HttpClient http)
    {
        var references = new List<MetadataReference>();
        foreach (var assembly in (string[])["Scry.Client", "Scry.Wire"])
        {
            var bytes = await http.GetByteArrayAsync($"_framework/{assembly}.dll");
            references.Add(MetadataReference.CreateFromImage(bytes));
        }

        return references;
    }

    public static SnippetExecutor Create(
        ScryIntrospection introspection,
        IReadOnlyList<MetadataReference> scryReferences) =>
        new([.. Net100.References.All, .. scryReferences], ModelSynthesizer.Synthesize(introspection, executable: true));

    public QueryRequest Translate(string userExpression)
    {
        var (expression, terminal) = Rewrite(userExpression);

        var compilation = CSharpCompilation.Create(
            "ScrySnippet",
            [
                CSharpSyntaxTree.ParseText(generatedSource),
                CSharpSyntaxTree.ParseText(Wrap(expression, terminal))
            ],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithConcurrentBuild(false));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            var message = string.Join(
                "; ",
                result.Diagnostics
                    .Where(_ => _.Severity == DiagnosticSeverity.Error)
                    .Take(3)
                    .Select(_ => _.GetMessage()));
            throw new InvalidOperationException($"Could not compile the query: {message}");
        }

        var assembly = Assembly.Load(stream.ToArray());

        // The capturing client is never asked to transport anything — ToScryRequest only reads the
        // captured expression tree.
        var client = new ScryClient((_, _) => Task.FromResult<QueryResponse>(null!));
        var query = Activator.CreateInstance(assembly.GetType("Scry.Generated.ScryQuery")!, client)!;
        var runner = assembly.GetType("Scry.Editor.Runner")!;
        return (QueryRequest)runner.GetMethod("Run")!.Invoke(null, [query])!;
    }

    // Recognised zero-argument terminal operators → their wire QueryOp ("" = enumerate to a list).
    // Both the Scry async terminals (the real client API) and the plain-LINQ equivalents are accepted,
    // so habitual `.ToList()`/`.Count()`/`.First()` work too.
    static Dictionary<string, string> terminals = new(StringComparer.Ordinal)
    {
        ["ToListAsync"] = "",
        ["ToList"] = "",
        ["ToArray"] = "",
        ["FirstAsync"] = "new global::Scry.Wire.FirstOp(false, null)",
        ["First"] = "new global::Scry.Wire.FirstOp(false, null)",
        ["FirstOrDefaultAsync"] = "new global::Scry.Wire.FirstOp(true, null)",
        ["FirstOrDefault"] = "new global::Scry.Wire.FirstOp(true, null)",
        ["SingleAsync"] = "new global::Scry.Wire.SingleOp(false, null)",
        ["Single"] = "new global::Scry.Wire.SingleOp(false, null)",
        ["SingleOrDefaultAsync"] = "new global::Scry.Wire.SingleOp(true, null)",
        ["SingleOrDefault"] = "new global::Scry.Wire.SingleOp(true, null)",
        ["CountAsync"] = "new global::Scry.Wire.CountOp()",
        ["Count"] = "new global::Scry.Wire.CountOp()",
        ["AnyAsync"] = "new global::Scry.Wire.AnyOp(null)",
        ["Any"] = "new global::Scry.Wire.AnyOp(null)"
    };

    /// <summary>
    /// Splits a trailing terminal operator off the user's expression: returns the underlying queryable
    /// expression and its wire terminal op ("" means enumerate to a list). A trailing ';' and a leading
    /// 'await' are tolerated. The terminal is stripped rather than executed — synchronous enumeration
    /// (e.g. plain <c>.ToList()</c>) would deadlock on the single-threaded WASM runtime. Anything that
    /// is not a recognised zero-argument terminal is left intact (i.e. translated as a list).
    /// </summary>
    static (string Expression, string Terminal) Rewrite(string code)
    {
        var cleaned = code.TrimEnd().TrimEnd(';').TrimEnd();
        var expression = SyntaxFactory.ParseExpression(cleaned);
        if (expression is AwaitExpressionSyntax awaited)
        {
            expression = awaited.Expression;
        }

        if (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member, ArgumentList.Arguments.Count: 0} &&
            terminals.TryGetValue(member.Name.Identifier.ValueText, out var terminal))
        {
            return (member.Expression.ToString(), terminal);
        }

        return (expression.ToString(), "");
    }

    static string Wrap(string expression, string terminal) =>
        $$"""
          using System;
          using System.Linq;
          using System.Collections.Generic;
          using Scry.Client;
          using Scry.Wire;
          using Scry.Generated;
          namespace Scry.Editor;
          public static class Runner
          {
              public static global::Scry.Wire.QueryRequest Run(global::Scry.Generated.ScryQuery Query)
                  => ({{expression}}).ToScryRequest({{terminal}});
          }

          """;
}
