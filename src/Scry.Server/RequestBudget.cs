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
        var counter = new Counter();
        counter.Pipeline(request.Pipeline);

        if (counter.Nodes > options.MaxExpressionNodes)
        {
            throw new ScryValidationException(
                $"The request carries {counter.Nodes} expression nodes, more than the maximum of {options.MaxExpressionNodes}.");
        }

        if (counter.Subqueries > options.MaxCorrelatedSubqueries)
        {
            throw new ScryValidationException(
                $"The request carries {counter.Subqueries} correlated subqueries, more than the maximum of {options.MaxCorrelatedSubqueries}.");
        }
    }

    sealed class Counter
    {
        public int Nodes;
        public int Subqueries;

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
                    Projection(select.Projection);
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

                    foreach (var member in join.Result)
                    {
                        Node(member.Aggregate);
                    }

                    break;
                case SetOp set:
                    Node(set.Predicate);
                    if (set.OperandOps is { } operandOps)
                    {
                        Pipeline(operandOps);
                    }

                    Projection(set.Projection);
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

        void Projection(Projection projection)
        {
            foreach (var member in projection.Members)
            {
                switch (member.Value)
                {
                    case NodeValue value:
                        Node(value.Node);
                        break;
                    case NestedValue nested:
                        Projection(nested.Projection);
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
                case BinaryNode binary:
                    Node(binary.Left);
                    Node(binary.Right);
                    break;
                case UnaryNode unary:
                    Node(unary.Operand);
                    break;
                case CallNode call:
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
    }
}
