/// <summary>
/// What a finished request does with the members its model marks <c>[Sensitive]</c>: whether it
/// compares one against a constant, and whether it returns one.
/// </summary>
/// <remarks>
/// The two are separate because they are separate hazards. A constant compared against a sensitive
/// member is written into the access log of every hop a URL passes, so such a query travels as a body.
/// A sensitive member in the result is written to the caller's disk if the response is storable, so
/// such a response is sent <c>no-store</c>. A query can do one, both, or neither.
/// </remarks>
readonly record struct SensitiveUse(bool InConstant, bool InProjection);

/// <summary>
/// Reads <see cref="SensitiveUse"/> off a <see cref="QueryRequest"/> — the finished wire AST, not the
/// expression tree it came from.
/// </summary>
/// <remarks>
/// <para>
/// The AST is the only place the answer is reliable. A client's translator has four ways to build a
/// request that never pass through one point — a terminal's predicate, a join's inner side, a set
/// operand, and the default projection appended after translation — so a check placed there answers
/// for some queries and silently misses others. By the time a request exists, all of that is nodes.
/// </para>
/// <para>
/// Compiled into the client and the server both. Each supplies its own resolver — the client reads the
/// attributes the generator emitted, the server reads its schema — but the walk has to be the same
/// one, because the client's answer decides how a query is sent and the server's decides whether that
/// was allowed. Two walks that disagreed would refuse queries a client had no way to know about.
/// </para>
/// </remarks>
static class SensitiveWalk
{
    /// <summary>
    /// Inspects a request. <paramref name="sensitive"/> is asked whether a member path off a source is
    /// marked; an <b>empty path</b> asks whether the source has anything marked at all, which is what
    /// a query with no <c>Select</c> returns. A <b>null source</b> means the walk lost track of which
    /// row a path is read off — after a flatten, a narrow, a group, or a join — and the resolver is
    /// expected to answer conservatively rather than guess.
    /// </summary>
    public static SensitiveUse Inspect(QueryRequest request, Func<string?, IReadOnlyList<string>, bool> sensitive)
    {
        var walk = new Walk(sensitive);
        walk.Pipeline(request.Pipeline, request.Root);
        return new(walk.InConstant, walk.InProjection);
    }

    sealed class Walk(Func<string?, IReadOnlyList<string>, bool> sensitive)
    {
        public bool InConstant;
        public bool InProjection;

        public void Pipeline(IReadOnlyList<QueryOp> pipeline, string? root)
        {
            // A query that never says what to return is answered with the source's own members, every
            // one of them — so a source with anything marked returns it whether the query named it or
            // not. Terminals that fold rows to a scalar return nothing of the row, and are excluded.
            var projects = !pipeline.Any(_ => _ is SelectOp or JoinOp or SetOp or GroupByOp) &&
                           !pipeline.Any(_ => _ is CountOp or LongCountOp or AnyOp or AllOp or AggregateOp);
            if (projects && sensitive(root, []))
            {
                InProjection = true;
            }

            foreach (var op in pipeline)
            {
                root = Operator(op, root);
            }
        }

        // Returns the source later operators read off, which most operators leave alone. The ones that
        // reshape the row hand back null: the walk cannot say what a path means after that, and the
        // resolver is told so rather than being asked about the wrong source.
        string? Operator(QueryOp op, string? root)
        {
            switch (op)
            {
                case WhereOp where:
                    Expression(where.Predicate, root);
                    return root;
                case OrderByOp orderBy:
                    Expression(orderBy.Key, root);
                    return root;
                case ThenByOp thenBy:
                    Expression(thenBy.Key, root);
                    return root;
                case SelectOp select:
                    Projection(select.Projection, root);
                    return root;
                case CountOp {Predicate: { } predicate}:
                    Expression(predicate, root);
                    return root;
                case LongCountOp {Predicate: { } predicate}:
                    Expression(predicate, root);
                    return root;
                case AnyOp {Predicate: { } predicate}:
                    Expression(predicate, root);
                    return root;
                case AllOp all:
                    Expression(all.Predicate, root);
                    return root;
                case FirstOp {Predicate: { } predicate}:
                    Expression(predicate, root);
                    return root;
                case SingleOp {Predicate: { } predicate}:
                    Expression(predicate, root);
                    return root;
                case LastOp {Predicate: { } predicate}:
                    Expression(predicate, root);
                    return root;
                case AggregateOp aggregate:
                    Expression(aggregate.Selector, root);
                    return root;
                case GroupByOp groupBy:
                    foreach (var key in groupBy.Keys)
                    {
                        Expression(key, root);
                    }

                    // The rows are groups now, and a later path reads a key or an aggregate rather than
                    // a member of the source.
                    return null;
                case OfTypeOp ofType:
                    // Narrowing keeps the row and changes its type, which is a source name of its own.
                    return ofType.Type;
                case SelectManyOp selectMany:
                    Member(selectMany.Path, root);
                    return null;
                case JoinOp join:
                    Expression(join.OuterKey, root);
                    Expression(join.InnerKey, join.Root);
                    if (join.InnerPredicate is { } inner)
                    {
                        Expression(inner, join.Root);
                    }

                    if (join.InnerOps is { } innerOps)
                    {
                        Pipeline(innerOps, join.Root);
                    }

                    foreach (var member in join.Result)
                    {
                        var side = member.Side == JoinSide.Inner ? join.Root : root;
                        Member(member.Path, side, projected: true);
                        if (member.Aggregate is { } folded)
                        {
                            Expression(folded, join.Root);
                        }
                    }

                    return null;
                case SetOp set:
                    if (set.Predicate is { } filter)
                    {
                        Expression(filter, set.Root);
                    }

                    if (set.OperandOps is { } operandOps)
                    {
                        Pipeline(operandOps, set.Root);
                    }

                    // The operand's own rows, projected to match the pipeline's shape.
                    Projection(set.Projection, set.Root);
                    return null;
                default:
                    // Skip, Take, Distinct, Reverse, Page, and the bare terminals carry no member path
                    // and no constant of a member's own, so there is nothing here to read.
                    return root;
            }
        }

        void Projection(Projection projection, string? root)
        {
            foreach (var member in projection.Members)
            {
                switch (member.Value)
                {
                    case NodeValue value:
                        Expression(value.Node, root, projected: true);
                        break;
                    case NestedValue nested:
                        // The path reaches a complex member; the projection below it is read off that.
                        Member(nested.Path, root, projected: true);
                        Projection(nested.Projection, null);
                        break;
                }
            }
        }

        /// <summary>
        /// Walks one expression, recording a sensitive member on its own and — separately — a sensitive
        /// member sharing the expression with a constant. Sharing the expression rather than being
        /// compared directly against it is deliberate: it is the same answer for the shapes that
        /// matter, it needs no per-node reasoning about which side is which, and every shape it is not
        /// exact for it errs toward the body, which is the safe direction.
        /// </summary>
        void Expression(Node? node, string? root, bool projected = false)
        {
            if (node is null)
            {
                return;
            }

            var found = new Found();
            Visit(node, root, projected, found);
            if (found.Sensitive && found.Constant)
            {
                InConstant = true;
            }
        }

        sealed class Found
        {
            public bool Sensitive;
            public bool Constant;
        }

        void Visit(Node? node, string? root, bool projected, Found found)
        {
            switch (node)
            {
                case null:
                    return;
                case ConstNode:
                    found.Constant = true;
                    return;
                case MemberNode member:
                    if (Member(member.Path, root, projected))
                    {
                        found.Sensitive = true;
                    }

                    return;
                case BinaryNode binary:
                    Visit(binary.Left, root, projected, found);
                    Visit(binary.Right, root, projected, found);
                    return;
                case UnaryNode unary:
                    Visit(unary.Operand, root, projected, found);
                    return;
                case CallNode call:
                    Visit(call.Target, root, projected, found);
                    foreach (var argument in call.Arguments)
                    {
                        Visit(argument, root, projected, found);
                    }

                    return;
                case ConditionalNode conditional:
                    Visit(conditional.Test, root, projected, found);
                    Visit(conditional.IfTrue, root, projected, found);
                    Visit(conditional.IfFalse, root, projected, found);
                    return;
                case CollateNode collate:
                    Visit(collate.Target, root, projected, found);
                    return;
                case CompositeKeyNode composite:
                    foreach (var part in composite.Parts)
                    {
                        Visit(part, root, projected, found);
                    }

                    return;
                case AggregateNode aggregate:
                    // Folded from many rows to one value, so what it returns is not the member itself —
                    // but a constant reaching it still shares the expression with one.
                    Visit(aggregate.Selector, root, projected: false, found);
                    Visit(aggregate.Predicate, root, projected: false, found);
                    return;
                case SubqueryNode subquery:
                    // The collection member is read off this row; what is inside is read off its
                    // elements, which are rows of a type this walk cannot name.
                    Member(subquery.Path, root);
                    Visit(subquery.Predicate, null, projected: false, found);
                    Visit(subquery.Selector, null, projected: false, found);
                    return;
                case InSourceNode inSource:
                    Visit(inSource.Value, root, projected, found);
                    Visit(inSource.Selector, inSource.Root, projected: false, found);
                    Visit(inSource.Predicate, inSource.Root, projected: false, found);
                    return;
                case ElementNode:
                case GroupKeyNode:
                    return;
                default:
                    // A node kind this walk does not know cannot be reasoned about, so it is treated as
                    // both — the query travels as a body and its answer is not stored. Failing closed
                    // here is what lets the vocabulary grow without this quietly going blind.
                    found.Sensitive = true;
                    found.Constant = true;
                    InProjection = true;
                    return;
            }
        }

        bool Member(IReadOnlyList<string> path, string? root, bool projected = false)
        {
            if (path.Count == 0 ||
                !sensitive(root, path))
            {
                return false;
            }

            if (projected)
            {
                InProjection = true;
            }

            return true;
        }
    }
}
