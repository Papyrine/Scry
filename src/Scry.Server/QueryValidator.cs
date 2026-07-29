/// <summary>
/// The authoritative server-side gate. Walks an incoming query AST and rejects anything that is not
/// allow-listed or exceeds a resource limit — independent of whatever code the client was generated
/// against. Runs before any expression is rebound or executed.
/// </summary>
sealed class QueryValidator(Schema schema, ScryOptions options)
{
    public ScrySource Validate(QueryRequest request)
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
        var sawDistinct = false;
        var sawJoin = false;
        var terminalIndex = -1;
        IReadOnlyList<MemberNode>? groupKeys = null;
        Projection? projection = null;

        for (var i = 0; i < pipeline.Count; i++)
        {
            var op = pipeline[i];

            if (terminalIndex >= 0)
            {
                throw Reject("No operator may follow a terminal operator.");
            }

            // A join fixes the row shape — its projection names both sides, and every later operator is
            // single-rooted, so none of them could say which side it meant.
            if (sawJoin &&
                op is not (CountOp or LongCountOp or AnyOp or FirstOp or SingleOp))
            {
                throw Reject("Only Count, LongCount, Any, First, or Single may follow a Join.");
            }

            switch (op)
            {
                case WhereOp where:
                    EnsureNotProjected(sawSelect, "Where");
                    EnsureNotDistinct(sawDistinct, "Where");
                    if (sawGroupBy)
                    {
                        // Written after GroupBy it filters the groups, not the rows — SQL HAVING.
                        ValidateHaving(where.Predicate, rootType, groupKeys, depth: 0);
                    }
                    else
                    {
                        ValidatePredicate(where.Predicate, rootType);
                    }

                    break;

                case OrderByOp orderBy:
                    EnsureNotGrouped(sawGroupBy, "OrderBy");
                    EnsureNotProjected(sawSelect, "OrderBy");
                    EnsureNotDistinct(sawDistinct, "OrderBy");
                    ValidateScalar(orderBy.Key, rootType, "OrderBy key");
                    sawOrdering = true;
                    break;

                case ThenByOp thenBy:
                    if (!sawOrdering)
                    {
                        throw Reject("ThenBy must follow OrderBy.");
                    }

                    EnsureNotDistinct(sawDistinct, "ThenBy");
                    ValidateScalar(thenBy.Key, rootType, "ThenBy key");
                    break;

                case SkipOp skip:
                    EnsureNotDistinct(sawDistinct, "Skip");
                    EnsureNonNegative(skip.Count, "Skip");
                    break;

                case TakeOp take:
                    EnsureNotDistinct(sawDistinct, "Take");
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

                    EnsureNotDistinct(sawDistinct, "GroupBy");

                    if (groupBy.Keys.Count != 1)
                    {
                        throw Reject("Exactly one GroupBy key is supported.");
                    }

                    foreach (var key in groupBy.Keys)
                    {
                        ValidateScalar(key, rootType, "GroupBy key");
                    }

                    groupKeys = [..groupBy.Keys.OfType<MemberNode>()];
                    sawGroupBy = true;
                    break;

                case SelectOp select:
                    if (sawSelect)
                    {
                        throw Reject("Only one Select is allowed.");
                    }

                    ValidateProjection(select.Projection, rootType, sawGroupBy, groupKeys, depth: 0);
                    projection = select.Projection;
                    sawSelect = true;
                    break;

                case DistinctOp:
                    if (sawDistinct)
                    {
                        throw Reject("Only one Distinct is allowed.");
                    }

                    sawDistinct = true;
                    break;

                case ReverseOp:
                    if (!sawOrdering)
                    {
                        throw Reject("Reverse requires an ordered query — add an OrderBy.");
                    }

                    EnsureNotGrouped(sawGroupBy, "Reverse");
                    EnsureNotDistinct(sawDistinct, "Reverse");
                    break;

                case JoinOp join:
                    if (sawJoin)
                    {
                        throw Reject("Only one Join is allowed.");
                    }

                    EnsureNotGrouped(sawGroupBy, "Join");
                    EnsureNotProjected(sawSelect, "Join");
                    EnsureNotDistinct(sawDistinct, "Join");
                    ValidateJoin(join, rootType);
                    sawJoin = true;
                    break;

                case CountOp count:
                    ValidateTerminalPredicate(count.Predicate, rootType, sawSelect, sawJoin);
                    EnsureFoldableDistinct(sawDistinct, sawGroupBy, projection, "Count");
                    terminalIndex = i;
                    break;

                case LongCountOp longCount:
                    ValidateTerminalPredicate(longCount.Predicate, rootType, sawSelect, sawJoin);
                    EnsureFoldableDistinct(sawDistinct, sawGroupBy, projection, "LongCount");
                    terminalIndex = i;
                    break;

                case AnyOp any:
                    ValidateTerminalPredicate(any.Predicate, rootType, sawSelect, sawJoin);
                    EnsureFoldableDistinct(sawDistinct, sawGroupBy, projection, "Any");
                    terminalIndex = i;
                    break;

                case AllOp all:
                    if (sawDistinct)
                    {
                        throw Reject("All is not supported over a Distinct query.");
                    }

                    // Unlike the other terminal predicates this one is required, but it reads the same
                    // row members, so it is subject to the same "not after a projection" rule.
                    ValidateTerminalPredicate(all.Predicate, rootType, sawSelect);
                    terminalIndex = i;
                    break;

                case FirstOp first:
                    ValidateTerminalPredicate(first.Predicate, rootType, sawSelect, sawJoin);
                    terminalIndex = i;
                    break;

                case SingleOp single:
                    ValidateTerminalPredicate(single.Predicate, rootType, sawSelect, sawJoin);
                    terminalIndex = i;
                    break;

                case LastOp last:
                    if (!sawOrdering)
                    {
                        throw Reject("Last requires an ordered query — add an OrderBy.");
                    }

                    ValidateTerminalPredicate(last.Predicate, rootType, sawSelect);
                    terminalIndex = i;
                    break;

                case AggregateOp aggregate:
                    if (aggregate.Function == AggregateFn.Count)
                    {
                        throw Reject("Use the Count terminal to count rows.");
                    }

                    if (sawGroupBy || sawSelect)
                    {
                        throw Reject("An aggregate terminal must precede GroupBy and Select.");
                    }

                    if (sawDistinct)
                    {
                        throw Reject("An aggregate terminal is not supported over a Distinct query.");
                    }

                    ValidateScalar(aggregate.Selector, rootType, "Aggregate selector");
                    terminalIndex = i;
                    break;

                case PageOp page:
                    if (sawGroupBy)
                    {
                        throw Reject("Paging is not supported over a grouped query.");
                    }

                    if (sawDistinct)
                    {
                        throw Reject("Paging is not supported over a Distinct query.");
                    }

                    if (page.Size is { } pageSize)
                    {
                        EnsureNonNegative(pageSize, "Page size");
                        if (pageSize > options.MaxPageSize)
                        {
                            throw Reject($"Page size {pageSize} exceeds the maximum page size of {options.MaxPageSize}.");
                        }
                    }

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

    void ValidateTerminalPredicate(Node? predicate, Type rootType, bool sawSelect, bool sawJoin = false)
    {
        if (predicate is null)
        {
            return;
        }

        if (sawJoin)
        {
            throw Reject("A terminal predicate is not allowed after a Join — filter each side before joining.");
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
        IReadOnlyList<MemberNode>? groupKeys,
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
                case NodeValue { Node: AggregateNode aggregate }:
                    if (!grouped)
                    {
                        throw Reject("Aggregates are only allowed in a Select following GroupBy.");
                    }

                    if (aggregate.Selector is { } selector)
                    {
                        ValidateScalar(selector, rootType, "Aggregate selector");
                    }

                    break;

                case NodeValue { Node: MemberNode memberNode }:
                    if (grouped)
                    {
                        if (groupKeys is null ||
                            !groupKeys.Any(_ => PathEquals(_.Path, memberNode.Path)))
                        {
                            throw Reject("A grouped projection may only reference the group key or aggregates.");
                        }
                    }
                    else
                    {
                        ResolvePath(memberNode.Path, rootType, requireScalar: true, "Projection member");
                    }

                    break;

                // Any other expression: validated exactly as a predicate's would be, against the same
                // allow-list, function set and depth limit. A projection is one more place a row can be
                // read from, not a place where more can be read.
                case NodeValue value:
                    if (grouped)
                    {
                        throw Reject("A grouped projection may only reference the group key or aggregates.");
                    }

                    // A leaf that reads nothing is a value the client already has, and a provider has
                    // no column to compute it from — EF rejects a constant in a client projection
                    // outright. Caught here so it reads as the rejection it is.
                    if (!ReadsRow(value.Node))
                    {
                        throw Reject("A projection member must read at least one member of the row.");
                    }

                    ValidateExpr(value.Node, rootType, depth: 0);
                    break;

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

    /// <summary>
    /// Validates a <c>HAVING</c> predicate. It reads a group rather than a row, so the only members it
    /// may name are the group key — every other column has been folded away — and aggregates, which are
    /// rejected everywhere else outside a grouped projection.
    /// </summary>
    void ValidateHaving(Node node, Type elementType, IReadOnlyList<MemberNode>? groupKeys, int depth)
    {
        if (depth > options.MaxExpressionDepth)
        {
            throw Reject("Expression nesting is too deep.");
        }

        switch (node)
        {
            case MemberNode member:
                if (groupKeys is null ||
                    !groupKeys.Any(_ => PathEquals(_.Path, member.Path)))
                {
                    throw Reject("A predicate after GroupBy may only reference the group key or aggregates.");
                }

                break;

            case AggregateNode aggregate:
                if (aggregate.Selector is { } selector)
                {
                    ValidateScalar(selector, elementType, "Aggregate selector");
                }
                else if (aggregate.Function != AggregateFn.Count)
                {
                    throw Reject($"Aggregate '{aggregate.Function}' requires a selector.");
                }

                break;

            case ConstNode:
                break;

            case BinaryNode binary:
                ValidateHaving(binary.Left, elementType, groupKeys, depth + 1);
                ValidateHaving(binary.Right, elementType, groupKeys, depth + 1);
                break;

            case UnaryNode unary:
                ValidateHaving(unary.Operand, elementType, groupKeys, depth + 1);
                break;

            case ConditionalNode conditional:
                ValidateHaving(conditional.Test, elementType, groupKeys, depth + 1);
                ValidateHaving(conditional.IfTrue, elementType, groupKeys, depth + 1);
                ValidateHaving(conditional.IfFalse, elementType, groupKeys, depth + 1);
                break;

            case CallNode call:
                var (min, max) = Arity(call.Function);
                if (call.Arguments.Count < min ||
                    call.Arguments.Count > max)
                {
                    throw Reject($"Function '{call.Function}' does not take {call.Arguments.Count} argument(s).");
                }

                ValidateHaving(call.Target, elementType, groupKeys, depth + 1);
                foreach (var argument in call.Arguments)
                {
                    ValidateHaving(argument, elementType, groupKeys, depth + 1);
                }

                break;

            default:
                throw Reject($"Unsupported expression '{node.GetType().Name}'.");
        }
    }

    static bool ReadsRow(Node node) =>
        node switch
        {
            MemberNode => true,
            ConstNode => false,
            BinaryNode binary => ReadsRow(binary.Left) || ReadsRow(binary.Right),
            UnaryNode unary => ReadsRow(unary.Operand),
            ConditionalNode conditional => ReadsRow(conditional.Test) ||
                                           ReadsRow(conditional.IfTrue) ||
                                           ReadsRow(conditional.IfFalse),
            CallNode call => ReadsRow(call.Target) || call.Arguments.Any(ReadsRow),
            _ => true
        };

    void ValidatePredicate(Node expr, Type elementType) =>
        ValidateExpr(expr, elementType, depth: 0);

    void ValidateScalar(Node node, Type elementType, string what)
    {
        ValidateExpr(node, elementType, depth: 0);
        if (node is MemberNode member)
        {
            ResolvePath(member.Path, elementType, requireScalar: true, what);
        }
    }

    void ValidateExpr(Node node, Type elementType, int depth)
    {
        while (true)
        {
            if (depth > options.MaxExpressionDepth)
            {
                throw Reject("Expression nesting is too deep.");
            }

            switch (node)
            {
                case MemberNode member:
                    // A member used as a value in an expression must resolve to a scalar. Intermediate
                    // navigations/complex types in the path are traversed by ResolvePath; only the leaf
                    // must be scalar. This rejects e.g. a bare navigation or complex member compared to
                    // a constant at validation, rather than letting it fault during execution.
                    ResolvePath(member.Path, elementType, requireScalar: true, "Member");
                    break;

                case ConstNode:
                    break;

                case BinaryNode binary:
                    ValidateExpr(binary.Left, elementType, depth + 1);
                    node = binary.Right;
                    depth += 1;
                    continue;

                case UnaryNode unary:
                    node = unary.Operand;
                    depth += 1;
                    continue;

                case ConditionalNode conditional:
                    ValidateExpr(conditional.Test, elementType, depth + 1);
                    ValidateExpr(conditional.IfTrue, elementType, depth + 1);
                    node = conditional.IfFalse;
                    depth += 1;
                    continue;

                case CallNode call:
                    ValidateCall(call, elementType, depth);
                    break;

                case SubqueryNode subquery:
                    ValidateSubquery(subquery, elementType, depth);
                    break;

                case AggregateNode:
                    throw Reject("Aggregates are only allowed as a projection member in a grouped Select.");

                default:
                    throw Reject($"Unsupported expression '{node.GetType().Name}'.");
            }

            break;
        }
    }

    /// <summary>
    /// Validates a function call: that the function exists, that it was given the number of arguments
    /// the builder will read, and — for set membership — that the candidate values really are constants
    /// and stay within the configured cap. Arity is checked here rather than left to the builder so a
    /// malformed call is a rejected query, not a faulted one.
    /// </summary>
    void ValidateCall(CallNode call, Type elementType, int depth)
    {
        var (min, max) = Arity(call.Function);
        var count = call.Arguments.Count;
        if (count < min ||
            count > max)
        {
            throw Reject($"Function '{call.Function}' does not take {count} argument(s).");
        }

        if (call.Function == KnownFunction.In)
        {
            if (count > options.MaxInValues)
            {
                throw Reject($"A Contains set of {count} values exceeds the maximum of {options.MaxInValues}.");
            }

            if (call.Arguments.Any(_ => _ is not ConstNode))
            {
                throw Reject("Every value in a Contains set must be a constant.");
            }
        }

        ValidateExpr(call.Target, elementType, depth + 1);
        foreach (var argument in call.Arguments)
        {
            ValidateExpr(argument, elementType, depth + 1);
        }
    }

    /// <summary>
    /// Validates a join. The inner source is looked up by name in the same allow-list a request's own
    /// root goes through, and each projected member is validated against the side it names — the two
    /// sides never share an allow-list.
    /// </summary>
    void ValidateJoin(JoinOp join, Type outerType)
    {
        if (!schema.TryGetSource(join.Root, out var inner))
        {
            throw Reject($"Unknown source '{join.Root}'.");
        }

        var innerType = inner.ClrType;

        ValidateScalar(join.OuterKey, outerType, "Join key");
        ValidateScalar(join.InnerKey, innerType, "Join key");

        if (join.InnerPredicate is { } predicate)
        {
            ValidatePredicate(predicate, innerType);
        }

        if (join.Result.Count == 0)
        {
            throw Reject("A join must project at least one member.");
        }

        foreach (var member in join.Result)
        {
            var side = member.Side switch
            {
                JoinSide.Outer => outerType,
                JoinSide.Inner => innerType,
                _ => throw Reject($"Unsupported join side '{member.Side}'.")
            };

            ResolvePath(member.Path, side, requireScalar: true, "Join projection member");
        }
    }

    /// <summary>
    /// Validates a question asked about a collection navigation. The path must end at an exposed
    /// collection, and the inner predicate and selector are validated against the collection's element
    /// type — a different allow-list from the row the subquery hangs off, and the reason they cannot
    /// simply be validated in place.
    /// </summary>
    void ValidateSubquery(SubqueryNode subquery, Type elementType, int depth)
    {
        var member = ResolvePath(subquery.Path, elementType, requireScalar: false, "Subquery");
        if (member.Kind != MemberKind.Collection)
        {
            throw Reject($"'{string.Join('.', subquery.Path)}' is not an aggregable collection.");
        }

        var target = Schema.CollectionElement(member.Type) ??
                     throw Reject($"'{string.Join('.', subquery.Path)}' is not a collection.");

        switch (subquery.Function)
        {
            case SubqueryFn.All when subquery.Predicate is null:
                throw Reject("All over a collection requires a predicate.");

            case SubqueryFn.Any or SubqueryFn.All or SubqueryFn.Count:
                if (subquery.Selector is not null)
                {
                    throw Reject($"'{subquery.Function}' over a collection does not take a selector.");
                }

                break;

            case SubqueryFn.Sum or SubqueryFn.Average or SubqueryFn.Min or SubqueryFn.Max:
                if (subquery.Selector is null)
                {
                    throw Reject($"'{subquery.Function}' over a collection requires a selector.");
                }

                break;

            default:
                throw Reject($"Unsupported subquery function '{subquery.Function}'.");
        }

        if (subquery.Predicate is { } predicate)
        {
            EnsureNoNestedSubquery(predicate);
            ValidateExpr(predicate, target, depth + 1);
        }

        if (subquery.Selector is { } selector)
        {
            EnsureNoNestedSubquery(selector);
            ValidateScalar(selector, target, "Subquery selector");
        }
    }

    // A subquery costs a correlated query per row; one inside another multiplies that per element, and
    // the depth limit alone does not bound it meaningfully. One level is the whole allowance.
    static void EnsureNoNestedSubquery(Node node)
    {
        switch (node)
        {
            case SubqueryNode:
                throw Reject("A subquery may not appear inside another subquery.");
            case BinaryNode binary:
                EnsureNoNestedSubquery(binary.Left);
                EnsureNoNestedSubquery(binary.Right);
                break;
            case UnaryNode unary:
                EnsureNoNestedSubquery(unary.Operand);
                break;
            case ConditionalNode conditional:
                EnsureNoNestedSubquery(conditional.Test);
                EnsureNoNestedSubquery(conditional.IfTrue);
                EnsureNoNestedSubquery(conditional.IfFalse);
                break;
            case CallNode call:
                EnsureNoNestedSubquery(call.Target);
                foreach (var argument in call.Arguments)
                {
                    EnsureNoNestedSubquery(argument);
                }

                break;
        }
    }

    static (int Min, int Max) Arity(KnownFunction function) =>
        function switch
        {
            KnownFunction.StringToLower or
                KnownFunction.StringToUpper or
                KnownFunction.StringIsNullOrEmpty or
                KnownFunction.StringIsNullOrWhiteSpace or
                KnownFunction.StringLength or
                KnownFunction.StringTrim or
                KnownFunction.StringTrimStart or
                KnownFunction.StringTrimEnd or
                KnownFunction.DateYear or
                KnownFunction.DateMonth or
                KnownFunction.DateDay or
                KnownFunction.DateHour or
                KnownFunction.DateMinute or
                KnownFunction.DateSecond or
                KnownFunction.DateDayOfYear or
                KnownFunction.DateDate or
                KnownFunction.MathAbs or
                KnownFunction.MathCeiling or
                KnownFunction.MathFloor => (0, 0),

            KnownFunction.StringContains or
                KnownFunction.StringStartsWith or
                KnownFunction.StringEndsWith or
                KnownFunction.StringIndexOf or
                KnownFunction.DateAddYears or
                KnownFunction.DateAddMonths or
                KnownFunction.DateAddDays or
                KnownFunction.DateAddHours or
                KnownFunction.DateAddMinutes or
                KnownFunction.DateAddSeconds => (1, 1),

            KnownFunction.StringReplace => (2, 2),
            KnownFunction.StringSubstring => (1, 2),
            KnownFunction.MathRound => (0, 1),

            // Bounded by MaxInValues rather than by arity.
            KnownFunction.In => (0, int.MaxValue),

            _ => throw Reject($"Unsupported function '{function}'.")
        };

    Type ResolveNavigation(IReadOnlyList<string> path, Type rootType)
    {
        var member = ResolvePath(path, rootType, requireScalar: false, "Navigation");
        if (member.Kind == MemberKind.Navigation)
        {
            return member.Type;
        }

        throw Reject($"'{string.Join('.', path)}' is not a navigation property.");
    }

    Member ResolvePath(IReadOnlyList<string> path, Type rootType, bool requireScalar, string what)
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
        Member? member = null;

        for (var i = 0; i < path.Count; i++)
        {
            if (!schema.TryGetType(currentType, out var meta))
            {
                throw Reject($"Type '{currentType.Name}' is not queryable.");
            }

            if (!meta.TryGetMember(path[i], out member))
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

                // Unwrap Nullable<T> so an optional struct complex member resolves to its underlying type.
                currentType = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
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

    // Distinct is applied to the projected rows, so only the projection it deduplicates and a terminal
    // may follow it. Anything that reads row members again would be describing the pre-Distinct rows and
    // silently mean something else. Paging is excluded for a different reason: an ordering cannot
    // survive a Distinct, so Skip/Take over one would be slicing an undefined order.
    static void EnsureNotDistinct(bool sawDistinct, string op)
    {
        if (sawDistinct)
        {
            throw Reject($"{op} is not allowed after Distinct.");
        }
    }

    /// <summary>
    /// A terminal that folds deduplicated rows to a scalar has to fold a single projected value:
    /// <c>COUNT(DISTINCT x)</c> is one column by definition, and a provider given a whole row to
    /// deduplicate and count has no equivalent to fall back on. Enumerating a Distinct query is
    /// unrestricted; only the folding terminals are.
    /// </summary>
    static void EnsureFoldableDistinct(bool sawDistinct, bool sawGroupBy, Projection? projection, string op)
    {
        if (!sawDistinct)
        {
            return;
        }

        if (sawGroupBy)
        {
            throw Reject($"{op} over a Distinct query is not supported after GroupBy.");
        }

        if (projection is not { Members: [{ Value: NodeValue { Node: MemberNode } }] })
        {
            throw Reject($"{op} over a Distinct query requires the Select to project exactly one member.");
        }
    }

    static void EnsureNonNegative(int count, string op)
    {
        if (count < 0)
        {
            throw Reject($"{op} count must be non-negative.");
        }
    }

    static ScryValidationException Reject(string message) =>
        new(message);
}
