using System.Net.Http;
using System.Reflection;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Scry.Client;
using Scry.Wire;

namespace Scry.Explorer.Ui.Roslyn;

/// <summary>
/// Compiles a user's query expression in the browser, runs it against a capturing client to build
/// the LINQ expression tree, and reuses <see cref="ScryQueryableExtensions.ToScryRequest{T}"/> to
/// produce the wire <see cref="QueryRequest"/> — the same translation the production client performs.
/// </summary>
sealed class SnippetExecutor
{
    readonly IReadOnlyList<MetadataReference> references;
    readonly string generatedSource;

    SnippetExecutor(IReadOnlyList<MetadataReference> references, string generatedSource)
    {
        this.references = references;
        this.generatedSource = generatedSource;
    }

    public static async Task<SnippetExecutor> CreateAsync(HttpClient http, ScryIntrospection introspection)
    {
        var references = new List<MetadataReference>(Net100.References.All);

        // The compiled snippet must bind against the real client/wire types so ToScryRequest is
        // available. Fetch their (Webcil-disabled, unfingerprinted) PE images from _framework.
        foreach (var assembly in (string[])["Scry.Client", "Scry.Wire"])
        {
            var bytes = await http.GetByteArrayAsync($"_framework/{assembly}.dll");
            references.Add(MetadataReference.CreateFromImage(bytes));
        }

        return new(references, ModelSynthesizer.Synthesize(introspection, executable: true));
    }

    public QueryRequest Translate(string userExpression)
    {
        var compilation = CSharpCompilation.Create(
            "ScrySnippet",
            [
                CSharpSyntaxTree.ParseText(generatedSource),
                CSharpSyntaxTree.ParseText(Wrap(userExpression))
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

    static string Wrap(string userExpression) =>
        """
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
                => (
        """ + userExpression + ").ToScryRequest();\n}\n";
}
