/// <summary>
/// Translates a captured LINQ expression tree into the wire AST. Supports the closed operator set;
/// anything outside it throws a clear <see cref="NotSupportedException"/> at translation time.
/// </summary>
sealed partial class QueryTranslator
{
    public static IReadOnlyList<QueryOp> Translate(Expression expression) =>
        Translate(expression, out _);

    /// <summary>
    /// Translates a query, also reporting the attachment handles its result carries. The bindings are
    /// a client-side concern — nothing about them is on the wire — so they come back beside the
    /// pipeline rather than in it.
    /// </summary>
    public static IReadOnlyList<QueryOp> Translate(Expression expression, out IReadOnlyList<AttachmentBinding> attachments)
    {
        var translator = new QueryTranslator();
        var ops = new List<QueryOp>();
        // A variable a lambda binds is substituted before anything is read, so every reader below sees
        // the one expression the lambda stands for — see LetInliner.
        translator.Visit(LetInliner.Inline(expression), ops);
        attachments = translator.ResolveAttachments(ops);
        return ops;
    }

    /// <summary>
    /// Translates a standalone lambda body — a predicate or an aggregate selector supplied to a
    /// terminal rather than captured in the query expression.
    /// </summary>
    public static Node TranslateLambda(LambdaExpression lambda)
    {
        var inlined = (LambdaExpression) LetInliner.Inline(lambda);
        return new QueryTranslator().TranslateExpr(inlined.Body, inlined.Parameters[0]);
    }

    void Visit(Expression expression, List<QueryOp> ops)
    {
        if (expression is ConstantExpression)
        {
            return;
        }

        if (expression is not MethodCallExpression call)
        {
            throw Unsupported(expression);
        }

        Visit(call.Arguments[0], ops);

        // GroupBy with a result selector abbreviates the GroupBy + Select it stands for, and unfolds
        // into the same two operators on the wire.
        if (call is {Method.Name: "GroupBy", Arguments.Count: 3} &&
            Lambda(call.Arguments[2]) is {Parameters.Count: 2} result)
        {
            ops.Add(TranslateGroupBy(Lambda(call.Arguments[1])));
            ops.Add(new SelectOp(TranslateProjection(RebindGroupResult(result))));
            return;
        }

        ops.Add(TranslateCall(call));
    }

    /// <summary>
    /// Rewrites a GroupBy result selector onto the grouping the following Select would have read: the
    /// key parameter becomes <c>g.Key</c>, the group parameter the grouping itself, and the body then
    /// translates as the grouped projection it already is.
    /// </summary>
    static LambdaExpression RebindGroupResult(LambdaExpression result)
    {
        var key = result.Parameters[0];
        var group = result.Parameters[1];
        var grouping = Expression.Parameter(
            typeof(IGrouping<,>).MakeGenericType(key.Type, group.Type.GetGenericArguments()[0]),
            group.Name);
        var body = new GroupResultRebinder(key, group, grouping).Visit(result.Body);
        return Expression.Lambda(body, grouping);
    }

    QueryOp TranslateCall(MethodCallExpression call)
    {
        switch (call.Method.Name)
        {
            case "Where":
                var where = Lambda(call.Arguments[1]);
                return new WhereOp(TranslateExpr(where.Body, where.Parameters[0]));

            case "OrderBy":
                return new OrderByOp(TranslateKey(call), Descending: false);
            case "OrderByDescending":
                return new OrderByOp(TranslateKey(call), Descending: true);
            case "ThenBy":
                return new ThenByOp(TranslateKey(call), Descending: false);
            case "ThenByDescending":
                return new ThenByOp(TranslateKey(call), Descending: true);

            case "Skip":
                return new SkipOp(IntArgument(call.Arguments[1]));
            case "Take":
                return new TakeOp(IntArgument(call.Arguments[1]));

            case "GroupBy" when call.Arguments.Count == 2:
                return TranslateGroupBy(Lambda(call.Arguments[1]));

            // The result-selector form was unfolded before this switch, so what arrives here is an
            // element selector or a comparer — and silently grouping without one would answer with
            // aggregates over the wrong elements.
            case "GroupBy":
                throw new NotSupportedException(
                    "This overload of GroupBy is not supported by Scry — group by the key alone, and compose the elements inside the aggregates that read the group.");

            case "Select":
                return new SelectOp(TranslateProjection(Lambda(call.Arguments[1])));

            case "OfType":
                return new OfTypeOp(ModelSource(call.Method.GetGenericArguments()[0]));

            case "Cast":
                throw new NotSupportedException(
                    "Cast is not supported by Scry. Its check runs when a row is materialized into an entity, and a Scry query always ends in a projection instead — so the assertion would be dropped and the derived members read as null over rows of any other type. Use OfType, which narrows by filtering and needs no check on the way back.");

            case "SelectMany" when call.Arguments.Count == 2:
                var flatten = Lambda(call.Arguments[1]);
                if (flatten.Body is not MemberExpression collection ||
                    !IsRootedCollection(collection, flatten.Parameters[0]))
                {
                    throw new NotSupportedException(
                        "SelectMany must name a collection on the row, as in '.SelectMany(_ => _.Lines)'.");
                }

                return new SelectManyOp(MemberPath(collection));

            case "SelectMany":
                throw new NotSupportedException(
                    "SelectMany with a result selector is not supported by Scry — flatten first, then Select.");

            case "Distinct" when call.Arguments.Count == 1:
                return new DistinctOp();

            case "Distinct":
                throw new NotSupportedException("Distinct with an equality comparer is not supported by Scry.");

            case "Reverse":
                return new ReverseOp();

            case "Union":
                return TranslateSet(call, SetKind.Union);
            case "Concat":
                return TranslateSet(call, SetKind.Concat);
            case "Intersect":
                return TranslateSet(call, SetKind.Intersect);
            case "Except":
                return TranslateSet(call, SetKind.Except);

            case "Join":
                return TranslateJoin(call, JoinKind.Inner);
            case "LeftJoin":
                return TranslateJoin(call, JoinKind.Left);
            case "RightJoin":
                return TranslateJoin(call, JoinKind.Right);
            case "GroupJoin":
                return TranslateJoin(call, JoinKind.Group);

            default:
                throw new NotSupportedException($"LINQ operator '{call.Method.Name}' is not supported by Scry.");
        }
    }

    // The wire name of the source a generated query model stands for. Only the generator can know it —
    // a model's CLR name and its source name diverge the moment a [Queryable(Name = "...")] renames
    // one — so it is carried on the model rather than guessed from the type name.
    // Falls back to the type's own name for a hand-built source, whose caller chose the source names
    // anyway. Nothing rests on getting it right here: the server re-resolves the name against its
    // allow-list, so a wrong guess is a rejected request rather than a reachable type.
    static string ModelSource(Type model) =>
        ScryModels.Of(model)?.Source ?? model.Name;

    Node TranslateKey(MethodCallExpression call)
    {
        var lambda = Lambda(call.Arguments[1]);
        return TranslateExpr(lambda.Body, lambda.Parameters[0]);
    }

    sealed class GroupResultRebinder(ParameterExpression key, ParameterExpression group, ParameterExpression grouping) :
        ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == group)
            {
                return grouping;
            }

            if (node == key)
            {
                return Expression.Property(grouping, "Key");
            }

            return base.VisitParameter(node);
        }
    }
}
