// ReSharper disable TailRecursiveCall
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

        // Whether each key of the current grouping touched a marked member, by index, so a group key
        // read later — in the HAVING, in the Select — answers as the key's own expression would.
        readonly List<bool> groupKeys = [];

        // Inside a subquery: the collection's path off the row, prefixed onto every path read there,
        // so the element's members resolve through the collection rather than as an unresolved name.
        IReadOnlyList<string>? prefix;

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
        string? Operator(QueryOp op, string? root) =>
            op switch
            {
                WhereOp where => Read(where.Predicate, root),
                OrderByOp orderBy => Read(orderBy.Key, root),
                ThenByOp thenBy => Read(thenBy.Key, root),
                CountOp count => Read(count.Predicate, root),
                LongCountOp longCount => Read(longCount.Predicate, root),
                AnyOp any => Read(any.Predicate, root),
                AllOp all => Read(all.Predicate, root),
                FirstOp first => Read(first.Predicate, root),
                SingleOp single => Read(single.Predicate, root),
                LastOp last => Read(last.Predicate, root),
                AggregateOp aggregate => Read(aggregate.Selector, root),
                SelectOp select => Projected(select.Projection, root),
                GroupByOp groupBy => Grouped(groupBy, root),

                // Narrowing keeps the row and changes its type, which is a source name of its own.
                OfTypeOp ofType => ofType.Type,

                SelectManyOp selectMany => Flattened(selectMany, root),
                JoinOp join => Joined(join, root),
                SetOp set => Combined(set, root),

                // Carry no member path and no constant of a member's own, so there is nothing here to
                // read. A terminal carrying no predicate reaches its own arm above and reads nothing
                // there either.
                SkipOp or TakeOp or DistinctOp or ReverseOp or PageOp => root
            };

        // Reads one expression off the row, which the operator leaves as it was.
        string? Read(Node? node, string? root)
        {
            Expression(node, root);
            return root;
        }

        string? Projected(Projection projection, string? root)
        {
            Projection(projection, root);
            return root;
        }

        string? Grouped(GroupByOp op, string? root)
        {
            groupKeys.Clear();
            foreach (var key in op.Keys)
            {
                var found = new Found();
                Visit(key, root, projected: false, found);
                if (found is { Sensitive: true, Constant: true })
                {
                    InConstant = true;
                }

                groupKeys.Add(found.Sensitive);
            }

            // The rows are groups now, and a later path reads a key or an aggregate rather than a
            // member of the source.
            return null;
        }

        string? Flattened(SelectManyOp op, string? root)
        {
            Member(op.Path, root);
            return null;
        }

        string? Joined(JoinOp op, string? root)
        {
            Expression(op.OuterKey, root);
            Expression(op.InnerKey, op.Root);
            if (op.InnerPredicate is { } inner)
            {
                Expression(inner, op.Root);
            }

            if (op.InnerOps is { } innerOps)
            {
                Pipeline(innerOps, op.Root);
            }

            foreach (var member in op.Result)
            {
                var side = member.Side == JoinSide.Inner ? op.Root : root;
                Member(member.Path, side, projected: true);
                if (member.Aggregate is { } folded)
                {
                    Expression(folded, op.Root);
                }
            }

            return null;
        }

        string? Combined(SetOp op, string? root)
        {
            if (op.Predicate is { } filter)
            {
                Expression(filter, op.Root);
            }

            if (op.OperandOps is { } operandOps)
            {
                Pipeline(operandOps, op.Root);
            }

            // The operand's own rows, projected to match the pipeline's shape.
            Projection(op.Projection, op.Root);
            return null;
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
            if (found is { Sensitive: true, Constant: true })
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
                    // elements, which both resolvers reach by following the collection's path one
                    // segment further. A subquery cannot nest, so one prefix is the most there is.
                    Member(subquery.Path, root);
                    var outer = prefix;
                    prefix = [.. outer ?? [], .. subquery.Path];
                    Visit(subquery.Predicate, root, projected: false, found);
                    Visit(subquery.Selector, root, projected: false, found);
                    prefix = outer;
                    return;
                case InSourceNode inSource:
                    Visit(inSource.Value, root, projected, found);
                    Visit(inSource.Selector, inSource.Root, projected: false, found);
                    Visit(inSource.Predicate, inSource.Root, projected: false, found);
                    return;
                case GroupKeyNode groupKey:
                    // Reads the key's value, so it answers as the key's expression did — and a marked
                    // key in the Select is a marked member in the result.
                    if (groupKey.Index < groupKeys.Count &&
                        groupKeys[groupKey.Index])
                    {
                        found.Sensitive = true;
                        if (projected)
                        {
                            InProjection = true;
                        }
                    }

                    return;
                case ElementNode:
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

        static IReadOnlyList<string> Prefixed(IReadOnlyList<string> prefix, IReadOnlyList<string> path) =>
            [.. prefix, .. path];

        bool Member(IReadOnlyList<string> path, string? root, bool projected = false)
        {
            if (path.Count == 0)
            {
                return false;
            }

            var full = prefix is null ? path : Prefixed(prefix, path);
            if (!sensitive(root, full))
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
