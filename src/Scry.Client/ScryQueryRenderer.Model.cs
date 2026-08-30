/// <summary>
/// The renderer's internal refusal signal. Thrown from any depth and caught at the public surface,
/// so no shape — however malformed — escapes as an exception.
/// </summary>
sealed class RenderRefusalException(RenderRefusal refusal) :
    Exception
{
    public RenderRefusal Refusal { get; } = refusal;
}

/// <summary>
/// One rendering pass over a request: the entry point, the terminal map, the default-projection
/// detection, and the model tracking every type-directed decision reads.
/// </summary>
partial class QueryRenderer(Type? rootModel)
{
    readonly Type? rootModel = rootModel;

    // The model of the row the pipeline is currently reading: the root's, then whatever OfType or
    // SelectMany narrowed or flattened to, and null after a Select/Join/Set leaves an anonymous
    // shape. Null is not a refusal on its own — only a construct that needs the model refuses.
    Type? currentModel;

    // Grouping state, set by GroupBy and cleared by the Select that consumes it. The keys are what a
    // bare member read inside the group resolves against; the names are the composite key's
    // anonymous-type member names, null while the key is single.
    bool grouped;
    IReadOnlyList<Node>? groupKeys;
    List<string>? groupKeyNames;

    public string Render(QueryRequest request)
    {
        currentModel = rootModel;
        var pipeline = request.Pipeline;
        QueryOp? terminal = null;
        var count = pipeline.Count;
        if (count > 0 && IsTerminal(pipeline[^1]))
        {
            terminal = pipeline[^1];
            count--;
        }

        // A terminal anywhere but last is not a pipeline the client can capture.
        for (var i = 0; i < count; i++)
        {
            if (IsTerminal(pipeline[i]))
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }
        }

        var terminalText = TerminalText(terminal);
        var body = pipeline.Take(count).ToList();
        if (IsDefaultProjection(body, terminal))
        {
            body.RemoveAt(body.Count - 1);
        }

        var builder = new StringBuilder();
        builder.Append("Query.").Append(request.Root);
        foreach (var op in body)
        {
            builder.Append('\n').Append(RenderOp(op));
        }

        builder.Append('\n').Append(terminalText);
        return builder.ToString();
    }

    static bool IsTerminal(QueryOp op) =>
        op is CountOp or LongCountOp or AnyOp or AllOp or FirstOp or SingleOp or LastOp or AggregateOp or PageOp;

    // Only terminals the explorer folds back into the identical wire op are spelled. A
    // predicate-carrying terminal has no such spelling: `.Where(p).FirstAsync()` produces different
    // bytes (a separate WhereOp, and a default projection the predicate form suppresses).
    static string TerminalText(QueryOp? terminal) =>
        terminal switch
        {
            null => ".ToListAsync()",
            CountOp {Predicate: null} => ".CountAsync()",
            AnyOp {Predicate: null} => ".AnyAsync()",
            FirstOp {Predicate: null} first => first.OrDefault ? ".FirstOrDefaultAsync()" : ".FirstAsync()",
            SingleOp {Predicate: null} single => single.OrDefault ? ".SingleOrDefaultAsync()" : ".SingleAsync()",
            _ => throw Refuse(RenderRefusal.UnsupportedTerminal)
        };

    /// <summary>
    /// Whether the trailing Select is exactly the one <c>ToScryRequest</c> appends on its own when
    /// the query wrote none — in which case the snippet omits it and the explorer's forward
    /// translation re-appends the identical operator. Anything inexact renders the Select
    /// explicitly, which always round-trips because an explicit Select suppresses the default.
    /// </summary>
    bool IsDefaultProjection(List<QueryOp> body, QueryOp? terminal)
    {
        if (body.Count == 0 ||
            body[^1] is not SelectOp select)
        {
            return false;
        }

        // Terminals that suppress the default projection: with the Select omitted, the forward pass
        // would not re-append it. Only renderable terminals can reach here.
        if (terminal is CountOp or AnyOp)
        {
            return false;
        }

        var leading = body.Take(body.Count - 1).ToList();
        if (leading.Any(_ => _ is SelectOp or GroupByOp or JoinOp or SetOp))
        {
            return false;
        }

        // The members come off the element model the pipeline ended with, exactly as
        // AddDefaultProjection selects them. Unresolvable means undetectable, which is safe: the
        // Select is rendered explicitly instead.
        var element = rootModel;
        foreach (var op in leading)
        {
            switch (op)
            {
                case OfTypeOp ofType:
                    element = SensitiveModel.ModelFor(ofType.Type);
                    break;
                case SelectManyOp many:
                    element = ElementModel(element, many.Path);
                    break;
            }
        }

        if (element?.GetCustomAttribute<ScryModelAttribute>()?.Members is not {Count: > 0} members ||
            members.Count != select.Projection.Members.Count)
        {
            return false;
        }

        for (var i = 0; i < members.Count; i++)
        {
            var member = select.Projection.Members[i];
            if (member.Name != members[i] ||
                member.Value is not NodeValue {Node: MemberNode {Path: [var single]}} ||
                single != members[i])
            {
                return false;
            }
        }

        return true;
    }

    // The element model a member path's final collection yields, or null where any step cannot be
    // resolved.
    static Type? ElementModel(Type? model, IReadOnlyList<string> path)
    {
        if (Walk(model, path) is not { } property)
        {
            return null;
        }

        return SensitiveModel.Element(property.PropertyType);
    }

    // Walks a member path from a model to the property its last segment names, stepping through
    // navigations and collections between segments. Null where the model is unknown or a segment
    // does not resolve.
    static PropertyInfo? Walk(Type? model, IReadOnlyList<string> path)
    {
        if (model is null ||
            path.Count == 0)
        {
            return null;
        }

        PropertyInfo? property = null;
        var current = model;
        foreach (var segment in path)
        {
            property = SensitiveModel.Property(current, segment);
            if (property is null)
            {
                return null;
            }

            current = SensitiveModel.Element(property.PropertyType);
        }

        return property;
    }

    /// <summary>
    /// The CLR type an expression resolves to, where the renderer can know it — a member's declared
    /// type, or the fixed result type of a known function. Null means unknown, which each caller
    /// treats as its own need dictates.
    /// </summary>
    static Type? InferType(Node node, Scope scope) =>
        node switch
        {
            MemberNode member => Walk(scope.Model, member.Path)?.PropertyType,
            ElementNode => scope.Model,
            UnaryNode unary => InferType(unary.Operand, scope),
            CallNode call => call.Function switch
            {
                KnownFunction.DateDayOfWeek => typeof(DayOfWeek),
                KnownFunction.DateTimeOfDay => typeof(TimeSpan),
                KnownFunction.DateDate => typeof(DateTime),
                KnownFunction.DateOnlyFromDateTime => typeof(Date),
                KnownFunction.TimeOnlyFromDateTime or KnownFunction.TimeOnlyFromTimeSpan => typeof(Time),
                KnownFunction.DateTimeFromDateAndTime => typeof(DateTime),
                KnownFunction.UnixSecondsFromOffset or KnownFunction.UnixMillisecondsFromOffset => typeof(long),
                KnownFunction.DateAddYears or
                    KnownFunction.DateAddMonths or
                    KnownFunction.DateAddDays or
                    KnownFunction.DateAddHours or
                    KnownFunction.DateAddMinutes or
                    KnownFunction.DateAddSeconds or
                    KnownFunction.DateAddMilliseconds => InferType(call.Target, scope),
                _ => null
            },
            _ => null
        };

    // The C# spelling of a type, for the typed arrays an In test constructs.
    static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return $"{TypeName(underlying)}?";
        }

        if (type == typeof(int))
        {
            return "int";
        }

        if (type == typeof(long))
        {
            return "long";
        }

        if (type == typeof(short))
        {
            return "short";
        }

        if (type == typeof(byte))
        {
            return "byte";
        }

        if (type == typeof(bool))
        {
            return "bool";
        }

        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(char))
        {
            return "char";
        }

        if (type == typeof(decimal))
        {
            return "decimal";
        }

        if (type == typeof(double))
        {
            return "double";
        }

        if (type == typeof(float))
        {
            return "float";
        }

        return type.Name;
    }

    static RenderRefusalException Refuse(RenderRefusal refusal) =>
        new(refusal);
}
