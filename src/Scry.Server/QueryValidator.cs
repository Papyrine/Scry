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

        ValidatePipeline(request.Pipeline, source);
        return source;
    }

    void ValidatePipeline(IReadOnlyList<QueryOp> pipeline, ScrySource root)
    {
        var rootType = root.ClrType;
        var sawOrdering = false;
        var sawOuterFilter = false;
        var sawSelectMany = false;
        var sawGroupBy = false;
        var sawSelect = false;
        var sawDistinct = false;
        var sawJoin = false;
        var sawSet = false;
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

            // Combined rows come from two sources, so there is no single root left to read.
            if (sawSet &&
                op is not (CountOp or LongCountOp or AnyOp))
            {
                throw Reject("Only Count, LongCount, or Any may follow a set operation.");
            }

            switch (op)
            {
                case WhereOp where:
                    sawOuterFilter = true;
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

                case OfTypeOp narrowed:
                    EnsureNotGrouped(sawGroupBy, "OfType");
                    EnsureNotProjected(sawSelect, "OfType");
                    EnsureNotDistinct(sawDistinct, "OfType");
                    if (sawJoin || sawSet)
                    {
                        throw Reject("OfType is not allowed after a Join or a set operation.");
                    }

                    // Everything after the narrowing reads the derived type, whose members the base
                    // does not expose. The narrowing itself is validated against the schema, never
                    // against a CLR type named on the wire.
                    rootType = ValidateOfType(narrowed, rootType);
                    break;

                case SelectManyOp flatten:
                    if (sawSelectMany)
                    {
                        throw Reject("Only one SelectMany is allowed.");
                    }

                    EnsureNotGrouped(sawGroupBy, "SelectMany");
                    EnsureNotProjected(sawSelect, "SelectMany");
                    EnsureNotDistinct(sawDistinct, "SelectMany");

                    // Everything after the flatten reads the collection's element, so the root the rest
                    // of the pipeline is validated against changes here.
                    rootType = ValidateSelectMany(flatten, rootType);
                    sawSelectMany = true;

                    // An ordering written before the flatten described the rows it consumed, so it can
                    // no longer be extended and cannot seed a cursor.
                    sawOrdering = false;
                    break;

                case OrderByOp orderBy:
                    EnsureNotGrouped(sawGroupBy, "OrderBy");
                    if (sawDistinct)
                    {
                        // Over a deduplicated query the key names the projected member rather than a
                        // row member — the rows the ordering describes are the deduplicated ones.
                        EnsureDeduplicatedKey(orderBy.Key, projection, "OrderBy");
                    }
                    else
                    {
                        EnsureNotProjected(sawSelect, "OrderBy");
                        ValidateScalar(orderBy.Key, rootType, "OrderBy key");
                    }

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
                    sawOuterFilter = true;
                    EnsurePageableDistinct(sawDistinct, sawOrdering, projection, "Skip");
                    EnsureNonNegative(skip.Count, "Skip");
                    break;

                case TakeOp take:
                    sawOuterFilter = true;
                    EnsurePageableDistinct(sawDistinct, sawOrdering, projection, "Take");
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

                    if (groupBy.Keys.Count == 0)
                    {
                        throw Reject("GroupBy requires at least one key.");
                    }

                    if (groupBy.Keys.Count > DistinctRow.ByArity.Length)
                    {
                        throw Reject(
                            $"GroupBy supports at most {DistinctRow.ByArity.Length} keys.");
                    }

                    // A composite key is addressed one part at a time in the projection, so every part
                    // has to be a member path there is a name to match it back by.
                    if (groupBy.Keys.Count > 1 &&
                        groupBy.Keys.Any(_ => _ is not MemberNode))
                    {
                        throw Reject("Every key of a composite GroupBy must be a member.");
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
                    EnsureUnnarrowedRightJoinOuter(join.Kind, sawOuterFilter, root);
                    ValidateJoin(join, rootType);
                    sawJoin = true;
                    break;

                case SetOp set:
                    if (sawSet)
                    {
                        throw Reject("Only one set operation is allowed.");
                    }

                    EnsureNotGrouped(sawGroupBy, "A set operation");
                    EnsureNotDistinct(sawDistinct, "A set operation");
                    if (sawJoin)
                    {
                        throw Reject("A set operation is not allowed after a Join.");
                    }

                    ValidateSet(set, projection);
                    sawSet = true;
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
                    // Over a group the same vocabulary applies as in a HAVING predicate: the key and
                    // aggregates, composed with the ordinary operators and functions.
                    if (grouped)
                    {
                        ValidateHaving(value.Node, rootType, groupKeys, depth: 0);
                        break;
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
        while (true)
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
                    node = binary.Right;
                    depth += 1;
                    continue;

                case UnaryNode unary:
                    node = unary.Operand;
                    depth += 1;
                    continue;

                case ConditionalNode conditional:
                    ValidateHaving(conditional.Test, elementType, groupKeys, depth + 1);
                    ValidateHaving(conditional.IfTrue, elementType, groupKeys, depth + 1);
                    node = conditional.IfFalse;
                    depth += 1;
                    continue;

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

            break;
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

                case InSourceNode inSource:
                    ValidateInSource(inSource, elementType, depth);
                    break;

                case CollateNode collate:
                    // A server that has configured no collation for the requested sensitivity cannot
                    // answer the question, and must not guess at one.
                    if (Collation(collate.Match) is null)
                    {
                        throw Reject(
                            $"This server has no {collate.Match} collation configured — set ScryOptions.{collate.Match}Collation to enable it.");
                    }

                    node = collate.Target;
                    depth += 1;
                    continue;

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
    /// Validates a set operation. The second source is looked up by name in the same allow-list a
    /// request's own root goes through, and its projection is validated against <i>its</i> type. Both
    /// sides must name the same members: the combined rows are one shape, and a row carries no record
    /// of which side produced it.
    /// </summary>
    void ValidateSet(SetOp set, Projection? projection)
    {
        if (!schema.TryGetSource(set.Root, out var other))
        {
            throw Reject($"Unknown source '{set.Root}'.");
        }

        // The combined rows are materialized as a row with one property per member, which is what lets
        // a provider compare them at all — so the same flat, bounded shape a Distinct needs applies.
        EnsureRowShaped(projection, "A set operation");

        ValidateProjection(set.Projection, other.ClrType, grouped: false, groupKeys: null, depth: 0);
        EnsureRowShaped(set.Projection, "A set operation");

        if (set.Projection.Members.Count != projection!.Members.Count)
        {
            throw Reject("Both sides of a set operation must project the same number of members.");
        }

        for (var i = 0; i < projection.Members.Count; i++)
        {
            if (!string.Equals(projection.Members[i].Name, set.Projection.Members[i].Name, StringComparison.Ordinal))
            {
                throw Reject(
                    $"Both sides of a set operation must project the same members, but '{projection.Members[i].Name}' " +
                    $"and '{set.Projection.Members[i].Name}' differ.");
            }
        }

        if (set.Predicate is { } predicate)
        {
            ValidatePredicate(predicate, other.ClrType);
        }
    }

    // EF hoists a predicate on the outer side of a RightJoin out of the join and into the WHERE of the
    // combined query, which silently turns the right join into an inner one — unmatched inner rows are
    // dropped rather than kept with nulls. Refusing the shape is better than answering it wrongly. The
    // inner side has no such problem: EF keeps it as a subquery, which is why LeftJoin filters its
    // inner source freely. A row policy on the outer source is hoisted the same way, so a policied
    // source cannot be the outer side of a right join at all.
    static void EnsureUnnarrowedRightJoinOuter(JoinKind kind, bool sawOuterFilter, ScrySource root)
    {
        if (kind != JoinKind.Right)
        {
            return;
        }

        if (sawOuterFilter)
        {
            throw Reject(
                "RightJoin cannot narrow its outer side — remove the Where, Skip, or Take before it, " +
                "or swap the sides and use LeftJoin.");
        }

        if (root.PolicyType is not null)
        {
            throw Reject(
                $"Source '{root.Name}' carries a row policy, so it cannot be the outer side of a " +
                "RightJoin — swap the sides and use LeftJoin.");
        }
    }

    /// <summary>
    /// Validates a narrowing to a derived type and returns the type the rest of the pipeline reads.
    /// The name is resolved through the same allow-list a request's own root goes through, so a type
    /// that was not opted in cannot be reached however it is spelled — and it must actually derive
    /// from the type being queried, which is what keeps the narrowing a narrowing.
    /// </summary>
    Type ValidateOfType(OfTypeOp narrowed, Type rootType)
    {
        if (!schema.TryGetSource(narrowed.Type, out var derived))
        {
            throw Reject($"Unknown source '{narrowed.Type}'.");
        }

        var target = derived.ClrType;
        if (target == rootType)
        {
            throw Reject($"OfType to '{narrowed.Type}' does not narrow — it is the type already being queried.");
        }

        if (!rootType.IsAssignableFrom(target))
        {
            throw Reject($"'{narrowed.Type}' does not derive from '{rootType.Name}'.");
        }

        return target;
    }

    /// <summary>
    /// Validates a flatten and returns the element type the rest of the pipeline reads. The element is
    /// allow-listed in its own right; it carries no row policy, because a <c>[QueryableCollection]</c>
    /// of a policied type is already refused at startup.
    /// </summary>
    Type ValidateSelectMany(SelectManyOp flatten, Type rootType)
    {
        var member = ResolvePath(flatten.Path, rootType, requireScalar: false, "SelectMany");
        if (member.Kind != MemberKind.Collection)
        {
            throw Reject($"'{string.Join('.', flatten.Path)}' is not a queryable collection.");
        }

        return Schema.CollectionElement(member.Type) ??
               throw Reject($"'{string.Join('.', flatten.Path)}' is not a collection.");
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

        var grouped = join.Kind == JoinKind.Group;
        var sawAggregate = false;

        foreach (var member in join.Result)
        {
            var side = member.Side switch
            {
                JoinSide.Outer => outerType,
                JoinSide.Inner => innerType,
                _ => throw Reject($"Unsupported join side '{member.Side}'.")
            };

            if (member.Aggregate is { } aggregate)
            {
                if (!grouped ||
                    member.Side != JoinSide.Inner)
                {
                    throw Reject(
                        $"Join projection member '{member.Name}' aggregates, which only the inner side " +
                        "of a GroupJoin may do.");
                }

                if (aggregate.Function != AggregateFn.Count)
                {
                    if (aggregate.Selector is not { } selector)
                    {
                        throw Reject($"Aggregate '{aggregate.Function}' requires a selector.");
                    }

                    // Against the inner side's own allow-list — the two sides never share one.
                    ValidateScalar(selector, innerType, "Aggregate selector");
                }
                else if (aggregate.Selector is not null)
                {
                    throw Reject("Count over a joined group does not take a selector.");
                }

                sawAggregate = true;
                continue;
            }

            // The inner side of a group join is a group of rows, so there is no single row to read a
            // member off — only an aggregate folds it to something a response row can hold.
            if (grouped &&
                member.Side == JoinSide.Inner)
            {
                throw Reject(
                    $"Join projection member '{member.Name}' reads the inner side of a GroupJoin " +
                    "directly. The inner side is a group — aggregate it, or use Join.");
            }

            ResolvePath(member.Path, side, requireScalar: true, "Join projection member");
        }

        // Without one, a group join is an outer query with the inner side joined and discarded —
        // which is just the outer query, at the cost of the join.
        if (grouped &&
            !sawAggregate)
        {
            throw Reject("A GroupJoin must aggregate its inner side at least once.");
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
        while (true)
        {
            switch (node)
            {
                case SubqueryNode:
                    throw Reject("A subquery may not appear inside another subquery.");
                case BinaryNode binary:
                    EnsureNoNestedSubquery(binary.Left);
                    node = binary.Right;
                    continue;
                case UnaryNode unary:
                    node = unary.Operand;
                    continue;
                case ConditionalNode conditional:
                    EnsureNoNestedSubquery(conditional.Test);
                    EnsureNoNestedSubquery(conditional.IfTrue);
                    node = conditional.IfFalse;
                    continue;
                case CallNode call:
                    EnsureNoNestedSubquery(call.Target);
                    foreach (var argument in call.Arguments)
                    {
                        EnsureNoNestedSubquery(argument);
                    }

                    break;
            }

            break;
        }
    }

    /// <summary>
    /// Validates membership of a set drawn from another source. The named source goes through the same
    /// allow-list a request's own root does, and the selector and filter are validated against <i>its</i>
    /// type — the two sides never share an allow-list.
    /// </summary>
    void ValidateInSource(InSourceNode inSource, Type elementType, int depth)
    {
        if (!schema.TryGetSource(inSource.Root, out var inner))
        {
            throw Reject($"Unknown source '{inSource.Root}'.");
        }

        ValidateScalar(inSource.Value, elementType, "Membership value");
        ValidateScalar(inSource.Selector, inner.ClrType, "Membership selector");
        EnsureNoNestedSource(inSource.Selector);

        if (inSource.Predicate is { } predicate)
        {
            EnsureNoNestedSource(predicate);
            ValidateExpr(predicate, inner.ClrType, depth + 1);
        }
    }

    // One level only. A membership test costs a subquery; nesting them multiplies that per row, and
    // the depth limit alone does not bound it meaningfully — the same reasoning as for subqueries.
    static void EnsureNoNestedSource(Node node)
    {
        switch (node)
        {
            case InSourceNode:
                throw Reject("A membership test against another source may not appear inside another.");
            case BinaryNode binary:
                EnsureNoNestedSource(binary.Left);
                EnsureNoNestedSource(binary.Right);
                break;
            case UnaryNode unary:
                EnsureNoNestedSource(unary.Operand);
                break;
            case ConditionalNode conditional:
                EnsureNoNestedSource(conditional.Test);
                EnsureNoNestedSource(conditional.IfTrue);
                EnsureNoNestedSource(conditional.IfFalse);
                break;
            case CallNode call:
                EnsureNoNestedSource(call.Target);
                foreach (var argument in call.Arguments)
                {
                    EnsureNoNestedSource(argument);
                }

                break;
        }
    }

    string? Collation(StringMatch match) =>
        match switch
        {
            StringMatch.CaseSensitive => options.CaseSensitiveCollation,
            StringMatch.CaseInsensitive => options.CaseInsensitiveCollation,
            _ => null
        };

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
                KnownFunction.DateMillisecond or
                KnownFunction.DateDayOfYear or
                KnownFunction.DateDate or
                KnownFunction.MathAbs or
                KnownFunction.MathCeiling or
                KnownFunction.MathFloor or
                KnownFunction.MathTruncate or
                KnownFunction.MathSqrt or
                KnownFunction.MathExp or
                KnownFunction.MathLog10 or
                KnownFunction.MathSin or
                KnownFunction.MathCos or
                KnownFunction.MathTan or
                KnownFunction.MathAsin or
                KnownFunction.MathAcos or
                KnownFunction.MathAtan => (0, 0),

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
            KnownFunction.MathPow or
                KnownFunction.MathAtan2 => (1, 1),

            // Math.Log is the natural logarithm with no argument and a logarithm to a base with one.
            KnownFunction.MathLog => (0, 1),
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

        EnsureRowShaped(projection, op);
    }

    /// <summary>
    /// An ordering over a deduplicated query names one of the projected members, not a row member —
    /// the rows it describes are the deduplicated ones. Confined to a single-member projection for the
    /// same reason folding one is: the shaped <c>object[]</c> row has no ordering of its own, so only a
    /// projection of one value can be ordered as the typed value it is.
    /// </summary>
    static void EnsureDeduplicatedKey(Node key, Projection? projection, string op)
    {
        EnsureRowShaped(projection, op);

        if (key is not MemberNode { Path: [var name] } ||
            projection!.Members.All(_ => !string.Equals(_.Name, name, StringComparison.Ordinal)))
        {
            throw Reject($"{op} over a Distinct query may only order by one of its projected members.");
        }
    }

    // Paging a deduplicated query slices an order, so it needs one — otherwise it would be slicing an
    // order the deduplication never defined.
    static void EnsurePageableDistinct(bool sawDistinct, bool sawOrdering, Projection? projection, string op)
    {
        if (!sawDistinct)
        {
            return;
        }

        if (!sawOrdering)
        {
            throw Reject($"{op} over a Distinct query requires an OrderBy — a deduplication does not preserve one.");
        }

        EnsureRowShaped(projection, op);
    }

    /// <summary>
    /// Ordering, paging and folding a deduplicated query all materialize it as a row with one property
    /// per projected member, so the projection has to be a flat list of them: a nested object would
    /// contribute several leaves under one name, leaving nothing for an ordering to name.
    /// </summary>
    static void EnsureRowShaped(Projection? projection, string op)
    {
        if (projection is null ||
            projection.Members.Count == 0)
        {
            throw Reject($"{op} over a Distinct query requires a Select.");
        }

        if (projection.Members.Any(_ => _.Value is not NodeValue))
        {
            throw Reject($"{op} over a Distinct query does not support a nested projection member.");
        }

        if (projection.Members.Count > DistinctRow.ByArity.Length)
        {
            throw Reject(
                $"{op} over a Distinct query is limited to {DistinctRow.ByArity.Length} projected members.");
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
