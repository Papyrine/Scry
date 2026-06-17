using Pneumatic.Wire;

namespace Pneumatic;

/// <summary>
/// The authoritative server-side gate. Walks an incoming query AST and rejects anything that is not
/// allow-listed or exceeds a resource limit — independent of whatever code the client was generated
/// against. Runs before any expression is rebound or executed.
/// </summary>
sealed class QueryValidator(PneumaticSchema schema, PneumaticOptions options)
{
    public PneumaticSource Validate(QueryRequest request)
    {
        if (request.Version > WireFormat.Version)
        {
            throw Reject($"Unsupported wire version {request.Version}.");
        }

        if (!schema.TryGetSource(request.Root, out var source))
        {
            throw Reject($"Unknown source '{request.Root}'.");
        }

        if (request.Pipeline.Count > options.MaxPipelineLength)
        {
            throw Reject($"Pipeline exceeds the maximum length of {options.MaxPipelineLength}.");
        }

        ValidatePipeline(request.Pipeline, source.ClrType);
        return source;
    }

    void ValidatePipeline(IReadOnlyList<QueryOp> pipeline, Type rootType)
    {
        var sawOrdering = false;
        var sawGroupBy = false;
        var sawSelect = false;
        var terminalIndex = -1;
        IReadOnlyList<MemberExpr>? groupKeys = null;

        for (var i = 0; i < pipeline.Count; i++)
        {
            var op = pipeline[i];

            if (terminalIndex >= 0)
            {
                throw Reject("No operator may follow a terminal operator.");
            }

            switch (op)
            {
                case WhereOp where:
                    EnsureNotGrouped(sawGroupBy, "Where");
                    EnsureNotProjected(sawSelect, "Where");
                    ValidatePredicate(where.Predicate, rootType);
                    break;

                case OrderByOp orderBy:
                    EnsureNotGrouped(sawGroupBy, "OrderBy");
                    EnsureNotProjected(sawSelect, "OrderBy");
                    ValidateScalar(orderBy.Key, rootType, "OrderBy key");
                    sawOrdering = true;
                    break;

                case ThenByOp thenBy:
                    if (!sawOrdering)
                    {
                        throw Reject("ThenBy must follow OrderBy.");
                    }

                    ValidateScalar(thenBy.Key, rootType, "ThenBy key");
                    break;

                case SkipOp skip:
                    EnsureNonNegative(skip.Count, "Skip");
                    break;

                case TakeOp take:
                    EnsureNonNegative(take.Count, "Take");
                    if (take.Count > options.MaxPageSize)
                    {
                        throw Reject($"Take {take.Count} exceeds the maximum page size of {options.MaxPageSize}.");
                    }

                    break;

                case GroupByOp groupBy:
                    if (sawGroupBy)
                    {
                        throw Reject("Only one GroupBy is allowed.");
                    }

                    if (sawSelect)
                    {
                        throw Reject("GroupBy must precede Select.");
                    }

                    if (groupBy.Keys.Count != 1)
                    {
                        throw Reject("Exactly one GroupBy key is supported.");
                    }

                    foreach (var key in groupBy.Keys)
                    {
                        ValidateScalar(key, rootType, "GroupBy key");
                    }

                    groupKeys = [..groupBy.Keys.OfType<MemberExpr>()];
                    sawGroupBy = true;
                    break;

                case SelectOp select:
                    if (sawSelect)
                    {
                        throw Reject("Only one Select is allowed.");
                    }

                    ValidateProjection(select.Projection, rootType, sawGroupBy, groupKeys, depth: 0);
                    sawSelect = true;
                    break;

                case CountOp:
                    terminalIndex = i;
                    break;

                case AnyOp any:
                    ValidateTerminalPredicate(any.Predicate, rootType, sawSelect);
                    terminalIndex = i;
                    break;

                case FirstOp first:
                    ValidateTerminalPredicate(first.Predicate, rootType, sawSelect);
                    terminalIndex = i;
                    break;

                case SingleOp single:
                    ValidateTerminalPredicate(single.Predicate, rootType, sawSelect);
                    terminalIndex = i;
                    break;

                default:
                    throw Reject($"Unsupported operator '{op.GetType().Name}'.");
            }
        }

        if (sawGroupBy && !sawSelect)
        {
            throw Reject("GroupBy must be followed by a Select.");
        }
    }

    void ValidateTerminalPredicate(Expr? predicate, Type rootType, bool sawSelect)
    {
        if (predicate is null)
        {
            return;
        }

        if (sawSelect)
        {
            throw Reject("A terminal predicate is not allowed after a Select.");
        }

        ValidatePredicate(predicate, rootType);
    }

    void ValidateProjection(
        Projection projection,
        Type rootType,
        bool grouped,
        IReadOnlyList<MemberExpr>? groupKeys,
        int depth)
    {
        if (depth > options.MaxNavigationDepth)
        {
            throw Reject("Projection nesting is too deep.");
        }

        if (projection.Members.Count == 0)
        {
            throw Reject("A projection must have at least one member.");
        }

        foreach (var member in projection.Members)
        {
            switch (member.Value)
            {
                case ExprValue { Expression: AggregateExpr aggregate }:
                    if (!grouped)
                    {
                        throw Reject("Aggregates are only allowed in a Select following GroupBy.");
                    }

                    if (aggregate.Selector is { } selector)
                    {
                        ValidateScalar(selector, rootType, "Aggregate selector");
                    }

                    break;

                case ExprValue { Expression: MemberExpr memberExpr }:
                    if (grouped)
                    {
                        if (groupKeys is null || !groupKeys.Any(_ => PathEquals(_.Path, memberExpr.Path)))
                        {
                            throw Reject("A grouped projection may only reference the group key or aggregates.");
                        }
                    }
                    else
                    {
                        ResolvePath(memberExpr.Path, rootType, requireScalar: true, "Projection member");
                    }

                    break;

                case ExprValue:
                    throw Reject("Unsupported projection expression.");

                case NestedValue nested:
                    if (grouped)
                    {
                        throw Reject("Nested projections are not allowed in a grouped Select.");
                    }

                    var target = ResolveNavigation(nested.Path, rootType);
                    ValidateProjection(nested.Projection, target, grouped: false, groupKeys: null, depth + 1);
                    break;

                default:
                    throw Reject("Unsupported projection member.");
            }
        }
    }

    void ValidatePredicate(Expr expr, Type elementType) =>
        ValidateExpr(expr, elementType, depth: 0);

    void ValidateScalar(Expr expr, Type elementType, string what)
    {
        ValidateExpr(expr, elementType, depth: 0);
        if (expr is MemberExpr member)
        {
            ResolvePath(member.Path, elementType, requireScalar: true, what);
        }
    }

    void ValidateExpr(Expr expr, Type elementType, int depth)
    {
        if (depth > options.MaxExpressionDepth)
        {
            throw Reject("Expression nesting is too deep.");
        }

        switch (expr)
        {
            case MemberExpr member:
                ResolvePath(member.Path, elementType, requireScalar: false, "Member");
                break;

            case ConstExpr:
                break;

            case BinaryExpr binary:
                ValidateExpr(binary.Left, elementType, depth + 1);
                ValidateExpr(binary.Right, elementType, depth + 1);
                break;

            case UnaryExpr unary:
                ValidateExpr(unary.Operand, elementType, depth + 1);
                break;

            case CallExpr call:
                ValidateExpr(call.Target, elementType, depth + 1);
                foreach (var argument in call.Arguments)
                {
                    ValidateExpr(argument, elementType, depth + 1);
                }

                break;

            case AggregateExpr:
                throw Reject("Aggregates are only allowed as a projection member in a grouped Select.");

            default:
                throw Reject($"Unsupported expression '{expr.GetType().Name}'.");
        }
    }

    Type ResolveNavigation(IReadOnlyList<string> path, Type rootType)
    {
        var member = ResolvePath(path, rootType, requireScalar: false, "Navigation");
        if (member.Kind != MemberKind.Navigation)
        {
            throw Reject($"'{string.Join(".", path)}' is not a navigation property.");
        }

        return member.Type;
    }

    PneumaticMember ResolvePath(IReadOnlyList<string> path, Type rootType, bool requireScalar, string what)
    {
        if (path.Count == 0)
        {
            throw Reject($"{what} has an empty member path.");
        }

        if (path.Count > options.MaxNavigationDepth)
        {
            throw Reject($"{what} path is too deep.");
        }

        var currentType = rootType;
        PneumaticMember? member = null;

        for (var i = 0; i < path.Count; i++)
        {
            if (!schema.TryGetType(currentType, out var meta))
            {
                throw Reject($"Type '{currentType.Name}' is not queryable.");
            }

            if (!meta.Members.TryGetValue(path[i], out member))
            {
                throw Reject($"Property '{path[i]}' is not allow-listed on '{currentType.Name}'.");
            }

            var isLast = i == path.Count - 1;
            if (!isLast)
            {
                if (member.Kind != MemberKind.Navigation)
                {
                    throw Reject($"Cannot traverse through non-navigation '{path[i]}'.");
                }

                currentType = member.Type;
            }
        }

        if (requireScalar && member!.Kind != MemberKind.Scalar)
        {
            throw Reject($"{what} must reference a scalar value.");
        }

        return member!;
    }

    static bool PathEquals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    static void EnsureNotGrouped(bool sawGroupBy, string op)
    {
        if (sawGroupBy)
        {
            throw Reject($"{op} is not allowed after GroupBy.");
        }
    }

    static void EnsureNotProjected(bool sawSelect, string op)
    {
        if (sawSelect)
        {
            throw Reject($"{op} is not allowed after Select.");
        }
    }

    static void EnsureNonNegative(int count, string op)
    {
        if (count < 0)
        {
            throw Reject($"{op} count must be non-negative.");
        }
    }

    static PneumaticValidationException Reject(string message) =>
        new(message);
}
