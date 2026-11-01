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
    const string modelAssembly = "ScryModel";

    IReadOnlyList<MetadataReference> references;
    string generatedSource;

    // The models, compiled once for the schema this executor was created for: the reference a
    // snippet compiles against, and the assembly it runs against. A run used to re-parse, re-bind,
    // and re-emit the whole model alongside its snippet, and load the result into the default
    // context — never unloadable — so a hundred runs held a hundred copies of the model.
    Lazy<MetadataReference> model;

    // Where the model and every snippet compiled against it are loaded. A context of its own so the
    // snippet's reference to the model resolves to the one copy loaded here, and everything else —
    // Scry.Client, Scry.Wire, the BCL — falls through to the default context they are already in.
    ModelLoadContext context = new();

    // Snippets are named apart: a context refuses a second assembly under a name it already holds.
    int runs;

    SnippetExecutor(IReadOnlyList<MetadataReference> references, string generatedSource)
    {
        this.references = references;
        this.generatedSource = generatedSource;
        model = new(EmitModel);
    }

    /// <summary>
    /// What this executor has loaded: the model once, and one small assembly per run. Those stay
    /// loaded — a load context cannot be unloaded on this runtime — which is what makes the model
    /// worth loading once.
    /// </summary>
    public IReadOnlyList<Assembly> LoadedAssemblies =>
        [.. context.Assemblies];

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

        var rewritten = Rewrite(layout.Expression);

        // Only the snippet is compiled here, against the model compiled once: the runner's method is
        // a few lines, and the models it reads are exactly what they were on the last run.
        var compilation = CSharpCompilation.Create(
            $"ScrySnippet{Interlocked.Increment(ref runs)}",
            [CSharpSyntaxTree.ParseText(Wrap(layout.Preamble, rewritten))],
            [.. references, model.Value],
            options);

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            throw new($"Could not compile the query: {Errors(result)}");
        }

        stream.Position = 0;
        var assembly = context.LoadFromStream(stream);

        // The capturing client is never asked to transport anything — ToScryRequest only reads the
        // captured expression tree.
        var client = new ScryClient((_, _) => Task.FromResult<QueryResponse>(null!));
        var query = Activator.CreateInstance(context.Model!.GetType("Scry.Generated.ScryQuery")!, client)!;
        var runner = assembly.GetType("Scry.Runner")!;
        try
        {
            var request = (QueryRequest) runner.GetMethod("Run")!.Invoke(null, [query])!;
            return Terminated(request, rewritten, runner);
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is { } inner)
        {
            // Reflection wraps whatever the snippet threw — the translator refusing a call that is
            // client-side code, a variable's initializer failing — in an exception whose own message
            // says only that something was thrown. The banner shows the message, so the cause is
            // rethrown as itself, its stack intact.
            ExceptionDispatchInfo.Throw(inner);
            throw;
        }
    }

    static CSharpCompilationOptions options =
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithConcurrentBuild(false);

    // Compiles the models and loads them into the context, once. The image is kept as the reference
    // every snippet compiles against, so a snippet binds to exactly the assembly it will run against.
    MetadataReference EmitModel()
    {
        var compilation = CSharpCompilation.Create(
            modelAssembly,
            [CSharpSyntaxTree.ParseText(generatedSource)],
            references,
            options);

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            throw new($"Could not compile the query models: {Errors(result)}");
        }

        var image = stream.ToArray();
        context.Model = context.LoadFromStream(new MemoryStream(image));
        return MetadataReference.CreateFromImage(image);
    }

    static string Errors(EmitResult result) =>
        string.Join(
            "; ",
            result.Diagnostics
                .Where(_ => _.Severity == DiagnosticSeverity.Error)
                .Take(3)
                .Select(_ => _.GetMessage()));

    sealed class ModelLoadContext() :
        AssemblyLoadContext(nameof(SnippetExecutor))
    {
        public Assembly? Model { get; set; }

        // The one name this context answers for. Everything else is left to the default context,
        // which is where the snippet's other references already live.
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == modelAssembly)
            {
                return Model;
            }

            return null;
        }
    }

    // Collection-shaping terminals that all enumerate to a list on the wire. Their arguments (key
    // selectors, element selectors, comparers) reshape the result client-side and do not affect the
    // request, so these are stripped regardless of argument count. Both the Scry async terminals (the
    // real client API) and the plain-LINQ equivalents are accepted, so habitual `.ToList()` works too.
    static HashSet<string> collectionTerminals =
    [
        with(StringComparer.Ordinal),
        "ToListAsync",
        "ToList",
        "ToArrayAsync",
        "ToArray",
        "ToHashSetAsync",
        "ToHashSet",
        "ToDictionaryAsync",
        "ToDictionary",
        "ToLookupAsync",
        "ToLookup" // ToAsyncEnumerable is intentionally absent: streaming is not supported yet (the client
        // terminal throws), so the explorer must not fold it into a valid list request either.
    ];

    // Scalar and element terminals, and the wire operator each is, from the lambda it was written
    // with where it takes one: a predicate, or an aggregate's selector. Null where it was written
    // without one. The lambda is read by the client's own translator, so the operator is the one the
    // client would send.
    // Bare is the operator written into the runner where the terminal has no lambda, null where it
    // must have one; Op makes it from the lambda's translation where it has.
    static Dictionary<string, (string? Bare, Func<Node, QueryOp> Op)> scalarTerminals = Terminals();

    static Dictionary<string, (string? Bare, Func<Node, QueryOp> Op)> Terminals()
    {
        var terminals = new Dictionary<string, (string?, Func<Node, QueryOp>)>(StringComparer.Ordinal);

        void Add(string name, string? bare, Func<Node, QueryOp> op)
        {
            terminals[name] = (bare, op);
            terminals[name + "Async"] = (bare, op);
        }

        Add("First", "new global::Scry.FirstOp(false, null)", _ => new FirstOp(false, _));
        Add("FirstOrDefault", "new global::Scry.FirstOp(true, null)", _ => new FirstOp(true, _));
        Add("Single", "new global::Scry.SingleOp(false, null)", _ => new SingleOp(false, _));
        Add("SingleOrDefault", "new global::Scry.SingleOp(true, null)", _ => new SingleOp(true, _));
        Add("Last", "new global::Scry.LastOp(false, null)", _ => new LastOp(false, _));
        Add("LastOrDefault", "new global::Scry.LastOp(true, null)", _ => new LastOp(true, _));
        Add("Count", "new global::Scry.CountOp()", _ => new CountOp(_));
        Add("LongCount", "new global::Scry.LongCountOp()", _ => new LongCountOp(_));
        Add("Any", "new global::Scry.AnyOp(null)", _ => new AnyOp(_));
        Add("All", null, _ => new AllOp(_));
        Add("Sum", null, _ => new AggregateOp(AggregateFn.Sum, _));
        Add("Average", null, _ => new AggregateOp(AggregateFn.Average, _));
        Add("Min", null, _ => new AggregateOp(AggregateFn.Min, _));
        Add("Max", null, _ => new AggregateOp(AggregateFn.Max, _));
        return terminals;
    }

    // What a trailing terminal leaves for the runner: the queryable before it, the terminal's name,
    // and the text of the lambda it was written with, where it was.
    readonly record struct Rewritten(string Expression, string? Terminal, string? Argument);

    /// <summary>
    /// Splits a trailing terminal operator off the user's expression: returns the underlying queryable
    /// expression and its wire terminal op ("" means enumerate to a list). A trailing ';' and a leading
    /// 'await' are tolerated. The terminal is stripped rather than executed — synchronous enumeration
    /// (e.g. plain <c>.ToList()</c>) would deadlock on the single-threaded WASM runtime. Anything that
    /// is not a recognised terminal is left intact (i.e. translated as a list).
    /// </summary>
    static Rewritten Rewrite(string code)
    {
        var cleaned = code.TrimEnd().TrimEnd(';').TrimEnd();
        var expression = SyntaxFactory.ParseExpression(cleaned);
        if (expression is AwaitExpressionSyntax awaited)
        {
            expression = awaited.Expression;
        }

        if (expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax member
            } invocation)
        {
            var name = member.Name.Identifier.ValueText;
            var arguments = invocation.ArgumentList.Arguments;

            // Collection terminals enumerate to a list; their arguments (selectors/comparers) are
            // client-side shaping and do not change the wire request, so strip whatever the arity.
            if (collectionTerminals.Contains(name))
            {
                return new(member.Expression.ToString(), null, null);
            }

            // A scalar terminal with no lambda, or with the one lambda it takes: a predicate narrows
            // what it reads and a selector says what it folds, so the lambda goes with it to the wire.
            if (scalarTerminals.TryGetValue(name, out var terminal))
            {
                if (arguments.Count == 0 &&
                    terminal.Bare is not null)
                {
                    return new(member.Expression.ToString(), name, null);
                }

                if (arguments.Count == 1 &&
                    arguments[0].Expression is LambdaExpressionSyntax lambda)
                {
                    return new(member.Expression.ToString(), name, lambda.ToString());
                }
            }
        }

        return new(expression.ToString(), null, null);
    }

    // A terminal with a lambda is written as a CountOp, and replaced once the runner has run: the
    // runner cannot reach the translator that reads its lambda, and a CountOp shapes the pipeline before
    // it as every terminal with a lambda does. The lambda is handed back as an expression tree, typed by
    // a helper that takes the element type from the queryable and the result from the lambda.
    static string Wrap(string preamble, Rewritten rewritten)
    {
        var terminal = "";
        var lambda = "";
        if (rewritten.Argument is { } argument)
        {
            terminal = "new global::Scry.CountOp()";
            lambda = $"Lambda = Typed(scrySource, {argument});";
        }
        else if (rewritten.Terminal is { } name)
        {
            terminal = scalarTerminals[name].Bare!;
        }

        return $$"""
                 using System;
                 using System.Linq;
                 using System.Collections.Generic;
                 using Scry;
                 using Scry.Generated;
                 namespace Scry;
                 public static class Runner
                 {
                     public static global::System.Linq.Expressions.LambdaExpression? Lambda;

                     public static global::Scry.QueryRequest Run(global::Scry.Generated.ScryQuery Query)
                     {
                         {{preamble}}
                         var scrySource = ({{rewritten.Expression}});
                         {{lambda}}
                         return scrySource.ToScryRequest({{terminal}});
                     }

                     static global::System.Linq.Expressions.LambdaExpression Typed<T, TResult>(global::System.Linq.IQueryable<T> source, global::System.Linq.Expressions.Expression<global::System.Func<T, TResult>> lambda) =>
                         lambda;
                 }

                 """;
    }

    // The request the runner made, with the terminal it stood in for put in its place.
    static QueryRequest Terminated(QueryRequest request, Rewritten rewritten, Type runner)
    {
        if (rewritten is not {Terminal: { } name, Argument: not null} ||
            runner.GetField("Lambda")!.GetValue(null) is not System.Linq.Expressions.LambdaExpression lambda)
        {
            return request;
        }

        var terminal = scalarTerminals[name].Op(QueryTranslator.TranslateLambda(lambda));
        return request with
        {
            Pipeline = [.. request.Pipeline.Take(request.Pipeline.Count - 1), terminal]
        };
    }
}
