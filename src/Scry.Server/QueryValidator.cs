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
        IReadOnlyList<Node>? groupKeys = null;
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

                    // A narrowing is a predicate on the discriminator, and is hoisted out of a right
                    // join's outer side like any other — and the derived source's own policies, which
                    // the executor applies after narrowing, would be hoisted with it.
                    sawOuterFilter = true;
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


                    foreach (var key in groupBy.Keys)
                    {
                        ValidateScalar(key, rootType, "GroupBy key");
                    }

                    groupKeys = groupBy.Keys;
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

                    if (aggregate.Function == AggregateFn.Join)
                    {
                        throw Reject("Join folds a group's text and has no terminal form.");
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
        IReadOnlyList<Node>? groupKeys,
        int depth)
    {
        var members = 0;
        ValidateProjection(projection, rootType, grouped, groupKeys, depth, ref members);
    }

    // The member count runs across every level of nesting: a nested object is one name that carries
    // several members, and each of them is a column the query returns.
    void ValidateProjection(
        Projection projection,
        Type rootType,
        bool grouped,
        IReadOnlyList<Node>? groupKeys,
        int depth,
        ref int members)
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
            if (++members > options.MaxProjectionMembers)
            {
                throw Reject($"The projection exceeds the maximum of {options.MaxProjectionMembers} members.");
            }

            switch (member.Value)
            {
                case NodeValue {Node: AggregateNode aggregate}:
                    if (!grouped)
                    {
                        throw Reject("Aggregates are only allowed in a Select following GroupBy.");
                    }

                    ValidateAggregateShape(aggregate);
                    if (aggregate.Selector is { } selector)
                    {
                        ValidateScalar(selector, rootType, "Aggregate selector");
                    }

                    if (aggregate.Predicate is { } filtered)
                    {
                        ValidatePredicate(filtered, rootType);
                    }

                    break;

                case NodeValue {Node: MemberNode memberNode}:
                    if (grouped)
                    {
                        if (groupKeys is null ||
                            !groupKeys.OfType<MemberNode>().Any(_ => PathEquals(_.Path, memberNode.Path)))
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
                    ValidateProjection(nested.Projection, target, grouped: false, groupKeys: null, depth + 1, ref members);
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
    // A group key read by position rather than by path — the form a computed key takes, since it has
    // no path to name it by. Only meaningful where a grouping is in scope, and only for a key the
    // query actually has.
    static void EnsureGroupKeyInRange(GroupKeyNode key, IReadOnlyList<Node>? groupKeys)
    {
        if (groupKeys is null)
        {
            throw Reject("A group key can only be read in the Select or Where that follows a GroupBy.");
        }

        if (key.Index < 0 ||
            key.Index >= groupKeys.Count)
        {
            throw Reject($"Group key {key.Index} is out of range; the query grouped by {groupKeys.Count}.");
        }
    }

    void ValidateHaving(Node node, Type elementType, IReadOnlyList<Node>? groupKeys, int depth)
    {
        while (true)
        {
            if (depth > options.MaxExpressionDepth)
            {
                throw Reject("Expression nesting is too deep.");
            }

            switch (node)
            {
                case GroupKeyNode key:
                    EnsureGroupKeyInRange(key, groupKeys);
                    break;

                case MemberNode member:
                    if (groupKeys is null ||
                        !groupKeys.OfType<MemberNode>().Any(_ => PathEquals(_.Path, member.Path)))
                    {
                        throw Reject("A predicate after GroupBy may only reference the group key or aggregates.");
                    }

                    break;

                case AggregateNode aggregate:
                    ValidateAggregateShape(aggregate);
                    if (aggregate.Selector is { } selector)
                    {
                        ValidateScalar(selector, elementType, "Aggregate selector");
                    }
                    else if (aggregate.Function != AggregateFn.Count)
                    {
                        throw Reject($"Aggregate '{aggregate.Function}' requires a selector.");
                    }

                    if (aggregate.Predicate is { } filtered)
                    {
                        ValidatePredicate(filtered, elementType);
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
                    EnsureCallShape(call);
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

    // A bare member is resolved once, under the caller's own label, rather than by ValidateExpr and
    // then again for the label's sake.
    void ValidateScalar(Node node, Type elementType, string what, int depth = 0) =>
        ValidateExpr(node, elementType, depth, what);

    void ValidateExpr(Node node, Type elementType, int depth, string what = "Member")
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
                    if (Schema.IsScalar(elementType))
                    {
                        // Reached inside a subquery over a collection of values, where the row is the
                        // value itself. ResolvePath would report the element type as un-queryable,
                        // which is true but says nothing about what to write instead.
                        throw Reject(
                            $"'{string.Join('.', member.Path)}' cannot be read here: the collection holds values, not rows, so its element has no members. Read the element itself.");
                    }

                    ResolvePath(member.Path, elementType, requireScalar: true, what);
                    break;

                case ElementNode:
                    // The row itself, which is only a value a query can read when it is a scalar —
                    // inside a subquery over a collection of values. Anywhere else it would name a
                    // whole entity.
                    if (!Schema.IsScalar(elementType))
                    {
                        throw Reject("An element can only be read inside a subquery over a collection of values.");
                    }

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

                case GroupKeyNode:
                    throw Reject("A group key can only be read in the Select or Where that follows a GroupBy.");

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
        EnsureCallShape(call);
        ValidateExpr(call.Target, elementType, depth + 1);
        foreach (var argument in call.Arguments)
        {
            ValidateExpr(argument, elementType, depth + 1);
        }
    }

    // What a call is held to wherever it appears — over a row or over a group, which differ only in
    // what its target and arguments may read. Kept in one place so the set-membership cap and the
    // constants-only rule cannot be enforced on one vocabulary and forgotten on the other.
    void EnsureCallShape(CallNode call)
    {
        var (min, max) = Arity(call.Function);
        var count = call.Arguments.Count;
        if (count < min ||
            count > max)
        {
            throw Reject($"Function '{call.Function}' does not take {count} argument(s).");
        }

        if (call.Function != KnownFunction.In)
        {
            return;
        }

        if (count > options.MaxInValues)
        {
            throw Reject($"A Contains set of {count} values exceeds the maximum of {options.MaxInValues}.");
        }

        if (call.Arguments.Any(_ => _ is not ConstNode))
        {
            throw Reject("Every value in a Contains set must be a constant.");
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
                    $"Both sides of a set operation must project the same members, but '{projection.Members[i].Name}' and '{set.Projection.Members[i].Name}' differ.");
            }
        }

        if (set.OperandOps is { } operandOps)
        {
            if (set.Predicate is not null)
            {
                throw Reject("A set operand carries its filter as a predicate or as operand ops, never both.");
            }

            ValidateSideOps(operandOps, other.ClrType, "a set operand");
        }
        else if (set.Predicate is { } predicate)
        {
            ValidatePredicate(predicate, other.ClrType);
        }
    }

    /// <summary>
    /// The pipeline a join's inner side or a set operand may carry — filters first, then an ordering
    /// that exists only to bound the paging after it: <c>Where* [OrderBy ThenBy* (Skip [Take] |
    /// Take)]</c>. An unbounded ordering would be discarded inside a subquery, and unordered paging
    /// would slice rows in no defined order, so each requires the other.
    /// </summary>
    void ValidateSideOps(IReadOnlyList<QueryOp> ops, Type elementType, string side)
    {
        if (ops.Count == 0)
        {
            throw Reject($"Empty ops on {side} — omit them instead.");
        }

        // A pipeline of its own, so it is held to the same length as the one that carries it.
        if (ops.Count > options.MaxPipelineLength)
        {
            throw Reject($"The pipeline on {side} exceeds the maximum length of {options.MaxPipelineLength}.");
        }

        var stage = 0;
        foreach (var op in ops)
        {
            switch (op)
            {
                case WhereOp where when stage == 0:
                    ValidatePredicate(where.Predicate, elementType);
                    break;

                case OrderByOp orderBy when stage == 0:
                    ValidateScalar(orderBy.Key, elementType, "OrderBy key");
                    stage = 1;
                    break;

                case ThenByOp thenBy when stage == 1:
                    ValidateScalar(thenBy.Key, elementType, "ThenBy key");
                    break;

                case SkipOp skip when stage == 1:
                    if (skip.Count < 0)
                    {
                        throw Reject("Skip cannot be negative.");
                    }

                    stage = 2;
                    break;

                case TakeOp take when stage is 1 or 2:
                    if (take.Count < 1)
                    {
                        throw Reject("Take must be at least one.");
                    }

                    if (take.Count > options.MaxPageSize)
                    {
                        throw Reject($"Take of {take.Count} on {side} exceeds the maximum page size of {options.MaxPageSize}.");
                    }

                    stage = 3;
                    break;

                default:
                    throw Reject(
                        $"'{op.GetType().Name}' is not allowed on {side} — only filters, and an ordering bounded by Skip or Take, in that order.");
            }
        }

        if (stage == 1)
        {
            throw Reject($"An ordering on {side} must be bounded by Skip or Take — unbounded, a subquery discards it.");
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
                "RightJoin cannot narrow its outer side — remove the Where, OfType, Skip, or Take before it, or swap the sides and use LeftJoin.");
        }

        if (root.Policies.Count > 0)
        {
            throw Reject(
                $"Source '{root.Name}' carries a row policy, so it cannot be the outer side of a RightJoin — swap the sides and use LeftJoin.");
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

        var element = member.Element ??
                      throw Reject($"'{string.Join('.', flatten.Path)}' is not a collection.");

        // A collection of values can be aggregated but not flattened: the rows it would produce are
        // bare values, and every operator after the flatten — the projection above all — names members
        // of the row it reads. Rejected here rather than left to fail as an un-queryable element type.
        if (Schema.IsScalar(element))
        {
            throw Reject($"'{string.Join('.', flatten.Path)}' holds values rather than rows, so it cannot be flattened. Aggregate it instead.");
        }

        return element;
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

        ValidateJoinKeys(join, outerType, innerType);

        if (join.InnerOps is { } innerOps)
        {
            if (join.InnerPredicate is not null)
            {
                throw Reject("A join carries its inner filter as a predicate or as inner ops, never both.");
            }

            ValidateSideOps(innerOps, innerType, "a join's inner side");
        }
        else if (join.InnerPredicate is { } predicate)
        {
            ValidatePredicate(predicate, innerType);
        }

        if (join.Result.Count == 0)
        {
            throw Reject("A join must project at least one member.");
        }

        if (join.Result.Count > options.MaxProjectionMembers)
        {
            throw Reject($"A join projecting {join.Result.Count} members exceeds the maximum of {options.MaxProjectionMembers}.");
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
                        $"Join projection member '{member.Name}' aggregates, which only the inner side of a GroupJoin may do.");
                }

                // The text aggregate folds a grouped query's rows; over a joined group it is untried
                // territory for the provider, so it is refused rather than shipped as a fault.
                if (aggregate.Function == AggregateFn.Join)
                {
                    throw Reject("Join is not supported over a joined group — aggregate it to a number instead.");
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
                    $"Join projection member '{member.Name}' reads the inner side of a GroupJoin directly. The inner side is a group — aggregate it, or use Join.");
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

        var target = member.Element ??
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
            EnsureUncorrelated(predicate, Host.Subquery);
            ValidateExpr(predicate, target, depth + 1);
        }

        if (subquery.Selector is { } selector)
        {
            EnsureUncorrelated(selector, Host.Subquery);
            ValidateScalar(selector, target, "Subquery selector", depth + 1);
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

        // The value reads the row being tested, so a subquery there costs what it would anywhere else
        // on that row and is allowed; the subquery's own expressions are guarded when it is validated.
        EnsureUncorrelated(inSource.Value, Host.Membership, refuseSubquery: false);
        ValidateScalar(inSource.Value, elementType, "Membership value", depth + 1);

        EnsureUncorrelated(inSource.Selector, Host.Membership);
        ValidateScalar(inSource.Selector, inner.ClrType, "Membership selector", depth + 1);

        if (inSource.Predicate is { } predicate)
        {
            EnsureUncorrelated(predicate, Host.Membership);
            ValidateExpr(predicate, inner.ClrType, depth + 1);
        }
    }

    // What a correlated expression hangs off, named in the message that refuses one inside another.
    enum Host
    {
        Subquery,
        Membership
    }

    // A subquery and a membership test each cost a correlated query per row; either inside the other's
    // expressions multiplies that per element, and the depth limit alone does not bound it
    // meaningfully. One level is the whole allowance, whichever of the two it is. Every node kind is
    // walked, so nothing a value can be wrapped in — a collation, a conditional, a call — can hide one.
    static void EnsureUncorrelated(Node node, Host host, bool refuseSubquery = true)
    {
        while (true)
        {
            switch (node)
            {
                case SubqueryNode when refuseSubquery:
                    throw Reject(host == Host.Subquery
                        ? "A subquery may not appear inside another subquery."
                        : "A subquery may not appear inside a membership test against another source.");

                case SubqueryNode:
                    return;

                case InSourceNode:
                    throw Reject(host == Host.Subquery
                        ? "A membership test against another source may not appear inside a subquery."
                        : "A membership test against another source may not appear inside another.");

                case BinaryNode binary:
                    EnsureUncorrelated(binary.Left, host, refuseSubquery);
                    node = binary.Right;
                    continue;

                case UnaryNode unary:
                    node = unary.Operand;
                    continue;

                case ConditionalNode conditional:
                    EnsureUncorrelated(conditional.Test, host, refuseSubquery);
                    EnsureUncorrelated(conditional.IfTrue, host, refuseSubquery);
                    node = conditional.IfFalse;
                    continue;

                case CallNode call:
                    EnsureUncorrelated(call.Target, host, refuseSubquery);
                    foreach (var argument in call.Arguments)
                    {
                        EnsureUncorrelated(argument, host, refuseSubquery);
                    }

                    return;

                case CollateNode collate:
                    node = collate.Target;
                    continue;

                case AggregateNode aggregate:
                    if (aggregate.Selector is { } selector)
                    {
                        EnsureUncorrelated(selector, host, refuseSubquery);
                    }

                    if (aggregate.Predicate is { } predicate)
                    {
                        EnsureUncorrelated(predicate, host, refuseSubquery);
                    }

                    return;

                case CompositeKeyNode composite:
                    foreach (var part in composite.Parts)
                    {
                        EnsureUncorrelated(part, host, refuseSubquery);
                    }

                    return;

                case MemberNode or ConstNode or ElementNode or GroupKeyNode:
                    return;

                default:
                    throw Reject($"Unsupported expression '{node.GetType().Name}'.");
            }
        }
    }

    string? Collation(StringMatch match) =>
        match switch
        {
            StringMatch.CaseSensitive => options.CaseSensitiveCollation,
            StringMatch.CaseInsensitive => options.CaseInsensitiveCollation,
            _ => null
        };

    // A composite key is both sides' business at once: the parts pair positionally, so the sides
    // must agree on how many there are, and each part is then an ordinary scalar against its own
    // side. A part that is itself composite falls to ValidateScalar's default case and is rejected.
    void ValidateJoinKeys(JoinOp join, Type outerType, Type innerType)
    {
        if (join.OuterKey is not CompositeKeyNode &&
            join.InnerKey is not CompositeKeyNode)
        {
            ValidateScalar(join.OuterKey, outerType, "Join key");
            ValidateScalar(join.InnerKey, innerType, "Join key");
            return;
        }

        if (join.OuterKey is not CompositeKeyNode outer ||
            join.InnerKey is not CompositeKeyNode inner)
        {
            throw Reject("A composite join key must be composite on both sides.");
        }

        if (outer.Parts.Count != inner.Parts.Count)
        {
            throw Reject(
                $"A composite join key pairs its parts, but the sides carry {outer.Parts.Count} and {inner.Parts.Count}.");
        }

        if (outer.Parts.Count < 2)
        {
            throw Reject("A composite join key carries at least two parts — a single key needs no composite.");
        }

        if (outer.Parts.Count > DistinctRow.ByArity.Length)
        {
            throw Reject($"A composite join key carries at most {DistinctRow.ByArity.Length} parts.");
        }

        foreach (var part in outer.Parts)
        {
            ValidateScalar(part, outerType, "Join key");
        }

        foreach (var part in inner.Parts)
        {
            ValidateScalar(part, innerType, "Join key");
        }
    }

    // The shape rules an aggregate's own fields carry, wherever it appears: Join is the one that
    // needs a separator, and the only one allowed to carry it.
    static void ValidateAggregateShape(AggregateNode aggregate)
    {
        if (aggregate.Function == AggregateFn.Join)
        {
            if (aggregate.Selector is null)
            {
                throw Reject("Join requires a selector.");
            }

            if (aggregate.Separator is null)
            {
                throw Reject("Join requires a separator.");
            }

            // Join folds every present value, ordered by itself — a filtered or deduplicated variant
            // has no verified translation, so the composed fields stay off it.
            if (aggregate.Predicate is not null ||
                aggregate.Distinct)
            {
                throw Reject("Join folds the whole group — filter the rows before grouping.");
            }
        }
        else if (aggregate.Separator is not null)
        {
            throw Reject($"Aggregate '{aggregate.Function}' does not take a separator.");
        }

        if (aggregate is {Distinct: true, Selector: null})
        {
            throw Reject("A distinct aggregate folds selected values, so it requires a selector.");
        }

        if (aggregate is {Function: AggregateFn.Count, Selector: not null, Distinct: false})
        {
            throw Reject("Count takes a selector only under Distinct — without one, selected values change nothing about a count.");
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
                KnownFunction.DateMillisecond or
                KnownFunction.DateDayOfYear or
                KnownFunction.DateDayOfWeek or
                KnownFunction.DateDate or
                KnownFunction.DateMicrosecond or
                KnownFunction.DateNanosecond or
                KnownFunction.DateDayNumber or
                KnownFunction.DateTimeOfDay or
                KnownFunction.TimeSpanHours or
                KnownFunction.TimeSpanMinutes or
                KnownFunction.TimeSpanSeconds or
                KnownFunction.TimeSpanMilliseconds or
                KnownFunction.TimeSpanMicroseconds or
                KnownFunction.TimeSpanNanoseconds or
                KnownFunction.DateOnlyFromDateTime or
                KnownFunction.TimeOnlyFromDateTime or
                KnownFunction.TimeOnlyFromTimeSpan or
                KnownFunction.UnixSecondsFromOffset or
                KnownFunction.UnixMillisecondsFromOffset or
                KnownFunction.StringFirst or
                KnownFunction.StringLast or
                KnownFunction.BytesLength or
                KnownFunction.MathAbs or
                KnownFunction.MathCeiling or
                KnownFunction.MathFloor or
                KnownFunction.MathTruncate or
                KnownFunction.StringFrom or
                KnownFunction.Int32From or
                KnownFunction.Int64From or
                KnownFunction.DecimalFrom or
                KnownFunction.DoubleFrom or
                KnownFunction.BooleanFrom or
                KnownFunction.ByteFrom or
                KnownFunction.Int16From or
                KnownFunction.SingleFrom or
                KnownFunction.MathSign or
                KnownFunction.MathSqrt or
                KnownFunction.MathExp or
                KnownFunction.MathDegreesToRadians or
                KnownFunction.MathRadiansToDegrees or
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
                KnownFunction.DateAddSeconds or
                KnownFunction.DateAddMilliseconds or
                KnownFunction.EnumHasFlag or
                KnownFunction.DateTimeFromDateAndTime or
                KnownFunction.BytesContains or
                KnownFunction.BytesElementAt or
                KnownFunction.CompareTo => (1, 1),

            KnownFunction.StringReplace => (2, 2),
            KnownFunction.StringConcat => (1, 1),
            KnownFunction.MathPow or
                KnownFunction.MathAtan2 or
                KnownFunction.MathMax or
                KnownFunction.MathMin => (1, 1),

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
            return member.Target;
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

            // Rejected explicitly rather than left to the scalar check below, which several callers
            // skip: no query reads an attachment's value, wherever it is named. The generated client
            // cannot express one either — this is what a hand-built request meets.
            if (member.Kind == MemberKind.Attachment)
            {
                throw Reject($"'{path[i]}' on '{currentType.Name}' is an attachment. Its value is fetched through the attachment endpoint, not read by a query.");
            }

            var isLast = i == path.Count - 1;
            if (!isLast)
            {
                if (member.Kind != MemberKind.Navigation)
                {
                    throw Reject($"Cannot traverse through non-navigation '{path[i]}'.");
                }

                currentType = member.Target;
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

        if (key is not MemberNode {Path: [var name]} ||
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
