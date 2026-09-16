/// <summary>
/// Counts what a request asks for across the whole of it — every expression node, and every
/// correlated subquery — and refuses one over <see cref="ScryOptions.MaxExpressionNodes"/> or
/// <see cref="ScryOptions.MaxCorrelatedSubqueries"/> before the validator walks a single operator.
/// </summary>
/// <remarks>
/// Depth bounds how deeply an expression nests and width how many members a projection names; this
/// bounds how much there is. Counted per request rather than per operator, because a per-operator
/// budget is a budget the pipeline length multiplies. Nothing here resolves a member or checks a
/// shape — that is the validator's — so an over-budget request costs one walk and no lookups.
/// </remarks>
static class RequestBudget
{
    public static void Check(QueryRequest request, ScryOptions options)
    {
        var measured = Measure(request);

        if (measured.Nodes > options.MaxExpressionNodes)
        {
            throw new ScryValidationException(
                $"The request carries {measured.Nodes} expression nodes, more than the maximum of {options.MaxExpressionNodes}.");
        }

        if (measured.Subqueries > options.MaxCorrelatedSubqueries)
        {
            throw new ScryValidationException(
                $"The request carries {measured.Subqueries} correlated subqueries, more than the maximum of {options.MaxCorrelatedSubqueries}.");
        }
    }

    /// <summary>
    /// The same walk, without the refusal, and carrying the three counts the validator makes as it
    /// goes rather than up front — for <see cref="LimitWatch" /> to report what an accepted request
    /// used. Separate walks: this one runs after a query settles and only where something is
    /// watching, so the gate above stays the cheap thing it is.
    /// </summary>
    public static Measurement Measure(QueryRequest request)
    {
        var counter = new Counter();
        counter.Pipeline(request.Pipeline);
        return new(
            counter.Nodes,
            counter.Subqueries,
            counter.InValues,
            counter.NavigationDepth,
            counter.ProjectionMembers);
    }

    /// <summary>
    /// What one request used, against the limits it was measured for. Each mirrors a rule the
    /// validator applies as it walks — see the comments on <see cref="Counter" /> for which.
    /// </summary>
    public readonly record struct Measurement(
        int Nodes,
        int Subqueries,
        int InValues,
        int NavigationDepth,
        int ProjectionMembers);

    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    sealed class Counter
    {
        public int Nodes;
        public int Subqueries;

        // The largest argument count of an In call, which is what MaxInValues bounds: the validator
        // holds each set to it rather than their sum.
        public int InValues;

        // The longest member path, and the deepest projection nesting. The validator measures both
        // against MaxNavigationDepth, so the larger of the two is what came closest to it. Every wire
        // type carrying a path reaches ResolvePath, so every one of them is counted here.
        public int NavigationDepth;

        // The most members one projection named, counted across its nesting the way the validator
        // counts them, and a join's projected member count.
        public int ProjectionMembers;

        public void Pipeline(IReadOnlyList<QueryOp> pipeline)
        {
            foreach (var op in pipeline)
            {
                Operator(op);
            }
        }

        void Operator(QueryOp op)
        {
            switch (op)
            {
                case WhereOp where:
                    Node(where.Predicate);
                    break;
                case OrderByOp orderBy:
                    Node(orderBy.Key);
                    break;
                case ThenByOp thenBy:
                    Node(thenBy.Key);
                    break;
                case SelectOp select:
                    TopProjection(select.Projection);
                    break;
                case SelectManyOp flatten:
                    Track(ref NavigationDepth, flatten.Path.Count);
                    break;
                case GroupByOp groupBy:
                    Each(groupBy.Keys);
                    break;
                case JoinOp join:
                    Node(join.OuterKey);
                    Node(join.InnerKey);
                    Node(join.InnerPredicate);
                    if (join.InnerOps is { } innerOps)
                    {
                        Pipeline(innerOps);
                    }

                    Track(ref ProjectionMembers, join.Result.Count);
                    foreach (var member in join.Result)
                    {
                        Track(ref NavigationDepth, member.Path.Count);
                        Node(member.Aggregate);
                    }

                    break;
                case SetOp set:
                    Node(set.Predicate);
                    if (set.OperandOps is { } operandOps)
                    {
                        Pipeline(operandOps);
                    }

                    TopProjection(set.Projection);
                    break;
                case CountOp count:
                    Node(count.Predicate);
                    break;
                case LongCountOp longCount:
                    Node(longCount.Predicate);
                    break;
                case AnyOp any:
                    Node(any.Predicate);
                    break;
                case AllOp all:
                    Node(all.Predicate);
                    break;
                case FirstOp first:
                    Node(first.Predicate);
                    break;
                case SingleOp single:
                    Node(single.Predicate);
                    break;
                case LastOp last:
                    Node(last.Predicate);
                    break;
                case AggregateOp aggregate:
                    Node(aggregate.Selector);
                    break;
            }
        }

        // A projection the pipeline names, which is where the validator starts both of its counts: a
        // nesting depth of zero, and a member count that then runs across every level of it.
        void TopProjection(Projection projection)
        {
            var members = 0;
            Projection(projection, depth: 0, ref members);
            Track(ref ProjectionMembers, members);
        }

        void Projection(Projection projection, int depth, ref int members)
        {
            Track(ref NavigationDepth, depth);

            foreach (var member in projection.Members)
            {
                members++;
                switch (member.Value)
                {
                    case NodeValue value:
                        Node(value.Node);
                        break;
                    case NestedValue nested:
                        Track(ref NavigationDepth, nested.Path.Count);
                        Projection(nested.Projection, depth + 1, ref members);
                        break;
                }
            }
        }

        void Each(IReadOnlyList<Node> nodes)
        {
            foreach (var node in nodes)
            {
                Node(node);
            }
        }

        // ReSharper disable TailRecursiveCall
        // Bounded by the JSON reader's own depth limit, which a request has already passed.
        void Node(Node? node)
        {
            if (node is null)
            {
                return;
            }

            Nodes++;
            switch (node)
            {
                case MemberNode member:
                    Track(ref NavigationDepth, member.Path.Count);
                    break;
                case BinaryNode binary:
                    Node(binary.Left);
                    Node(binary.Right);
                    break;
                case UnaryNode unary:
                    Node(unary.Operand);
                    break;
                case CallNode call:
                    if (call.Function == KnownFunction.In)
                    {
                        Track(ref InValues, call.Arguments.Count);
                    }

                    Node(call.Target);
                    Each(call.Arguments);
                    break;
                case ConditionalNode conditional:
                    Node(conditional.Test);
                    Node(conditional.IfTrue);
                    Node(conditional.IfFalse);
                    break;
                case CollateNode collate:
                    Node(collate.Target);
                    break;
                case CompositeKeyNode composite:
                    Each(composite.Parts);
                    break;
                case AggregateNode aggregate:
                    Node(aggregate.Selector);
                    Node(aggregate.Predicate);
                    break;
                case SubqueryNode subquery:
                    Subqueries++;
                    Track(ref NavigationDepth, subquery.Path.Count);
                    Node(subquery.Predicate);
                    Node(subquery.Selector);
                    break;
                case InSourceNode inSource:
                    Subqueries++;
                    Node(inSource.Value);
                    Node(inSource.Selector);
                    Node(inSource.Predicate);
                    break;
            }
        }
        // ReSharper restore TailRecursiveCall

        static void Track(ref int current, int value)
        {
            if (value > current)
            {
                current = value;
            }
        }
    }
}
