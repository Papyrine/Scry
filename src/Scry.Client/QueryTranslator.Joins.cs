// The operators that read a second source: the set combinations and the joins, and the grammar the
// pipeline each of them carries has to obey.
sealed partial class QueryTranslator
{
    /// <summary>
    /// Translates <c>left.Union(right)</c> and its siblings. The other side is a captured Scry source
    /// of its own, so its root, filters and projection are read straight off it — both sides project
    /// their own shape, and the server checks the two agree.
    /// </summary>
    static SetOp TranslateSet(MethodCallExpression call, SetKind kind)
    {
        if (call.Arguments.Count != 2)
        {
            throw new NotSupportedException($"'{call.Method.Name}' with an equality comparer is not supported by Scry.");
        }

        if (Evaluate(call.Arguments[1]) is not IQueryable {Provider: QueryProvider provider} queryable)
        {
            throw new NotSupportedException($"The other side of '{call.Method.Name}' must be a Scry source.");
        }

        var ops = Translate(queryable.Expression).ToList();

        // The operand's projection has its own slot on the wire, and nothing may follow it — the
        // shape both sides share is the last thing an operand says.
        Projection? projection = null;
        if (ops.Count > 0 &&
            ops[^1] is SelectOp select)
        {
            projection = select.Projection;
            ops.RemoveAt(ops.Count - 1);
        }

        if (projection is null ||
            ops.Any(_ => _ is SelectOp))
        {
            throw new NotSupportedException(
                $"The other side of '{call.Method.Name}' must Select the shape both sides share, last.");
        }

        if (ops.All(_ => _ is WhereOp))
        {
            return new(kind, provider.Root, FoldPredicates(ops), projection);
        }

        ValidateSideOps(ops, $"the other side of '{call.Method.Name}'");
        return new(kind, provider.Root, null, projection)
        {
            OperandOps = ops
        };
    }

    /// <summary>
    /// Translates <c>outer.Join(inner, o =&gt; …, i =&gt; …, (o, i) =&gt; new {…})</c>. The inner argument is
    /// another captured Scry source, so its own root and any filters it carries are read straight off
    /// it rather than being re-derived here.
    /// </summary>
    JoinOp TranslateJoin(MethodCallExpression call, JoinKind kind)
    {
        if (call.Arguments.Count != 5)
        {
            throw new NotSupportedException("A join must supply an inner source, both key selectors, and a result selector.");
        }

        var (root, innerPredicate, innerOps) = InnerSource(call.Arguments[1]);
        var outerKey = Lambda(call.Arguments[2]);
        var innerKey = Lambda(call.Arguments[3]);
        var result = Lambda(call.Arguments[4]);

        if (result.Parameters.Count != 2)
        {
            throw new NotSupportedException("A join's result selector must take the outer and inner rows.");
        }

        return new(
            root,
            kind,
            TranslateJoinKey(outerKey),
            TranslateJoinKey(innerKey),
            innerPredicate,
            JoinMembers(result, kind))
        {
            InnerOps = innerOps
        };
    }

    // A key constructed from several members joins on all of them at once, position by position.
    // C# already guarantees the two sides construct the same shape, since Join takes one key type.
    Node TranslateJoinKey(LambdaExpression key)
    {
        if (key.Body is not (NewExpression or MemberInitExpression))
        {
            return TranslateExpr(key.Body, key.Parameters[0]);
        }

        var parts = NestedMembers(key.Body)
            .Select(_ => TranslateExpr(_.Value, key.Parameters[0]))
            .ToList();

        return parts.Count switch
        {
            0 => throw new NotSupportedException("A composite join key must have at least one member."),
            1 => parts[0],
            _ => new CompositeKeyNode(parts)
        };
    }

    // The joined source is a captured queryable of its own. A plain filter crosses as the predicate
    // every server reads; an ordering bounded by paging crosses as the inner side's own pipeline,
    // under the wire version that introduced it. Every other operator would describe rows the join
    // has already consumed.
    static (string Root, Node? Predicate, IReadOnlyList<QueryOp>? Ops) InnerSource(Expression expression)
    {
        var value = Evaluate(expression);
        if (value is not IQueryable {Provider: QueryProvider provider} queryable)
        {
            throw new NotSupportedException("The inner side of a join must be a Scry source.");
        }

        var ops = Translate(queryable.Expression);
        if (ops.All(_ => _ is WhereOp))
        {
            return (provider.Root, FoldPredicates(ops), null);
        }

        ValidateSideOps(ops, "the inner side of a join");
        return (provider.Root, null, ops);
    }

    static Node? FoldPredicates(IReadOnlyList<QueryOp> ops)
    {
        Node? predicate = null;
        foreach (var op in ops)
        {
            var where = (WhereOp)op;
            predicate = predicate is null
                ? where.Predicate
                : new BinaryNode(BinaryOp.AndAlso, predicate, where.Predicate);
        }

        return predicate;
    }

    /// <summary>
    /// The grammar a side pipeline obeys — filters first, then an ordering that exists only to bound
    /// the paging after it: <c>Where* [OrderBy ThenBy* (Skip [Take] | Take)]</c>. An unbounded
    /// ordering would be discarded inside a subquery, and unordered paging would slice rows in no
    /// defined order, so each requires the other.
    /// </summary>
    static void ValidateSideOps(IReadOnlyList<QueryOp> ops, string side)
    {
        var stage = 0;
        foreach (var op in ops)
        {
            switch (op)
            {
                case WhereOp when stage == 0:
                    break;
                case OrderByOp when stage == 0:
                    stage = 1;
                    break;
                case ThenByOp when stage == 1:
                    break;
                case SkipOp when stage == 1:
                    stage = 2;
                    break;
                case TakeOp when stage is 1 or 2:
                    stage = 3;
                    break;
                default:
                    throw new NotSupportedException(
                        $"'{op.GetType().Name.Replace("Op", "")}' is not supported on {side} — only filters, and an ordering bounded by Skip or Take, cross over, in that order.");
            }
        }

        if (stage == 1)
        {
            throw new NotSupportedException(
                $"An ordering on {side} must be bounded by Skip or Take — unbounded, a subquery discards it.");
        }
    }

    List<JoinMember> JoinMembers(LambdaExpression result, JoinKind kind)
    {
        var members = new List<JoinMember>();
        foreach (var (name, raw) in NestedMembers(result.Body))
        {
            // A value read from a side the join can leave unmatched — or an aggregate over an
            // possibly-empty group — is assigned to a nullable target, so the compiler wraps it in a
            // widening convert. That is the shape the server produces anyway, so unwrap it rather than
            // reject the projection.
            var value = raw is UnaryExpression { NodeType: ExpressionType.Convert } convert &&
                        Nullable.GetUnderlyingType(convert.Type) == convert.Operand.Type
                ? convert.Operand
                : raw;

            // The inner side of a group join is a group rather than a row, so the only thing a member
            // can be there is an aggregate folding it.
            if (kind == JoinKind.Group &&
                value is MethodCallExpression fold &&
                IsCallOver(fold, result.Parameters[1]))
            {
                members.Add(
                    new(name, JoinSide.Inner, [])
                    {
                        Aggregate = TranslateAggregate(fold)
                    });
                continue;
            }

            // Each leaf must say which side it reads, so it has to be a plain path rooted at one of
            // the two parameters — the joined pair has no single root to resolve it against.
            var side = Rooted(value, result.Parameters[0])
                ? JoinSide.Outer
                : Rooted(value, result.Parameters[1])
                    ? JoinSide.Inner
                    : throw new NotSupportedException(
                        $"Join projection member '{name}' must be a member path on the outer or inner row.");

            members.Add(new(name, side, MemberPath((MemberExpression)value)));
        }

        if (members.Count == 0)
        {
            throw new NotSupportedException("A join must project at least one member.");
        }

        return members;
    }

    static bool Rooted(Expression expression, ParameterExpression root) =>
        expression is MemberExpression member && IsRooted(member, root);
}
