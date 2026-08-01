/// <summary>
/// Finds the LINQ chains that are written against a Scry source, and walks each one root-first.
///
/// A chain is recognised by what it bottoms out at rather than by what it is called on: the generated
/// models carry <c>[ScryModel]</c>, so an <c>IQueryable&lt;T&gt;</c> whose element carries it is a Scry
/// source however many locals or helper calls it passed through. That matters because the element
/// type stops being a model the moment a Select projects an anonymous type, and the operators after
/// that Select are exactly the ones worth reporting on.
/// </summary>
static class QueryChain
{
    /// <summary>
    /// The calls in <paramref name="outermost"/>'s chain, root-first, or null when the chain is not
    /// written against a Scry source. A chain composed across statements is followed through the
    /// locals it was held in, so what the query carries in total is visible from its last statement —
    /// which is the only place a rule about the whole query can be checked.
    /// </summary>
    /// <param name="inherited">
    /// How many of the leading links came from a followed local. Those were written in another
    /// statement and reported there, so they count towards the query's state without being reported
    /// again here.
    /// </param>
    public static List<IInvocationOperation>? Of(IInvocationOperation outermost, KnownTypes known, out int inherited)
    {
        inherited = 0;
        var links = new List<IInvocationOperation>();
        var written = 0;
        var crossed = false;
        IOperation? current = outermost;
        while (true)
        {
            if (current is IInvocationOperation call && IsLink(call, known))
            {
                links.Add(call);
                if (!crossed)
                {
                    written++;
                }

                current = Unwrap(Source(call));
                continue;
            }

            if (current is ILocalReferenceOperation reference &&
                Initializer(reference) is { } initializer)
            {
                crossed = true;
                current = Unwrap(initializer);
                continue;
            }

            break;
        }

        if (current is null ||
            links.Count == 0 ||
            !IsSource(current, known))
        {
            return null;
        }

        links.Reverse();
        inherited = links.Count - written;
        return links;
    }

    // What a local was declared with, and only when it was never assigned again: a reassigned local no
    // longer stands for the chain its declaration gave it, and following one would report against a
    // query that was never written.
    static IOperation? Initializer(ILocalReferenceOperation reference)
    {
        IOperation? initializer = null;
        foreach (var operation in ExpressionRules.Descendants(Root(reference)))
        {
            if (operation is ISimpleAssignmentOperation {Target: ILocalReferenceOperation assigned} &&
                SymbolEqualityComparer.Default.Equals(assigned.Local, reference.Local))
            {
                return null;
            }

            if (operation is IVariableDeclaratorOperation declarator &&
                SymbolEqualityComparer.Default.Equals(declarator.Symbol, reference.Local))
            {
                initializer = declarator.Initializer?.Value;
            }
        }

        return initializer;
    }

    static IOperation Root(IOperation operation)
    {
        var current = operation;
        while (current.Parent is { } parent)
        {
            current = parent;
        }

        return current;
    }

    /// <summary>
    /// Whether some enclosing call carries this one as its source, in which case that call is the one
    /// to analyse — a chain is walked once, from its outermost link.
    /// </summary>
    public static bool IsInner(IInvocationOperation invocation)
    {
        var parent = invocation.Parent;
        while (parent is IConversionOperation or IParenthesizedOperation or IArgumentOperation)
        {
            parent = parent.Parent;
        }

        return parent is IInvocationOperation call &&
               ReferenceEquals(Unwrap(Source(call)), invocation);
    }

    /// <summary>
    /// Whether this sits inside a lambda that a query operator was handed. Such a chain reads a row
    /// rather than composing the query — a membership test against another source, an aggregate over a
    /// collection navigation — and the rules for what it may carry are different ones. Left to the
    /// translator rather than guessed at here.
    /// </summary>
    public static bool InsideQueryLambda(IOperation operation, KnownTypes known)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is not IAnonymousFunctionOperation)
            {
                continue;
            }

            var parent = current.Parent;
            while (parent is IConversionOperation or IDelegateCreationOperation or IArgumentOperation)
            {
                parent = parent.Parent;
            }

            if (parent is IInvocationOperation call && IsLink(call, known))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The expression a call was written against. An extension method in reduced form carries its
    /// receiver as argument zero rather than as an instance, which is the form every link here takes.
    /// </summary>
    public static IOperation? Source(IInvocationOperation invocation)
    {
        if (invocation.Instance is { } instance)
        {
            return instance;
        }

        if (invocation.Arguments.Length > 0)
        {
            return invocation.Arguments[0].Value;
        }

        return null;
    }

    /// <summary>
    /// The lambda handed to a call at that <c>Queryable</c> argument position — position zero being
    /// the source — or null when the operand is not a lambda.
    /// </summary>
    public static IAnonymousFunctionOperation? Lambda(IInvocationOperation invocation, int index)
    {
        // Arguments are indexed by unreduced position, so they line up with Queryable's own signature.
        // A genuine instance call carries no source among them and shifts down by one.
        var position = invocation.Instance is null ? index : index - 1;
        if (position < 0 ||
            position >= invocation.Arguments.Length)
        {
            return null;
        }

        var value = invocation.Arguments[position].Value;
        while (value is IConversionOperation or IDelegateCreationOperation)
        {
            value = value switch
            {
                IConversionOperation conversion => conversion.Operand,
                IDelegateCreationOperation creation => creation.Target,
                _ => value
            };
        }

        return value as IAnonymousFunctionOperation;
    }

    /// <summary>
    /// The method as <c>Queryable</c> declares it. A call written in reduced form resolves to a symbol
    /// with the source dropped from its parameters, and every arity here is counted the other way.
    /// </summary>
    public static IMethodSymbol Unreduced(IInvocationOperation invocation) =>
        invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;

    /// <summary>
    /// Where to put the squiggle: the operator's own name, rather than the whole chain leading up to
    /// it, which is what the invocation's syntax spans.
    /// </summary>
    public static Location Where(IInvocationOperation invocation)
    {
        if (invocation.Syntax is InvocationExpressionSyntax {Expression: MemberAccessExpressionSyntax member})
        {
            return member.Name.GetLocation();
        }

        return invocation.Syntax.GetLocation();
    }

    // Only the extension methods that compose or terminate a query are followed. ScryClient.Source is
    // deliberately absent: it opens a chain rather than continuing one, so it is a root below.
    static bool IsLink(IInvocationOperation invocation, KnownTypes known)
    {
        var containing = invocation.TargetMethod.ContainingType;
        return Named(containing, known.Queryable) ||
               Named(containing, known.Enumerable) ||
               Named(containing, known.Extensions) ||
               Named(containing, known.Batch);
    }

    static bool IsSource(IOperation operation, KnownTypes known)
    {
        if (operation is IInvocationOperation {TargetMethod.Name: "Source"} call &&
            Named(call.TargetMethod.ContainingType, known.Client))
        {
            return true;
        }

        return Element(operation.Type) is { } element && known.IsModel(element);
    }

    // The T of an IQueryable<T>, whatever the static type spells it as — IOrderedQueryable<T>, or the
    // interface itself.
    static ITypeSymbol? Element(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return null;
        }

        if (IsQueryable(named))
        {
            return named.TypeArguments[0];
        }

        foreach (var contract in named.AllInterfaces)
        {
            if (IsQueryable(contract))
            {
                return contract.TypeArguments[0];
            }
        }

        return null;
    }

    static bool IsQueryable(INamedTypeSymbol type) =>
        type is {IsGenericType: true, TypeArguments.Length: 1, Name: "IQueryable"} &&
        type.ContainingNamespace.ToDisplayString() == "System.Linq";

    static bool Named(ISymbol? symbol, ISymbol? other) =>
        other is not null && SymbolEqualityComparer.Default.Equals(symbol, other);

    static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation or IParenthesizedOperation)
        {
            operation = operation switch
            {
                IConversionOperation conversion => conversion.Operand,
                IParenthesizedOperation parenthesized => parenthesized.Operand,
                _ => operation
            };
        }

        return operation;
    }
}
