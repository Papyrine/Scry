namespace Scry;

/// <summary>
/// Compiles a user's snippet in the browser, runs it against a capturing client to build the LINQ
/// expression tree, and reuses <see cref="ScryQueryableExtensions.ToScryRequest{T}"/> to produce the
/// wire <see cref="QueryRequest"/> — the same translation the production client performs. A snippet
/// is a query expression, optionally preceded by variables it reads; those are captured state like
/// any other, so the translator folds what the query takes from them into constants.
/// A trailing terminal operator (e.g. <c>.ToListAsync()</c>, <c>.FirstAsync()</c>,
/// <c>.CountAsync()</c>, or plain LINQ <c>.ToList()</c>) is recognised and folded into the wire
/// request as its <see cref="QueryOp"/> terminal.
/// </summary>
public sealed class SnippetExecutor
{
    IReadOnlyList<MetadataReference> references;
    string generatedSource;

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
        new(
            [.. Net100.References.All, .. scryReferences],
            ModelSynthesizer.Synthesize(introspection, executable: true));

    public QueryRequest Translate(string snippet)
    {
        var layout = SnippetLayout.Of(snippet);

        // The editor squiggles this too, but the executor is reachable without one — and a preamble
        // it refuses would otherwise compile and run, which is the whole of what the rule is against.
        if (layout.Problem is { } problem)
        {
            throw new(problem.Message);
        }

        var (expression, terminal) = Rewrite(layout.Expression);

        var compilation = CSharpCompilation.Create(
            "ScrySnippet",
            [
                CSharpSyntaxTree.ParseText(generatedSource),
                CSharpSyntaxTree.ParseText(Wrap(layout.Preamble, expression, terminal))
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
            throw new($"Could not compile the query: {message}");
        }

        var assembly = Assembly.Load(stream.ToArray());

        // The capturing client is never asked to transport anything — ToScryRequest only reads the
        // captured expression tree.
        var client = new ScryClient((_, _) => Task.FromResult<QueryResponse>(null!));
        var query = Activator.CreateInstance(assembly.GetType("Scry.Generated.ScryQuery")!, client)!;
        var runner = assembly.GetType("Scry.Runner")!;
        return (QueryRequest)runner.GetMethod("Run")!.Invoke(null, [query])!;
    }

    // Collection-shaping terminals that all enumerate to a list on the wire. Their arguments (key
    // selectors, element selectors, comparers) reshape the result client-side and do not affect the
    // request, so these are stripped regardless of argument count. Both the Scry async terminals (the
    // real client API) and the plain-LINQ equivalents are accepted, so habitual `.ToList()` works too.
    static HashSet<string> collectionTerminals = new(StringComparer.Ordinal)
    {
        "ToListAsync",
        "ToList",
        "ToArrayAsync",
        "ToArray",
        "ToHashSetAsync",
        "ToHashSet",
        "ToDictionaryAsync",
        "ToDictionary",
        "ToLookupAsync",
        "ToLookup"
        // ToAsyncEnumerable is intentionally absent: streaming is not supported yet (the client
        // terminal throws), so the explorer must not fold it into a valid list request either.
    };

    // Scalar/element terminals → their wire QueryOp. Only recognised with zero arguments: a predicate
    // overload (e.g. `.First(_ => _.Active)`) affects the wire and must not be silently dropped, so it
    // is left intact and translated as part of the pipeline instead.
    static Dictionary<string, string> scalarTerminals = new(StringComparer.Ordinal)
    {
        ["FirstAsync"] = "new global::Scry.FirstOp(false, null)",
        ["First"] = "new global::Scry.FirstOp(false, null)",
        ["FirstOrDefaultAsync"] = "new global::Scry.FirstOp(true, null)",
        ["FirstOrDefault"] = "new global::Scry.FirstOp(true, null)",
        ["SingleAsync"] = "new global::Scry.SingleOp(false, null)",
        ["Single"] = "new global::Scry.SingleOp(false, null)",
        ["SingleOrDefaultAsync"] = "new global::Scry.SingleOp(true, null)",
        ["SingleOrDefault"] = "new global::Scry.SingleOp(true, null)",
        ["CountAsync"] = "new global::Scry.CountOp()",
        ["Count"] = "new global::Scry.CountOp()",
        ["AnyAsync"] = "new global::Scry.AnyOp(null)",
        ["Any"] = "new global::Scry.AnyOp(null)"
    };

    /// <summary>
    /// Splits a trailing terminal operator off the user's expression: returns the underlying queryable
    /// expression and its wire terminal op ("" means enumerate to a list). A trailing ';' and a leading
    /// 'await' are tolerated. The terminal is stripped rather than executed — synchronous enumeration
    /// (e.g. plain <c>.ToList()</c>) would deadlock on the single-threaded WASM runtime. Anything that
    /// is not a recognised terminal is left intact (i.e. translated as a list).
    /// </summary>
    static (string Expression, string Terminal) Rewrite(string code)
    {
        var cleaned = code.TrimEnd().TrimEnd(';').TrimEnd();
        var expression = SyntaxFactory.ParseExpression(cleaned);
        if (expression is AwaitExpressionSyntax awaited)
        {
            expression = awaited.Expression;
        }

        if (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation)
        {
            var name = member.Name.Identifier.ValueText;

            // Collection terminals enumerate to a list; their arguments (selectors/comparers) are
            // client-side shaping and do not change the wire request, so strip whatever the arity.
            if (collectionTerminals.Contains(name))
            {
                return (member.Expression.ToString(), "");
            }

            // Scalar terminals only when written without a predicate — otherwise the filter would be lost.
            if (invocation.ArgumentList.Arguments.Count == 0 &&
                scalarTerminals.TryGetValue(name, out var terminal))
            {
                return (member.Expression.ToString(), terminal);
            }
        }

        return (expression.ToString(), "");
    }

    static string Wrap(string preamble, string expression, string terminal) =>
        $$"""
          using System;
          using System.Linq;
          using System.Collections.Generic;
          using Scry;
          using Scry.Generated;
          namespace Scry;
          public static class Runner
          {
              public static global::Scry.QueryRequest Run(global::Scry.Generated.ScryQuery Query)
              {
                  {{preamble}}
                  return ({{expression}}).ToScryRequest({{terminal}});
              }
          }

          """;
}
