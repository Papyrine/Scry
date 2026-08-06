namespace Scry;

/// <summary>
/// Reports, at the call site, LINQ that Scry cannot carry — the same closed set <c>QueryTranslator</c>
/// enforces when the query is captured, and the server's <c>QueryValidator</c> enforces when the
/// request arrives.
/// </summary>
/// <remarks>
/// Deliberately a partial checker. It reads the chain as written, so a query composed through a helper
/// it cannot follow, or a rule that depends on a value only known at runtime, is left to the two
/// places that already enforce it. Nothing here is a guarantee: the client is assumed hostile and the
/// server re-validates every request regardless — see docs/security.md. This moves a mistake from a
/// stack trace to a squiggle, and nowhere else.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScryLinqAnalyzer :
    DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => LinqDiagnostics.All;

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        // The generated entry points are IQueryable-returning members that no chain is written in, so
        // there is nothing to report there — and reporting into a file the consumer cannot edit is
        // what the generator's own diagnostics exist to avoid.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(Start);
    }

    static void Start(CompilationStartAnalysisContext context)
    {
        // Nothing in this compilation was generated against a Scry model, so no chain can be one.
        if (KnownTypes.For(context.Compilation) is not { } known)
        {
            return;
        }

        context.RegisterOperationAction(_ => Analyze(_, known), OperationKind.Invocation);
        context.RegisterOperationAction(_ => Loop(_, known), OperationKind.Loop);
    }

    // foreach is the one way to run a query that is not a call, so no chain walk sees it: it reaches
    // the provider through GetEnumerator rather than through a terminal, and lands on the same throw.
    //
    // An await foreach is the opposite and is left alone. A captured query has no GetAsyncEnumerator,
    // so the only way one compiles is over the IAsyncEnumerable a ToAsyncEnumerable terminal returned —
    // which is the streaming idiom itself, and the advice this rule carries (buffer it with
    // ToListAsync) is the one thing streaming exists to avoid.
    static void Loop(OperationAnalysisContext context, KnownTypes known)
    {
        if (context.Operation is not IForEachLoopOperation {IsAsynchronous: false} loop ||
            !QueryChain.IsQuery(loop.Collection, known))
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                LinqDiagnostics.SynchronousExecution,
                loop.Collection.Syntax.GetLocation(),
                "foreach",
                "await ToListAsync and iterate what it returns"));
    }

    static void Analyze(OperationAnalysisContext context, KnownTypes known)
    {
        var invocation = (IInvocationOperation) context.Operation;

        // A chain is walked once, from its outermost call.
        if (QueryChain.IsInner(invocation))
        {
            return;
        }

        // A chain written inside a query lambda reads a row rather than composing the query — a
        // membership test against another source, an aggregate over a collection navigation — and what
        // it may carry is a different rule set. Left to the translator rather than guessed at here.
        if (QueryChain.InsideQueryLambda(invocation, known))
        {
            return;
        }

        if (QueryChain.Of(invocation, known, out var inherited) is not { } chain)
        {
            return;
        }

        Walk(context, chain, inherited, known);
    }

    static void Walk(OperationAnalysisContext context, List<IInvocationOperation> chain, int inherited, KnownTypes known)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = false;

        // Whether an operator has already rewritten what a row is. Once one has, an attachment can no
        // longer be fetched: the key beside it stops identifying one row of one source.
        var rewritten = false;

        for (var index = 0; index < chain.Count; index++)
        {
            var link = chain[index];
            var method = QueryChain.Unreduced(link);
            var name = method.Name;

            // Written in an earlier statement, and reported there. Still part of the query: a Select
            // three statements up is the query's one Select.
            if (index < inherited)
            {
                Track(seen, ref ordered, name);
                continue;
            }

            // A Scry terminal ends the chain. Its own arguments are client-side — a key selector for a
            // dictionary, a page size — with the exception of the predicates and aggregate selectors
            // that do reach the wire, which the expression rules below pick up by parameter type.
            if (known.IsScry(method.ContainingType))
            {
                ExpressionRules.Check(context, link);
                continue;
            }

            if (!ReturnsQuery(method))
            {
                Report(
                    context,
                    LinqDiagnostics.SynchronousExecution,
                    link,
                    name,
                    known.AsyncTerminal(name) is { } terminal
                        ? $"use {terminal} instead"
                        : "and Scry has no async terminal for it");
                continue;
            }

            if (name == "Cast")
            {
                var target = method.TypeArguments.Length > 0 ? method.TypeArguments[0].Name : "T";
                Report(context, LinqDiagnostics.Cast, link, target);
                continue;
            }

            // Read before the operator's own rules, so a query carrying an attachment is reported for
            // the operator that would strand it rather than only for what else is wrong with it.
            if (AttachmentRules.Rewrites(name))
            {
                if (!rewritten &&
                    AttachmentRules.Carries(ElementOf(link), known))
                {
                    AttachmentRules.Operator(context, link, name);
                }

                rewritten = true;
            }

            AttachmentRules.Check(context, link, name, known, rewritten);

            if (!SupportedLinq.Operators.TryGetValue(name, out var arities))
            {
                Report(context, LinqDiagnostics.UnsupportedOperator, link, name);
                continue;
            }

            if (Array.IndexOf(arities, method.Parameters.Length) < 0)
            {
                Overload(context, link, known, method);
                continue;
            }

            // The indexed overloads carry the same argument count as the supported ones and differ
            // only in the lambda: Select((row, index) => …) has no wire operator, since the wire never
            // numbers rows.
            if (QueryChain.Lambda(link, 1) is {Symbol.Parameters.Length: 2} &&
                name is "Where" or "Select" or "SelectMany")
            {
                Report(context, LinqDiagnostics.UnsupportedOperator, link, $"{name} with an index");
                continue;
            }

            // The 3-argument GroupBy is three spellings with one arity. A result selector unfolds
            // into the GroupBy + Select it abbreviates; an element selector and a comparer have no
            // wire form. The third argument's own shape tells them apart.
            if (name == "GroupBy" &&
                method.Parameters.Length == 3)
            {
                if (known.IsComparer(method.Parameters[2].Type))
                {
                    Report(context, LinqDiagnostics.Comparer, link, name);
                    continue;
                }

                if (QueryChain.Lambda(link, 2) is not {Symbol.Parameters.Length: 2} selector)
                {
                    Report(context, LinqDiagnostics.UnsupportedOperator, link, "GroupBy with an element selector");
                    continue;
                }

                // The result selector is the query's one Select, so a later explicit Select is a
                // second — and it must construct an object like any other projection.
                if (!seen.Add("Select"))
                {
                    Report(context, LinqDiagnostics.SingleUse, link, "Select");
                    continue;
                }

                if (ExpressionRules.Body(selector) is { } constructed &&
                    !ExpressionRules.Constructs(constructed))
                {
                    Report(context, LinqDiagnostics.Projection, link);
                }
            }

            if (SupportedLinq.SingleUse.TryGetValue(name, out var group) &&
                !seen.Add(group))
            {
                Report(context, LinqDiagnostics.SingleUse, link, group);
                continue;
            }

            if (name == "Reverse" &&
                !ordered)
            {
                Report(context, LinqDiagnostics.UnorderedReverse, link);
            }

            if (SupportedLinq.Ordering.Contains(name))
            {
                ordered = true;
                Key(context, link, name);
            }

            if (name == "Select")
            {
                Projection(context, link);
            }

            if (name == "GroupJoin")
            {
                Group(context, link);
            }

            ExpressionRules.Check(context, link);
        }
    }

    // The bookkeeping a rule about the whole query rests on, kept up to date for links that are not
    // this statement's to report.
    static void Track(HashSet<string> seen, ref bool ordered, string name)
    {
        if (SupportedLinq.SingleUse.TryGetValue(name, out var group))
        {
            seen.Add(group);
        }

        if (SupportedLinq.Ordering.Contains(name))
        {
            ordered = true;
        }
    }

    // An overload outside the set, told apart by what the extra operand is. A comparer is the common
    // one and has its own reason; the rest are named so the message says which overload was written
    // rather than only which operator.
    static void Overload(OperationAnalysisContext context, IInvocationOperation link, KnownTypes known, IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (known.IsComparer(parameter.Type))
            {
                Report(context, LinqDiagnostics.Comparer, link, method.Name);
                return;
            }
        }

        if (method.Name == "SelectMany")
        {
            Report(context, LinqDiagnostics.ResultSelector, link);
            return;
        }

        if (method.Name == "GroupBy")
        {
            Report(context, LinqDiagnostics.UnsupportedOperator, link, "GroupBy with an element selector");
            return;
        }

        Report(context, LinqDiagnostics.UnsupportedOperator, link, $"this overload of {method.Name}");
    }

    // An ordering takes one value. A constructed key has no ordering of its own, and the wire carries
    // no constructed value outside a projection.
    static void Key(OperationAnalysisContext context, IInvocationOperation link, string name)
    {
        if (ExpressionRules.Body(QueryChain.Lambda(link, 1)) is { } body &&
            ExpressionRules.Constructs(body))
        {
            Report(context, LinqDiagnostics.OrderingKey, link, name);
        }
    }

    // A projection must construct an object: a response is keyed by member name, and a bare value has
    // no member name to key it by.
    static void Projection(OperationAnalysisContext context, IInvocationOperation link)
    {
        if (ExpressionRules.Body(QueryChain.Lambda(link, 1)) is { } body &&
            !ExpressionRules.Constructs(body))
        {
            Report(context, LinqDiagnostics.Projection, link);
        }
    }

    // The inner side of a group join is a group, not a row, so the only thing a result member can be
    // there is an aggregate folding it. Anything else would put a nested collection in the response.
    static void Group(OperationAnalysisContext context, IInvocationOperation link)
    {
        if (QueryChain.Lambda(link, 4) is not {Symbol.Parameters.Length: 2} result)
        {
            return;
        }

        var group = result.Symbol.Parameters[1];
        foreach (var operation in ExpressionRules.Descendants(result))
        {
            if (operation is not IParameterReferenceOperation reference ||
                !SymbolEqualityComparer.Default.Equals(reference.Parameter, group) ||
                ExpressionRules.IsFolded(reference))
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(LinqDiagnostics.ProjectedGroup, reference.Syntax.GetLocation(), group.Name));
        }
    }

    // A terminal that is not one of Scry's own runs the query where it stands. The capture-only
    // provider throws on synchronous enumeration rather than blocking an HTTP request out of it.
    static bool ReturnsQuery(IMethodSymbol method) =>
        method.ReturnType is INamedTypeSymbol {IsGenericType: true, Name: "IQueryable" or "IOrderedQueryable"};

    // The row type a link reads — the source's element, before the operator reshapes it. What says
    // whether a whole-model query would have carried attachments into the operator.
    static ITypeSymbol? ElementOf(IInvocationOperation link) =>
        link.Arguments.Length > 0 &&
        link.Arguments[0].Value.Type is INamedTypeSymbol {IsGenericType: true} source
            ? source.TypeArguments[0]
            : null;

    static void Report(OperationAnalysisContext context, DiagnosticDescriptor rule, IInvocationOperation link, params object?[] arguments) =>
        context.ReportDiagnostic(Diagnostic.Create(rule, QueryChain.Where(link), arguments));
}
