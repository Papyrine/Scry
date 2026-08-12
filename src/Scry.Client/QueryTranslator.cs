using System.Collections.ObjectModel;

/// <summary>
/// Translates a captured LINQ expression tree into the wire AST. Supports the closed operator set;
/// anything outside it throws a clear <see cref="NotSupportedException"/> at translation time.
/// </summary>
sealed class QueryTranslator
{
    // How to say "the key" for a query that grouped by one. A key that is a plain member says so by
    // its own path, which is what the server matches it back by; a computed key has no path, so it is
    // named by position instead.
    Node? groupKeyNode;

    // The same, per part of a composite key, by the name the key type gave it — what 'g.Key.Region' is
    // resolved through. Null while the query grouped by a single key.
    Dictionary<string, Node>? groupKeyParts;

    // Attachment members met while translating the projection, resolved against it once it is whole:
    // the keys an attachment needs are sibling members of the same projection, so none of them is
    // known to be present until every member has been translated.
    readonly List<Pending> pendingAttachments = [];

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
        translator.Visit(expression, ops);
        attachments = translator.ResolveAttachments(ops);
        return ops;
    }

    /// <summary>An attachment leaf met in a projection, before its keys have been looked for.</summary>
    /// <param name="Target">Where it sits in the projected object.</param>
    /// <param name="Prefix">
    /// The member path of the row it hangs off — empty for the query's own row, or the navigation a
    /// nested projection descended into. Its key members are read relative to this.
    /// </param>
    /// <param name="Root">The name of the source the attachment is fetched from.</param>
    /// <param name="Member">The attachment member on that source's row.</param>
    /// <param name="Keys">The row's key members, named relative to <paramref name="Prefix"/>.</param>
    sealed record Pending(
        IReadOnlyList<string> Target,
        IReadOnlyList<string> Prefix,
        string Root,
        string Member,
        IReadOnlyList<string> Keys);

    /// <summary>
    /// Translates a standalone lambda body — a predicate or an aggregate selector supplied to a
    /// terminal rather than captured in the query expression.
    /// </summary>
    public static Node TranslateLambda(LambdaExpression lambda) =>
        new QueryTranslator().TranslateExpr(lambda.Body, lambda.Parameters[0]);

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

    // A key built from several members groups on all of them at once. Each part keeps the name the key
    // type gave it, so a later read of 'g.Key.Region' can be resolved back to the member it grouped by;
    // the server matches the same paths to the positions it grouped at.
    GroupByOp TranslateGroupBy(LambdaExpression keyLambda)
    {
        var parameter = keyLambda.Parameters[0];

        if (keyLambda.Body is NewExpression or MemberInitExpression)
        {
            var keys = new List<Node>();
            var parts = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var (name, value) in NestedMembers(keyLambda.Body))
            {
                var key = TranslateExpr(value, parameter);
                keys.Add(key);

                // A member part is named back by its path; a computed one by its position.
                parts.Add(name, key is MemberNode member ? member : new GroupKeyNode(keys.Count - 1));
            }

            if (keys.Count == 0)
            {
                throw new NotSupportedException("A composite GroupBy key must have at least one member.");
            }

            groupKeyNode = null;
            groupKeyParts = parts;
            return new(keys);
        }

        var single = TranslateExpr(keyLambda.Body, parameter);
        groupKeyNode = single is MemberNode path ? path : new GroupKeyNode(0);
        groupKeyParts = null;
        return new([single]);
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

    // The wire name of the source a generated query model stands for. Only the generator can know it —
    // a model's CLR name and its source name diverge the moment a [Queryable(Name = "...")] renames
    // one — so it is carried on the model rather than guessed from the type name.
    // Falls back to the type's own name for a hand-built source, whose caller chose the source names
    // anyway. Nothing rests on getting it right here: the server re-resolves the name against its
    // allow-list, so a wrong guess is a rejected request rather than a reachable type.
    static string ModelSource(Type model) =>
        model.GetCustomAttribute<ScryModelAttribute>()?.Source ?? model.Name;

    static bool Rooted(Expression expression, ParameterExpression root) =>
        expression is MemberExpression member && IsRooted(member, root);

    Node TranslateKey(MethodCallExpression call)
    {
        var lambda = Lambda(call.Arguments[1]);
        return TranslateExpr(lambda.Body, lambda.Parameters[0]);
    }

    Projection TranslateProjection(LambdaExpression selector)
    {
        var parameter = selector.Parameters[0];
        var grouped = parameter.Type.IsGenericType &&
                      parameter.Type.GetGenericTypeDefinition() == typeof(IGrouping<,>);

        return selector.Body switch
        {
            NewExpression construction => FromNew(construction, parameter, grouped),
            MemberInitExpression init => FromMemberInit(init, parameter, grouped),
            _ => throw new NotSupportedException("A projection must construct an object (anonymous type, record, or object initializer).")
        };
    }

    Projection FromNew(NewExpression construction, ParameterExpression parameter, bool grouped)
    {
        var names = ProjectionNames(construction);
        var arguments = construction.Arguments;
        var members = new List<ProjectionMember>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (TryAttachment(arguments[i], parameter, grouped, [names[i]], prefix: []))
            {
                continue;
            }

            members.Add(new(names[i], ProjectionValue(arguments[i], parameter, grouped, [names[i]])));
        }

        return Built(members);
    }

    Projection FromMemberInit(MemberInitExpression init, ParameterExpression parameter, bool grouped)
    {
        var members = new List<ProjectionMember>(init.Bindings.Count);
        foreach (var binding in init.Bindings)
        {
            if (binding is not MemberAssignment assignment)
            {
                throw new NotSupportedException("Only simple member assignments are supported in a projection.");
            }

            if (TryAttachment(assignment.Expression, parameter, grouped, [assignment.Member.Name], prefix: []))
            {
                continue;
            }

            members.Add(new(assignment.Member.Name, ProjectionValue(assignment.Expression, parameter, grouped, [assignment.Member.Name])));
        }

        return Built(members);
    }

    // An attachment leaves the wire projection entirely, so a projection of nothing else would reach
    // the server empty. Reported here rather than as the server's own "empty projection", which would
    // read as a wire fault rather than the missing keys it really is.
    static Projection Built(List<ProjectionMember> members)
    {
        if (members.Count == 0)
        {
            throw new NotSupportedException(
                "A projection of nothing but attachments has no members left to send. Project the row's key beside the attachment — that is what the fetch is keyed by.");
        }

        return new(members);
    }

    /// <summary>
    /// Records an attachment leaf and reports that it was one, so the caller leaves it out of the wire
    /// projection. Nothing is validated here: whether its keys were projected too is a question about
    /// the whole projection, answered once every member has been seen.
    /// </summary>
    bool TryAttachment(
        Expression expression,
        ParameterExpression parameter,
        bool grouped,
        IReadOnlyList<string> target,
        IReadOnlyList<string> prefix)
    {
        if (expression is not MemberExpression member ||
            member.Type != typeof(ScryAttachment) ||
            !IsRooted(member, parameter))
        {
            return false;
        }

        // A grouped projection reads the group, not a row — there is no single row left for a key to
        // identify, and the aggregate the group folds to has no attachment either.
        if (grouped)
        {
            throw new NotSupportedException(
                $"Attachment '{member.Member.Name}' cannot be projected out of a group. A group is many rows folded to one, so there is no row key to fetch an attachment by.");
        }

        var path = MemberPath(member);
        var declaring = member.Expression!.Type;
        var (root, keys) = AttachmentModel.Fetching(declaring, member.Member.Name);

        // The row the attachment hangs off: the query's own where the path is a bare member, or the
        // navigation the path traversed to reach it.
        var owner = prefix.Concat(path.Take(path.Count - 1)).ToList();
        pendingAttachments.Add(new(target, owner, root, member.Member.Name, keys));
        return true;
    }

    /// <summary>
    /// Matches every attachment met in the projection to the key members it is fetched by, which must
    /// have been projected as leaves of the same row. A missing one is refused here rather than
    /// producing a handle that would fail at fetch time with nothing to say why.
    /// </summary>
    IReadOnlyList<AttachmentBinding> ResolveAttachments(IReadOnlyList<QueryOp> ops)
    {
        if (pendingAttachments.Count == 0)
        {
            return [];
        }

        // These rewrite what a row is — deduplicated, flattened, combined, or built from two sources —
        // so a key projected beside an attachment no longer identifies one row of one source.
        if (ops.FirstOrDefault(_ => _ is DistinctOp or SelectManyOp or JoinOp or SetOp or GroupByOp) is { } refused)
        {
            throw new NotSupportedException(
                $"An attachment cannot be carried through {refused.GetType().Name.Replace("Op", "")}. The result's rows no longer correspond to single rows of the source the attachment is fetched from.");
        }

        var projection = ops.OfType<SelectOp>().Single().Projection;
        var leaves = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        CollectLeaves(projection, [], leaves);

        var bindings = new List<AttachmentBinding>(pendingAttachments.Count);
        foreach (var pending in pendingAttachments)
        {
            var sources = new List<IReadOnlyList<string>>(pending.Keys.Count);
            foreach (var key in pending.Keys)
            {
                var wanted = pending.Prefix.Append(key).ToList();
                if (!leaves.TryGetValue(string.Join(".", wanted), out var source))
                {
                    throw new NotSupportedException(
                        $"Attachment '{pending.Member}' needs '_.{string.Join(".", wanted)}' projected beside it: an attachment is fetched by its row's key, so the key has to come back with the row. Add it to the projection.");
                }

                sources.Add(source);
            }

            bindings.Add(new(pending.Target, pending.Root, pending.Member, sources));
        }

        return bindings;
    }

    // Every member path the projection reads, mapped to where its value lands in the result object.
    // Only plain member reads are collected: a computed leaf is not a key, whatever it was computed
    // from, so one cannot stand in for the key an attachment names.
    static void CollectLeaves(
        Projection projection,
        IReadOnlyList<string> memberPrefix,
        Dictionary<string, IReadOnlyList<string>> leaves,
        IReadOnlyList<string>? outputPrefix = null)
    {
        foreach (var member in projection.Members)
        {
            var output = (outputPrefix ?? []).Append(member.Name).ToList();
            switch (member.Value)
            {
                case NodeValue {Node: MemberNode node}:
                    leaves[string.Join(".", memberPrefix.Concat(node.Path))] = output;
                    break;

                case NestedValue nested:
                    CollectLeaves(nested.Projection, [..memberPrefix, ..nested.Path], leaves, output);
                    break;
            }
        }
    }

    ProjectionValue ProjectionValue(Expression expression, ParameterExpression parameter, bool grouped, IReadOnlyList<string>? target = null)
    {
        target ??= [];
        // Over a group the row being read is the grouping itself, which TranslateExpr already knows how
        // to read: its Key is the group key and a call taking it is an aggregate. That leaves the two
        // free to compose — _.Sum(x => x.Amount) / _.Count(), or _.Key.ToUpper().
        if (grouped)
        {
            return new NodeValue(TranslateExpr(expression, parameter));
        }

        // A member whose value is itself a constructed object is a nested projection into a navigation
        // (e.g. Department = new DepartmentInfo(_.Department.Name)), producing a nested result object.
        // One that reads nothing from the row is not a projection at all — it is a constructed constant
        // such as new DateTime(2026, 1, 1), and falls through to be evaluated as one.
        if (expression is NewExpression or MemberInitExpression &&
            ReferencesParameter(expression, parameter))
        {
            return TranslateNested(expression, parameter, target);
        }

        return new NodeValue(TranslateExpr(expression, parameter));
    }

    NestedValue TranslateNested(Expression expression, ParameterExpression parameter, IReadOnlyList<string> target)
    {
        var members = new List<(string Name, Node Value)>();
        foreach (var (name, value) in NestedMembers(expression))
        {
            // An attachment nested inside a projected object reads the same full path it would at the
            // top level, so only where its handle lands differs.
            if (TryAttachment(value, parameter, grouped: false, [..target, name], prefix: []))
            {
                continue;
            }

            members.Add((name, TranslateExpr(value, parameter)));
        }

        if (members.Count == 0)
        {
            throw new NotSupportedException("A nested projection must have at least one member.");
        }

        // The navigation a nested object descends into is inferred from the member paths it reads —
        // which may sit anywhere inside an expression, not only at its root, so they are collected from
        // the whole tree and then stripped back off it.
        var paths = new List<IReadOnlyList<string>>();
        foreach (var (_, value) in members)
        {
            CollectPaths(value, paths);
        }

        var prefix = CommonNavigationPrefix(paths);
        if (prefix.Count == 0)
        {
            throw new NotSupportedException(
                "A nested projection must read from a single navigation property (every member sharing, e.g., _.Department).");
        }

        var projected = members
            .Select(_ => new ProjectionMember(_.Name, new NodeValue(StripPrefix(_.Value, prefix.Count))))
            .ToList();

        return new(prefix, new(projected));
    }

    // ReSharper disable TailRecursiveCall
    /// <summary>
    /// Gathers every member path an expression reads from the row. The predicate and selector of a
    /// subquery or a membership test are skipped: they are rooted at the other sequence's element, not
    /// at the row, so they say nothing about which navigation the enclosing projection descends into.
    /// </summary>
    static void CollectPaths(Node node, List<IReadOnlyList<string>> paths)
    {
        switch (node)
        {
            case MemberNode member:
                paths.Add(member.Path);
                break;
            case SubqueryNode subquery:
                paths.Add(subquery.Path);
                break;
            case InSourceNode inSource:
                CollectPaths(inSource.Value, paths);
                break;
            case BinaryNode binary:
                CollectPaths(binary.Left, paths);
                CollectPaths(binary.Right, paths);
                break;
            case UnaryNode unary:
                CollectPaths(unary.Operand, paths);
                break;
            case CollateNode collate:
                CollectPaths(collate.Target, paths);
                break;
            case ConditionalNode conditional:
                CollectPaths(conditional.Test, paths);
                CollectPaths(conditional.IfTrue, paths);
                CollectPaths(conditional.IfFalse, paths);
                break;
            case CallNode call:
                CollectPaths(call.Target, paths);
                foreach (var argument in call.Arguments)
                {
                    CollectPaths(argument, paths);
                }

                break;
        }
    }
    // ReSharper restore TailRecursiveCall

    /// <summary>
    /// Rebases an expression onto the navigation the nested projection descends into, by dropping the
    /// shared leading segments from every path it reads.
    /// </summary>
    static Node StripPrefix(Node node, int prefix) =>
        node switch
        {
            MemberNode member => new MemberNode([..member.Path.Skip(prefix)]),
            SubqueryNode subquery => subquery with { Path = [..subquery.Path.Skip(prefix)] },
            InSourceNode inSource => inSource with { Value = StripPrefix(inSource.Value, prefix) },
            BinaryNode binary => new BinaryNode(binary.Op, StripPrefix(binary.Left, prefix), StripPrefix(binary.Right, prefix)),
            UnaryNode unary => new UnaryNode(unary.Op, StripPrefix(unary.Operand, prefix)),
            CollateNode collate => collate with { Target = StripPrefix(collate.Target, prefix) },
            ConditionalNode conditional => new ConditionalNode(
                StripPrefix(conditional.Test, prefix),
                StripPrefix(conditional.IfTrue, prefix),
                StripPrefix(conditional.IfFalse, prefix)),
            CallNode call => new CallNode(
                call.Function,
                StripPrefix(call.Target, prefix),
                [..call.Arguments.Select(_ => StripPrefix(_, prefix))]),
            _ => node
        };

    static IEnumerable<(string Name, Expression Value)> NestedMembers(Expression expression)
    {
        switch (expression)
        {
            case NewExpression construction:
                var names = ProjectionNames(construction);
                for (var i = 0; i < construction.Arguments.Count; i++)
                {
                    yield return (names[i], construction.Arguments[i]);
                }

                break;

            case MemberInitExpression init:
                foreach (var binding in init.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                    {
                        throw new NotSupportedException("Only simple member assignments are supported in a projection.");
                    }

                    yield return (assignment.Member.Name, assignment.Expression);
                }

                break;

            default:
                throw new NotSupportedException("A nested projection must construct an object.");
        }
    }

    // The navigation a nested projection descends into: the shared leading segments of every member
    // path, stopping before any member's final (scalar) segment so each keeps a non-empty relative
    // path. Empty means the members do not share a single navigation, which is unsupported.
    static List<string> CommonNavigationPrefix(IReadOnlyList<IReadOnlyList<string>> paths)
    {
        // Reading nothing from the row shares no navigation either, and the caller says so. Checked
        // here rather than left to the loop, whose All() is vacuously true on an empty list and would
        // then index into it — which is how a node type CollectPaths has no case for would surface.
        if (paths.Count == 0)
        {
            return [];
        }

        var prefix = new List<string>();
        while (paths.All(_ => _.Count > prefix.Count + 1))
        {
            var segment = paths[0][prefix.Count];
            if (paths.Any(_ => _[prefix.Count] != segment))
            {
                break;
            }

            prefix.Add(segment);
        }

        return prefix;
    }

    /// <summary>
    /// Translates an aggregate folding the group, including the composed forms: a <c>Where</c> before
    /// the fold filters the rows — <c>Count(predicate)</c> abbreviates it — and <c>Select</c> +
    /// <c>Distinct</c> folds only the distinct selected values. The grammar over the group is
    /// <c>g [.Where(pred)] [.Select(sel) [.Distinct()]] .Fold(…)</c>, written in that order because
    /// each stage reads what the previous one produced.
    /// </summary>
    AggregateNode TranslateAggregate(MethodCallExpression call)
    {
        Node? predicate = null;
        Node? selector = null;
        var distinct = false;

        // The fold's source, unwrapped from the outside in — so a Select is seen before the Where
        // written ahead of it, and a filter captured before any Select was seen must have been
        // written over the selected values, which the wire's predicate does not read.
        var source = call.Arguments[0];
        while (source is MethodCallExpression inner)
        {
            switch (inner.Method.Name)
            {
                case "Distinct" when inner.Arguments.Count == 1 && !distinct && selector is null:
                    distinct = true;
                    break;

                case "Select" when inner.Arguments.Count == 2 && selector is null && predicate is null:
                    var projected = Lambda(inner.Arguments[1]);
                    selector = TranslateExpr(projected.Body, projected.Parameters[0]);
                    break;

                case "Where" when inner.Arguments.Count == 2 && predicate is null:
                    var filter = Lambda(inner.Arguments[1]);
                    predicate = TranslateExpr(filter.Body, filter.Parameters[0]);
                    break;

                default:
                    throw new NotSupportedException(
                        $"'{inner.Method.Name}' cannot compose into an aggregate over a group this way — filter the rows, then select the values, then Distinct, then fold.");
            }

            source = inner.Arguments[0];
        }

        // string.Concat over the group's values is the text fold with nothing between them —
        // string.Join's empty-separator spelling, and like Join it folds the whole group.
        if (call is {Method.Name: "Concat", Arguments.Count: 1} &&
            call.Method.DeclaringType == typeof(string))
        {
            // The generic overload is what a non-string selector binds to.
            if (call.Method.IsGenericMethod)
            {
                throw new NotSupportedException("string.Concat over a group joins text — select a string member.");
            }

            if (predicate is not null ||
                distinct)
            {
                throw new NotSupportedException("string.Concat folds the whole group — filter the rows before grouping.");
            }

            if (selector is not MemberNode path)
            {
                throw new NotSupportedException(
                    "string.Concat over a group joins a text member the rows carry — string.Concat(_.Select(x => x.Code)).");
            }

            return new(AggregateFn.Join, path, "");
        }

        if (call is {Method.Name: "Count", Arguments.Count: 1})
        {
            // Without a Distinct there is nothing for selected values to change about a count, so the
            // selector is dropped rather than carried as noise.
            return new(AggregateFn.Count, distinct ? selector : null)
            {
                Predicate = predicate,
                Distinct = distinct
            };
        }

        if (call is {Method.Name: "Count", Arguments.Count: 2})
        {
            if (predicate is not null)
            {
                throw new NotSupportedException("Count over a group cannot combine a Where with its own predicate.");
            }

            if (distinct || selector is not null)
            {
                throw new NotSupportedException("Count over selected values takes no predicate — filter the rows before selecting.");
            }

            var counted = Lambda(call.Arguments[1]);
            return new(AggregateFn.Count, Selector: null)
            {
                Predicate = TranslateExpr(counted.Body, counted.Parameters[0])
            };
        }

        var function = call.Method.Name switch
        {
            "Sum" => AggregateFn.Sum,
            "Average" => AggregateFn.Average,
            "Min" => AggregateFn.Min,
            "Max" => AggregateFn.Max,
            // Reached for any other call written in a grouped projection, where only the group key and
            // an aggregate are expressible — so the arity below is safe to assume.
            _ => throw new NotSupportedException(
                $"'{call.Method.Name}' is not an aggregate. A grouped projection may only use the group key, Count/Sum/Average/Min/Max, and string.Join or string.Concat over the group's values.")
        };

        if (call.Arguments.Count == 2)
        {
            if (selector is not null)
            {
                throw new NotSupportedException(
                    $"Aggregate '{call.Method.Name}' already reads selected values — fold them bare, without a second selector.");
            }

            var folded = Lambda(call.Arguments[1]);
            selector = TranslateExpr(folded.Body, folded.Parameters[0]);
        }

        if (selector is null)
        {
            throw new NotSupportedException($"Aggregate '{call.Method.Name}' requires a selector.");
        }

        return new(function, selector)
        {
            Predicate = predicate,
            Distinct = distinct
        };
    }

    /// <summary>
    /// Translates <c>string.Join(separator, g.Select(x =&gt; x.Member))</c> — the text aggregate,
    /// SQL's <c>STRING_AGG</c>. The separator is a constant; the values are the group's rows read
    /// through the selector. Returns null when the call is not string.Join at all, so the caller can
    /// keep trying.
    /// </summary>
    AggregateNode? TryJoinText(MethodCallExpression call, ParameterExpression root)
    {
        if (call.Method.Name != "Join" ||
            call.Method.DeclaringType != typeof(string) ||
            call.Arguments.Count != 2 ||
            !ReferencesParameter(call, root))
        {
            return null;
        }

        // The generic overload is what a non-string selector binds to.
        if (call.Method.IsGenericMethod)
        {
            throw new NotSupportedException("string.Join over a group joins text — select a string member.");
        }

        if (ReferencesParameter(call.Arguments[0], root) ||
            Evaluate(call.Arguments[0]) is not string separator)
        {
            throw new NotSupportedException("string.Join over a group takes a constant separator.");
        }

        if (call.Arguments[1] is not MethodCallExpression {Method.Name: "Select", Arguments: [var source, var projection]} ||
            source != root)
        {
            throw new NotSupportedException(
                """
                string.Join over a group joins the values its selector reads — string.Join(", ", _.Select(x => x.Name)).
                """);
        }

        // An aggregate selector is a member path, as every aggregate's is.
        var selector = Lambda(projection);
        if (TranslateExpr(selector.Body, selector.Parameters[0]) is not MemberNode path)
        {
            throw new NotSupportedException(
                "string.Join over a group joins a text member the rows carry, not a computed value.");
        }

        return new(AggregateFn.Join, path, separator);
    }

    Node TranslateExpr(Expression expression, ParameterExpression root)
    {
        while (true)
        {
            switch (expression)
            {
                case UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} convert:
                    expression = convert.Operand;
                    continue;

                case UnaryExpression {NodeType: ExpressionType.Not} not:
                    return new UnaryNode(UnaryOp.Not, TranslateExpr(not.Operand, root));

                case UnaryExpression {NodeType: ExpressionType.Negate} negate:
                    return new UnaryNode(UnaryOp.Negate, TranslateExpr(negate.Operand, root));

                // A binary member's Length is an ArrayLength node rather than a member access, since
                // the CLR spells an array's length as an operator.
                case UnaryExpression {NodeType: ExpressionType.ArrayLength} length
                    when length.Operand.Type == typeof(byte[]) && ReferencesParameter(length, root):
                    return new CallNode(KnownFunction.BytesLength, TranslateExpr(length.Operand, root), []);

                // C# compiles string concatenation to an Add carrying string.Concat as its method. The
                // operator alone cannot say which was meant — an Add of a string and a number is a
                // concatenation, an Add of two numbers is arithmetic — so the intent is recorded here,
                // where the method is still visible, rather than guessed from the operand types later.
                case BinaryExpression {NodeType: ExpressionType.Add, Method: {Name: "Concat"} method} concat
                    when method.DeclaringType == typeof(string):
                    return new CallNode(
                        KnownFunction.StringConcat,
                        TranslateExpr(concat.Left, root),
                        [TranslateExpr(concat.Right, root)]);

                case BinaryExpression binary:
                    return new BinaryNode(MapBinary(binary.NodeType), TranslateExpr(binary.Left, root), TranslateExpr(binary.Right, root));

                case ConditionalExpression conditional:
                    return new ConditionalNode(
                        TranslateExpr(conditional.Test, root),
                        TranslateExpr(conditional.IfTrue, root),
                        TranslateExpr(conditional.IfFalse, root));

                // Inside a grouped Where the row being read is a group: its Key is whatever the query
                // grouped by, and a call taking the group itself folds that group's rows.
                // One part of a composite key: 'g.Key.Region' is the member the query grouped by.
                case MemberExpression {Expression: MemberExpression {Member.Name: "Key"} owner} part
                    when owner.Expression == root && IsGrouping(root.Type) && groupKeyParts is not null:
                    return groupKeyParts.TryGetValue(part.Member.Name, out var resolved)
                        ? resolved
                        : throw new NotSupportedException(
                            $"'{part.Member.Name}' is not one of the query's group keys.");

                case MemberExpression {Member.Name: "Key"} key
                    when key.Expression == root && IsGrouping(root.Type):
                    return groupKeyNode ?? throw new NotSupportedException("No group key in scope.");

                case MethodCallExpression aggregate
                    when IsGrouping(root.Type) && IsChainOver(aggregate, root):
                    return TranslateAggregate(aggregate);

                case MethodCallExpression joined
                    when IsGrouping(root.Type) && TryJoinText(joined, root) is { } text:
                    return text;

                // The Count property a collection carries means the same as calling Count().
                case MemberExpression {Member.Name: "Count", Expression: { } owner}
                    when IsRootedCollection(owner, root):
                    return new SubqueryNode(MemberPath((MemberExpression)owner), SubqueryFn.Count, null, null);

                // A nullable's Value is the member it wraps. Every wire operand is already optional, so
                // there is no wrapper to strip on the far side — carried as a path segment it would
                // only read as a member the server cannot find.
                case MemberExpression {Member.Name: "Value", Expression: { } optional} valued
                    when IsOptional(optional) && IsRooted(valued, root):
                    expression = optional;
                    continue;

                // HasValue asks the one thing the wire already spells as a comparison: whether the
                // member is there.
                case MemberExpression {Member.Name: "HasValue", Expression: { } asked} present
                    when IsOptional(asked) && IsRooted(present, root):
                    return new BinaryNode(BinaryOp.NotEqual, TranslateExpr(asked, root), ConstantOf(null));

                case MemberExpression member when IsKnownProperty(member, out var function):
                    return new CallNode(function, TranslateExpr(member.Expression!, root), []);

                // An attachment reached anywhere an expression is being built. A projection leaf is
                // handled before this, so arriving here means it was used as a value — compared,
                // ordered by, aggregated — and its value is the one thing no query has.
                case MemberExpression member
                    when member.Type == typeof(ScryAttachment) && IsRooted(member, root):
                    throw new NotSupportedException(
                        $"Attachment '{member.Member.Name}' is not a value: no query reads it, so it cannot be filtered, ordered, or computed on. Fetch it from the row with OpenAsync instead.");

                case MemberExpression member when IsRooted(member, root):
                    return new MemberNode(MemberPath(member));

                case MemberExpression member:
                    return ConstantOf(Evaluate(member));

                case ConstantExpression constant:
                    return ConstantOf(constant.Value);

                // The lambda parameter read as a value rather than traversed: the element of a
                // collection of values, which has no member to name. A parameter standing for a row is
                // deliberately left out, so projecting or comparing a whole row still fails here rather
                // than as a rejected request.
                case ParameterExpression parameter when parameter == root && IsValue(parameter.Type):
                    return new ElementNode();

                case MethodCallExpression call:
                    return TranslateMethod(call, root);

                default:
                    // Anything else that does not read the row is closure state — a constructed value
                    // such as new DateTime(…), an indexer, a cast — so it is evaluated into a constant.
                    if (!ReferencesParameter(expression, root))
                    {
                        return ConstantOf(Evaluate(expression));
                    }

                    throw Unsupported(expression);
            }
        }
    }

    Node TranslateMethod(MethodCallExpression call, ParameterExpression root)
    {
        var declaring = call.Method.DeclaringType;

        // Reads a value as text, whatever its type. Only the argument-less form: an overload taking a
        // format is refused, since no provider translates it — see the note on StringFrom.
        if (call is {Method.Name: "ToString", Object: { } instance, Arguments.Count: 0})
        {
            return new CallNode(KnownFunction.StringFrom, TranslateExpr(instance, root), []);
        }

        // Convert.ToString is the same read spelled statically — checked before the format refusal
        // below, whose pattern its one argument would otherwise match.
        if (call is {Method.Name: "ToString", Object: null, Arguments: [var toText]} &&
            declaring == typeof(Convert) &&
            ReferencesParameter(call, root))
        {
            return new CallNode(KnownFunction.StringFrom, TranslateExpr(toText, root), []);
        }

        if (call is {Method.Name: "ToString", Arguments.Count: > 0})
        {
            throw new NotSupportedException(
                "ToString with a format is not supported by Scry. No provider translates it, and the SQL function that would express it reads the server's language, so the same row would format differently per connection. Format the value after the query returns.");
        }

        // The three-way comparison, over the types the server compares. Only the IComparable<T> shape:
        // the object overload would hide the operand type the server reconciles the constant against.
        if (call is {Method.Name: "CompareTo", Object: { } compared, Arguments.Count: 1} &&
            declaring is not null &&
            call.Method.GetParameters()[0].ParameterType == declaring &&
            IsThreeWayComparable(declaring) &&
            ReferencesParameter(call, root))
        {
            return new CallNode(KnownFunction.CompareTo, TranslateExpr(compared, root), [TranslateExpr(call.Arguments[0], root)]);
        }

        // Equals is == spelled as a method: the same comparison, over the same operands, refused by the
        // same rules when either is not a value. The overloads taking a StringComparison are not this
        // — they ask for a case sensitivity, which the string path reads as a collation — so they are
        // left for it.
        if (call.Method.Name == "Equals" &&
            !TakesComparison(call) &&
            EqualityOperands(call) is (var equated, var against))
        {
            // One that reads nothing from the row is closure state, evaluated here as any other
            // constant expression is — the string dispatch below would otherwise reach it first and
            // refuse it for having no function to become.
            return ReferencesParameter(call, root)
                ? new BinaryNode(BinaryOp.Equal, TranslateExpr(equated, root), TranslateExpr(against, root))
                : ConstantOf(Evaluate(call));
        }

        if (declaring == typeof(string))
        {
            return TranslateStringMethod(call, root);
        }

        // GetValueOrDefault abbreviates the coalesce it stands for: the value, or — with no
        // argument — the type's default, which travels as an ordinary constant.
        if (call is {Method.Name: "GetValueOrDefault", Object: { } optional} &&
            Nullable.GetUnderlyingType(optional.Type) is { } underlying &&
            ReferencesParameter(call, root))
        {
            var fallback = call.Arguments.Count == 1
                ? TranslateExpr(call.Arguments[0], root)
                : ConstantOf(Activator.CreateInstance(underlying));
            return new BinaryNode(BinaryOp.Coalesce, TranslateExpr(optional, root), fallback);
        }

        // HasFlag reads the row's enum member; the flag travels as an ordinary enum constant, a
        // combined value spelled the way Enum.ToString spells it.
        if (call is {Method.Name: "HasFlag", Object: { } flagged} &&
            declaring == typeof(Enum) &&
            ReferencesParameter(call, root))
        {
            return new CallNode(KnownFunction.EnumHasFlag, TranslateExpr(flagged, root), [TranslateExpr(call.Arguments[0], root)]);
        }

        // Parse, and Convert's To* forms, read text as a value — the inverse of StringFrom. Only that
        // direction is carried: a numeric member is already a value, which arithmetic and comparison
        // promote without a cast, and SQL's numeric conversions truncate where the CLR's round.
        if (call is {Object: null, Arguments: [var text]} &&
            declaring is not null &&
            ReferencesParameter(call, root))
        {
            var conversion = call.Method.Name == "Parse" && parseTargets.TryGetValue(declaring, out var byType)
                ? byType
                : declaring == typeof(Convert) && convertTargets.TryGetValue(call.Method.Name, out var byName)
                    ? byName
                    : (KnownFunction?)null;

            if (conversion is { } function)
            {
                if (text.Type != typeof(string))
                {
                    throw new NotSupportedException(
                        $"'{declaring.Name}.{call.Method.Name}' reads text as a value, and '{text.Type.Name}' is already one — arithmetic and comparison promote a numeric member without a cast.");
                }

                return new CallNode(function, TranslateExpr(text, root), []);
            }
        }

        // The statics that read one temporal type as another. Each takes the value being read as its
        // argument, so the wire's target is that argument rather than an instance.
        if (IsTemporal(declaring) &&
            call is {Object: null, Arguments: [var read]} &&
            ReferencesParameter(call, root))
        {
            var conversion = call.Method.Name switch
            {
                "FromDateTime" when declaring == typeof(Date) => KnownFunction.DateOnlyFromDateTime,
                "FromDateTime" when declaring == typeof(Time) => KnownFunction.TimeOnlyFromDateTime,
                "FromTimeSpan" when declaring == typeof(Time) => KnownFunction.TimeOnlyFromTimeSpan,
                _ => (KnownFunction?)null
            };

            if (conversion is { } function)
            {
                return new CallNode(function, TranslateExpr(read, root), []);
            }
        }

        // The Unix-time readings, which are argument-less instance methods on an offset.
        if (declaring == typeof(DateTimeOffset) &&
            call is {Object: { } stamped, Arguments.Count: 0} &&
            call.Method.Name is "ToUnixTimeSeconds" or "ToUnixTimeMilliseconds")
        {
            var unix = call.Method.Name == "ToUnixTimeSeconds"
                ? KnownFunction.UnixSecondsFromOffset
                : KnownFunction.UnixMillisecondsFromOffset;
            return new CallNode(unix, TranslateExpr(stamped, root), []);
        }

        // A date and a time composed back into one timestamp.
        if (declaring == typeof(Date) &&
            call is {Method.Name: "ToDateTime", Object: { } dated, Arguments: [var timed]})
        {
            return new CallNode(KnownFunction.DateTimeFromDateAndTime, TranslateExpr(dated, root), [TranslateExpr(timed, root)]);
        }

        if (IsTemporal(declaring))
        {
            var added = call.Method.Name switch
            {
                "AddYears" => KnownFunction.DateAddYears,
                "AddMonths" => KnownFunction.DateAddMonths,
                "AddDays" => KnownFunction.DateAddDays,
                "AddHours" => KnownFunction.DateAddHours,
                "AddMinutes" => KnownFunction.DateAddMinutes,
                "AddSeconds" => KnownFunction.DateAddSeconds,
                "AddMilliseconds" => KnownFunction.DateAddMilliseconds,
                _ => throw Unsupported(call)
            };
            return new CallNode(added, TranslateExpr(call.Object!, root), [TranslateExpr(call.Arguments[0], root)]);
        }

        // The angle conversions are statics on the floating types rather than on Math, but they are
        // math functions all the same.
        if ((declaring == typeof(double) || declaring == typeof(float)) &&
            call is {
                Object: null,
                Arguments.Count: 1,
                Method.Name: "DegreesToRadians" or "RadiansToDegrees"} &&
            ReferencesParameter(call, root))
        {
            var angle = call.Method.Name == "DegreesToRadians"
                ? KnownFunction.MathDegreesToRadians
                : KnownFunction.MathRadiansToDegrees;
            return new CallNode(angle, TranslateExpr(call.Arguments[0], root), []);
        }

        if (declaring == typeof(Math))
        {
            var math = call.Method.Name switch
            {
                "Abs" => KnownFunction.MathAbs,
                "Ceiling" => KnownFunction.MathCeiling,
                "Floor" => KnownFunction.MathFloor,
                "Round" => KnownFunction.MathRound,
                "Truncate" => KnownFunction.MathTruncate,
                "Sign" => KnownFunction.MathSign,
                "Sqrt" => KnownFunction.MathSqrt,
                "Pow" => KnownFunction.MathPow,
                "Exp" => KnownFunction.MathExp,
                "Log" => KnownFunction.MathLog,
                "Log10" => KnownFunction.MathLog10,
                "Sin" => KnownFunction.MathSin,
                "Cos" => KnownFunction.MathCos,
                "Tan" => KnownFunction.MathTan,
                "Asin" => KnownFunction.MathAsin,
                "Acos" => KnownFunction.MathAcos,
                "Atan" => KnownFunction.MathAtan,
                "Atan2" => KnownFunction.MathAtan2,
                "Max" => KnownFunction.MathMax,
                "Min" => KnownFunction.MathMin,
                _ => throw Unsupported(call)
            };

            // The two-operand forms — Round(value, digits), Pow(value, exponent), Log(value, base),
            // Atan2(y, x) — carry their second operand as the one argument; the rest take none.
            var arguments = call.Arguments.Count > 1
                ? new[] { TranslateExpr(call.Arguments[1], root) }
                : [];
            return new CallNode(math, TranslateExpr(call.Arguments[0], root), arguments);
        }

        // Text and binary answer a handful of Enumerable's questions without ever yielding their
        // elements — the first character, the byte at a position, whether there are any bytes at all.
        // Checked before the collection forms below, which read a navigation rather than a scalar.
        if (TrySequenceRead(call, root) is { } sequence)
        {
            return sequence;
        }

        // _.Orders.Any(o => …) — a question about a collection navigation, which the server evaluates
        // as a correlated subquery. Checked before the set-membership form below, whose Contains reads
        // a closure collection rather than one belonging to the row.
        if (TrySubquery(call, root) is { } subquery)
        {
            return subquery;
        }

        // Query.Department.Select(_ => _.Name).Contains(_.Name) — membership of a set drawn from
        // another source, which the server resolves and policy-filters before the test.
        if (TryInSource(call, root) is { } inSource)
        {
            return inSource;
        }

        // ids.Contains(_.Id) — membership of a client-side set, which becomes a SQL IN. The set must be
        // closure state (evaluated here into constants) and the tested value must come from the row.
        if (IsSetContains(call, root, out var set, out var value))
        {
            return new CallNode(KnownFunction.In, TranslateExpr(value, root), [..SetConstants(set)]);
        }

        // A call that does not touch the parameter is a closure value — evaluate it.
        if (!ReferencesParameter(call, root))
        {
            return ConstantOf(Evaluate(call));
        }

        // The call reads the row, so it cannot be evaluated into a constant — and it is not on the
        // callable surface, so there is nothing on the wire to carry it. Named in full: this is the
        // only reporter for a query the analyzer could not see into.
        var name = declaring is null
            ? call.Method.Name
            : $"{declaring.Name}.{call.Method.Name}";
        throw new NotSupportedException(
            $"'{name}' is client-side code, which cannot be carried on the wire — the callable set is closed. Evaluate it before the query, or apply it to the rows after they return.");
    }

    Node TranslateStringMethod(MethodCallExpression call, ParameterExpression root)
    {
        Node Target() => TranslateExpr(call.Object!, root);

        Node Argument(int index) => TranslateExpr(call.Arguments[index], root);

        // The StringComparison overloads ask for a case sensitivity rather than a different operation,
        // so the target is read under it and the ordinary function applies on top. Equals also has a
        // static spelling, which puts the target in the first argument rather than in the instance.
        if (TakesComparison(call) &&
            call.Arguments.Count == (call.Object is null ? 3 : 2) &&
            call.Method.Name is "Contains" or "StartsWith" or "EndsWith" or "Equals")
        {
            var function = call.Method.Name switch
            {
                "Contains" => KnownFunction.StringContains,
                "StartsWith" => KnownFunction.StringStartsWith,
                "EndsWith" => KnownFunction.StringEndsWith,
                _ => (KnownFunction?)null
            };

            var (compared, operand) = call.Object is { } instance
                ? (instance, call.Arguments[0])
                : (call.Arguments[0], call.Arguments[1]);

            var collated = new CollateNode(TranslateExpr(compared, root), Sensitivity(call.Arguments[^1]));

            // Equals is a comparison rather than a function; under a collation it is an ordinary one.
            return function is null
                ? new BinaryNode(BinaryOp.Equal, collated, TranslateExpr(operand, root))
                : new CallNode(function.Value, collated, [TranslateExpr(operand, root)]);
        }

        switch (call.Method.Name)
        {
            case "Contains" when call.Arguments.Count == 1:
                return new CallNode(KnownFunction.StringContains, Target(), [Argument(0)]);
            case "StartsWith" when call.Arguments.Count == 1:
                return new CallNode(KnownFunction.StringStartsWith, Target(), [Argument(0)]);
            case "EndsWith" when call.Arguments.Count == 1:
                return new CallNode(KnownFunction.StringEndsWith, Target(), [Argument(0)]);
            case "ToLower" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringToLower, Target(), []);
            case "ToUpper" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringToUpper, Target(), []);
            // The static spelling of the instance CompareTo handled before this switch.
            case "Compare" when call.Arguments.Count == 2:
                return new CallNode(KnownFunction.CompareTo, Argument(0), [Argument(1)]);

            case "IsNullOrEmpty":
                return new CallNode(KnownFunction.StringIsNullOrEmpty, Argument(0), []);
            case "IsNullOrWhiteSpace":
                return new CallNode(KnownFunction.StringIsNullOrWhiteSpace, Argument(0), []);

            // The char-set overloads (Trim(params char[])) have no SQL equivalent — only the
            // whitespace-trimming forms translate.
            case "Trim" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringTrim, Target(), []);
            case "TrimStart" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringTrimStart, Target(), []);
            case "TrimEnd" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringTrimEnd, Target(), []);

            case "Substring" when call.Arguments.Count is 1 or 2:
                return new CallNode(KnownFunction.StringSubstring, Target(), [..call.Arguments.Select(_ => TranslateExpr(_, root))]);
            case "IndexOf" when call.Arguments.Count == 1:
                return new CallNode(KnownFunction.StringIndexOf, Target(), [Argument(0)]);
            case "Replace" when call.Arguments.Count == 2:
                return new CallNode(KnownFunction.StringReplace, Target(), [Argument(0), Argument(1)]);

            case "Concat":
                return ConcatChain([..ConcatArguments(call).Select(_ => TranslateExpr(_, root))]);

            // An interpolated string lowers to string.Format inside an expression tree, which no
            // provider translates. Plain holes carry no formatting, so they mean the same as a
            // concatenation and are rewritten into one.
            case "Format" when call.Arguments is [ConstantExpression {Value: string format}, ..]:
                return ConcatChain(Interpolation(format, [..ConcatArguments(call).Skip(1)], root));

            case "Format":
                throw new NotSupportedException("Only an interpolated string with a literal format is supported.");

            default:
                throw Unsupported(call);
        }
    }

    /// <summary>
    /// Translates a question asked about a collection navigation — <c>_.Orders.Any(o =&gt; …)</c>,
    /// <c>_.Orders.Count()</c>, <c>_.Orders.Sum(o =&gt; o.Total)</c> — into a subquery node, including
    /// the <c>Where(…).Count()</c> form, whose filter folds into the subquery's own predicate. Returns
    /// null when the call is not one of these, so the caller can go on trying other forms.
    /// </summary>
    SubqueryNode? TrySubquery(MethodCallExpression call, ParameterExpression root)
    {
        // Contains over a collection the row holds asks whether any element equals the value — the
        // same question Any answers, and the only way to ask it of a collection of values, whose
        // elements have no member to compare.
        if (call.Method.Name == "Contains" &&
            TryContainsOverCollection(call, root) is { } contains)
        {
            return contains;
        }

        var function = call.Method.Name switch
        {
            "Any" => SubqueryFn.Any,
            "All" => SubqueryFn.All,
            "Count" => SubqueryFn.Count,
            "Sum" => SubqueryFn.Sum,
            "Average" => SubqueryFn.Average,
            "Min" => SubqueryFn.Min,
            "Max" => SubqueryFn.Max,
            _ => (SubqueryFn?)null
        };

        if (function is null ||
            call.Arguments.Count == 0)
        {
            return null;
        }

        // A preceding Where over the same collection contributes the subquery's predicate.
        var source = call.Arguments[0];
        Expression? filter = null;
        if (source is MethodCallExpression {Method.Name: "Where", Arguments.Count: 2} where)
        {
            source = where.Arguments[0];
            filter = where.Arguments[1];
        }

        if (!IsRootedCollection(source, root))
        {
            return null;
        }

        var path = MemberPath((MemberExpression)source);
        var argument = call.Arguments.Count > 1 ? Lambda(call.Arguments[1]) : null;
        var predicate = filter is null ? null : Lambda(filter);

        // Any/All/Count take a predicate; the rest take a value selector.
        Node? selector = null;
        if (argument is not null)
        {
            if (function is SubqueryFn.Any or SubqueryFn.All or SubqueryFn.Count)
            {
                predicate = predicate is null
                    ? argument
                    : throw new NotSupportedException(
                        $"'{call.Method.Name}' over a collection cannot combine a Where with its own predicate.");
            }
            else
            {
                selector = TranslateExpr(argument.Body, argument.Parameters[0]);
            }
        }
        else if (function is SubqueryFn.Sum or SubqueryFn.Average or SubqueryFn.Min or SubqueryFn.Max)
        {
            // Sum() and friends without a selector fold the elements themselves, which only a
            // collection of values can be asked for — there is nothing else there to read.
            selector = new ElementNode();
        }

        return new(
            path,
            function.Value,
            predicate is null ? null : TranslateExpr(predicate.Body, predicate.Parameters[0]),
            selector);
    }

    /// <summary>
    /// Translates <c>_.Tags.Contains("urgent")</c> — membership of a collection belonging to the row —
    /// into an <c>Any</c> over its elements. Both the instance form (<c>List&lt;T&gt;.Contains</c>) and
    /// the static form (<c>Enumerable.Contains</c>, which is what the generated model's
    /// <c>IReadOnlyList&lt;T&gt;</c> binds to) arrive here. Returns null for the closure-set form,
    /// <c>ids.Contains(_.Id)</c>, which is a different question and becomes a SQL <c>IN</c>.
    /// </summary>
    SubqueryNode? TryContainsOverCollection(MethodCallExpression call, ParameterExpression root)
    {
        Expression source;
        Expression argument;
        if (call.Object is null)
        {
            if (call.Arguments.Count != 2)
            {
                return null;
            }

            source = call.Arguments[0];
            argument = call.Arguments[1];
        }
        else
        {
            if (call.Arguments.Count != 1)
            {
                return null;
            }

            source = call.Object;
            argument = call.Arguments[0];
        }

        if (!IsRootedCollection(source, root))
        {
            return null;
        }

        var value = TranslateExpr(argument, root);
        if (value is not ConstNode)
        {
            throw new NotSupportedException(
                """
                Contains over a collection the row holds takes a constant.
                The test is evaluated against the collection's elements, and the row that owns them is not in scope there.
                """);
        }

        return new(
            MemberPath((MemberExpression) source),
            SubqueryFn.Any,
            new BinaryNode(BinaryOp.Equal, new ElementNode(), value),
            null);
    }

    /// <summary>
    /// Whether a type is one of the values a query reads directly, rather than a row whose members it
    /// names. Mirrors the server's <c>Schema.IsScalar</c>: the two only have to agree about what makes
    /// the lambda parameter itself readable, and a disagreement costs a rejected request rather than
    /// anything worse.
    /// </summary>
    static bool IsValue(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive ||
               underlying.IsEnum ||
               underlying == typeof(string) ||
               underlying == typeof(decimal) ||
               underlying == typeof(DateTime) ||
               underlying == typeof(Date) ||
               underlying == typeof(Time) ||
               underlying == typeof(DateTimeOffset) ||
               underlying == typeof(TimeSpan) ||
               underlying == typeof(Guid) ||
               underlying == typeof(byte[]);
    }

    // A member path rooted at the query parameter whose value is a collection — the shape the
    // generated model gives a collection navigation.
    static bool IsRootedCollection(Expression expression, ParameterExpression root) =>
        expression is MemberExpression member &&
        IsRooted(member, root) &&
        member.Type != typeof(string) &&
        member.Type.GetInterfaces()
            .Any(_ => _.IsGenericType && _.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    /// <summary>
    /// Reads the case sensitivity a <see cref="StringComparison"/> asks for. Only that much of it
    /// survives: the comparison the database then makes is its own, under the collation the server
    /// configured, which is not the culture rules the .NET value names.
    /// </summary>
    static StringMatch Sensitivity(Expression comparison)
    {
        if (Evaluate(comparison) is not StringComparison value)
        {
            throw new NotSupportedException("A string comparison mode must be a constant.");
        }

        return value switch
        {
            StringComparison.Ordinal or
                StringComparison.CurrentCulture or
                StringComparison.InvariantCulture => StringMatch.CaseSensitive,
            StringComparison.OrdinalIgnoreCase or
                StringComparison.CurrentCultureIgnoreCase or
                StringComparison.InvariantCultureIgnoreCase => StringMatch.CaseInsensitive,
            _ => throw new NotSupportedException($"String comparison '{value}' is not supported by Scry.")
        };
    }

    // The params overloads pass their arguments as a single constructed array.
    static IReadOnlyList<Expression> ConcatArguments(MethodCallExpression call)
    {
        if (call.Arguments is [NewArrayExpression array])
        {
            return array.Expressions;
        }

        return (ReadOnlyCollection<Expression>) [.. call.Arguments];
    }

    /// <summary>
    /// Splits a format string into its literal runs and holes. A hole carrying alignment or a format
    /// specifier is refused: it would change the value, and the database has no equivalent spelling.
    /// </summary>
    List<Node> Interpolation(string format, IReadOnlyList<Expression> arguments, ParameterExpression root)
    {
        var parts = new List<Node>();
        var literal = new StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            var character = format[i];

            // Doubled braces are an escaped literal brace.
            if (character is '{' or '}' &&
                i + 1 < format.Length &&
                format[i + 1] == character)
            {
                literal.Append(character);
                i++;
                continue;
            }

            if (character != '{')
            {
                literal.Append(character);
                continue;
            }

            var close = format.IndexOf('}', i);
            var hole = close < 0 ? "" : format[(i + 1)..close];
            if (close < 0 ||
                !int.TryParse(hole, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                index >= arguments.Count)
            {
                throw new NotSupportedException(
                    "An interpolated string may only contain plain holes — alignment and format specifiers are not supported.");
            }

            if (literal.Length > 0)
            {
                parts.Add(ConstantOf(literal.ToString()));
                literal.Clear();
            }

            parts.Add(TranslateExpr(arguments[index], root));
            i = close;
        }

        if (literal.Length > 0)
        {
            parts.Add(ConstantOf(literal.ToString()));
        }

        return parts;
    }

    static Node ConcatChain(IReadOnlyList<Node> parts)
    {
        if (parts.Count == 0)
        {
            throw new NotSupportedException("A concatenation must have at least one part.");
        }

        var chain = parts[0];
        foreach (var part in parts.Skip(1))
        {
            chain = new CallNode(KnownFunction.StringConcat, chain, [part]);
        }

        return chain;
    }

    /// <summary>
    /// Translates membership of a set drawn from another Scry source — the candidates come from a
    /// captured query rather than from closure state, so they are named rather than evaluated. Returns
    /// null when the call is not that, so the caller can go on trying the client-side set form.
    /// </summary>
    InSourceNode? TryInSource(MethodCallExpression call, ParameterExpression root)
    {
        if (call.Method.Name != "Contains" ||
            !IsSetContains(call, root, out var set, out var value))
        {
            return null;
        }

        if (Evaluate(set) is not IQueryable {Provider: QueryProvider provider} queryable)
        {
            return null;
        }

        // Walked directly rather than run through the operator translator: the Select here names a
        // bare value to compare against, where an ordinary projection must construct an object.
        Node? predicate = null;
        Node? selector = null;
        var current = queryable.Expression;
        while (current is MethodCallExpression inner)
        {
            switch (inner.Method.Name)
            {
                case "Select" when selector is null && inner.Arguments.Count == 2:
                    var projection = Lambda(inner.Arguments[1]);
                    selector = TranslateExpr(projection.Body, projection.Parameters[0]);
                    break;

                case "Where" when inner.Arguments.Count == 2:
                    var filter = Lambda(inner.Arguments[1]);
                    var clause = TranslateExpr(filter.Body, filter.Parameters[0]);
                    predicate = predicate is null
                        ? clause
                        : new BinaryNode(BinaryOp.AndAlso, clause, predicate);
                    break;

                default:
                    throw new NotSupportedException(
                        "The source of a membership test may only carry a Where and a Select of one value.");
            }

            current = inner.Arguments[0];
        }

        if (selector is null)
        {
            throw new NotSupportedException(
                "A membership test against another source must Select the single value to compare against.");
        }

        return new(TranslateExpr(value, root), provider.Root, selector, predicate);
    }

    // The receiver holds the candidate set and must be closure state; the tested value must read from
    // the row. Both the instance form (List.Contains) and the static form (Enumerable.Contains) map here.
    static bool IsSetContains(
        MethodCallExpression call,
        ParameterExpression root,
        [NotNullWhen(true)] out Expression? set,
        [NotNullWhen(true)] out Expression? value)
    {
        set = null;
        value = null;

        if (call.Method.Name != "Contains")
        {
            return false;
        }

        if (call is {Object: { } instance, Arguments.Count: 1})
        {
            (set, value) = (instance, call.Arguments[0]);
        }
        else if (call is {Object: null, Arguments.Count: 2})
        {
            (set, value) = (call.Arguments[0], call.Arguments[1]);
        }
        else
        {
            return false;
        }

        set = UnwrapSet(set);

        return !ReferencesParameter(set, root) &&
               ReferencesParameter(value, root);
    }

    /// <summary>
    /// Reads through to the collection a set expression really denotes. An array receiver binds to
    /// <c>MemoryExtensions.Contains</c>, so what arrives is a <c>ReadOnlySpan</c> produced by a
    /// conversion or an <c>AsSpan</c> call — a ref struct, which cannot be returned from the compiled
    /// lambda the values are read with. The array behind it can.
    /// </summary>
    static Expression UnwrapSet(Expression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} convert:
                    expression = convert.Operand;
                    continue;
                case MethodCallExpression {Object: null, Arguments.Count: 1, Type.IsByRefLike: true} call:
                    expression = call.Arguments[0];
                    continue;
                case MethodCallExpression {Object: { } instance, Arguments.Count: 0, Type.IsByRefLike: true}:
                    expression = instance;
                    continue;
                default:
                    return expression;
            }
        }
    }

    static IEnumerable<Node> SetConstants(Expression set)
    {
        if (Evaluate(set) is IEnumerable values)
        {
            return values.Cast<object?>().Select(ConstantOf);
        }

        throw new NotSupportedException("The Contains set must be a collection of values.");
    }

    // The Parse owners and Convert members the text-reading functions answer for, by target type.
    static readonly Dictionary<Type, KnownFunction> parseTargets = new()
    {
        [typeof(int)] = KnownFunction.Int32From,
        [typeof(long)] = KnownFunction.Int64From,
        [typeof(decimal)] = KnownFunction.DecimalFrom,
        [typeof(double)] = KnownFunction.DoubleFrom,
        [typeof(bool)] = KnownFunction.BooleanFrom,
        [typeof(byte)] = KnownFunction.ByteFrom,
        [typeof(short)] = KnownFunction.Int16From,
        [typeof(float)] = KnownFunction.SingleFrom
    };

    // ToSingle is deliberately absent: the provider translates float.Parse but has no ToSingle
    // conversion, so carrying the spelling would trade a translation-time refusal for an execution
    // fault.
    static readonly Dictionary<string, KnownFunction> convertTargets = new(StringComparer.Ordinal)
    {
        ["ToInt32"] = KnownFunction.Int32From,
        ["ToInt64"] = KnownFunction.Int64From,
        ["ToDecimal"] = KnownFunction.DecimalFrom,
        ["ToDouble"] = KnownFunction.DoubleFrom,
        ["ToBoolean"] = KnownFunction.BooleanFrom,
        ["ToByte"] = KnownFunction.ByteFrom,
        ["ToInt16"] = KnownFunction.Int16From
    };

    static bool IsGrouping(Type type) =>
        type.IsGenericType &&
        type.GetGenericTypeDefinition() == typeof(IGrouping<,>);

    // A call applied to the group itself — g.Count(), g.Sum(x => …) — rather than to something read
    // out of it, such as g.Key.ToUpper(), which is an ordinary function over the key.
    static bool IsCallOver(MethodCallExpression call, ParameterExpression target) =>
        call.Object == target ||
        (call.Arguments.Count > 0 && call.Arguments[0] == target);

    // The same question through the Where/Select/Distinct chain a composed aggregate may put between
    // the fold and the group. A chain bottoming at anything else — g.Key, a closure — is not the
    // group being folded.
    static bool IsChainOver(MethodCallExpression call, ParameterExpression target)
    {
        Expression? current = call;
        while (current is MethodCallExpression inner)
        {
            current = inner.Object ?? (inner.Arguments.Count > 0 ? inner.Arguments[0] : null);
        }

        return current == target;
    }

    static bool IsTemporal(Type? type) =>
        type == typeof(DateTime) ||
        type == typeof(Date) ||
        type == typeof(DateTimeOffset) ||
        type == typeof(Time);

    /// <summary>
    /// The questions a string or a binary member answers as a sequence. Both are scalars on the wire —
    /// neither ever yields its elements — so each of these folds to a single value, and the ones with
    /// no such folding (any predicate overload, anything returning a sequence) are left to fail as the
    /// client-side code they are.
    /// </summary>
    Node? TrySequenceRead(MethodCallExpression call, ParameterExpression root)
    {
        // MemoryExtensions sits alongside Enumerable here because the compiler prefers its span
        // overload for a byte[]'s Contains. The two spell one question, and the server rebinds either
        // onto the Enumerable form the provider translates.
        if (call is not {Object: null, Arguments: [var source, ..]} ||
            (call.Method.DeclaringType != typeof(Enumerable) &&
             call.Method.DeclaringType != typeof(MemoryExtensions)) ||
            !ReferencesParameter(source, root))
        {
            return null;
        }

        // MemoryExtensions takes a span, so the array reaches it through a conversion — spelled as a
        // call to the implicit operator rather than as a Convert node, since a ref struct is not a
        // type the tree can convert to on its own. The question is about what was converted, and the
        // wire carries that member rather than the span.
        source = Unconverted(source);

        if (source.Type == typeof(string))
        {
            var text = call.Method.Name switch
            {
                "FirstOrDefault" when call.Arguments.Count == 1 => KnownFunction.StringFirst,
                "LastOrDefault" when call.Arguments.Count == 1 => KnownFunction.StringLast,
                _ => (KnownFunction?)null
            };

            return text is { } reading
                ? new CallNode(reading, TranslateExpr(source, root), [])
                : null;
        }

        if (source.Type != typeof(byte[]))
        {
            return null;
        }

        // First is ElementAt at position zero, so it travels as that rather than as a function of its
        // own — the same unfolding the terminals use for ElementAtAsync.
        return call.Method.Name switch
        {
            "First" when call.Arguments.Count == 1 =>
                new CallNode(KnownFunction.BytesElementAt, TranslateExpr(source, root), [ConstantOf(0)]),
            "ElementAt" when call.Arguments is [_, var at] =>
                new CallNode(KnownFunction.BytesElementAt, TranslateExpr(source, root), [TranslateExpr(at, root)]),
            "Contains" when call.Arguments is [_, var value] =>
                new CallNode(KnownFunction.BytesContains, TranslateExpr(source, root), [TranslateExpr(value, root)]),
            _ => null
        };
    }

    // The value under any conversions wrapping it, whichever way the compiler spelled them: a Convert
    // node, or a call to a conversion operator where the target is a type the tree cannot convert to.
    static Expression Unconverted(Expression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} converted:
                    expression = converted.Operand;
                    continue;

                case MethodCallExpression
                {
                    Object: null,
                    Method.Name: "op_Implicit" or "op_Explicit",
                    Arguments: [var operand]
                }:
                    expression = operand;
                    continue;

                default:
                    return expression;
            }
        }
    }

    // Whether the expression is an optional value, whose Value and HasValue members are carried as the
    // member itself and as a comparison against null.
    static bool IsOptional(Expression expression) =>
        Nullable.GetUnderlyingType(expression.Type) is not null;

    // Whether the call's last argument names a case sensitivity rather than an operand.
    static bool TakesComparison(MethodCallExpression call) =>
        call.Arguments.Count > 0 &&
        call.Arguments[^1].Type == typeof(StringComparison);

    // The two operands of an Equals that means ==: the instance and its one argument, or the two
    // arguments of the static spelling. Any other shape is an overload the set does not carry.
    static (Expression Left, Expression Right)? EqualityOperands(MethodCallExpression call) =>
        call switch
        {
            {Object: { } instance, Arguments: [var argument]} => (instance, argument),
            {Object: null, Arguments: [var first, var second]} => (first, second),
            _ => null
        };

    // The types the server compares three ways: numbers, text, and dates. Mirrors the server's own
    // allow-list, so an unsupported target refuses at translation rather than as a rejected request.
    // Enums are excluded by hand: their type code reports the underlying number's.
    static bool IsThreeWayComparable(Type type) =>
        type == typeof(string) ||
        IsTemporal(type) ||
        (!type.IsEnum &&
         Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte
             or TypeCode.Int16 or TypeCode.UInt16
             or TypeCode.Int32 or TypeCode.UInt32
             or TypeCode.Int64 or TypeCode.UInt64
             or TypeCode.Single or TypeCode.Double or TypeCode.Decimal);

    // A property that reads as a function rather than a member path: a date part, or string length.
    static bool IsKnownProperty(MemberExpression member, out KnownFunction function)
    {
        var declaring = member.Member.DeclaringType;
        if (member.Expression is not null)
        {
            if (IsTemporal(declaring))
            {
                switch (member.Member.Name)
                {
                    case "Year":
                        function = KnownFunction.DateYear;
                        return true;
                    case "Month":
                        function = KnownFunction.DateMonth;
                        return true;
                    case "Day":
                        function = KnownFunction.DateDay;
                        return true;
                    case "Hour":
                        function = KnownFunction.DateHour;
                        return true;
                    case "Minute":
                        function = KnownFunction.DateMinute;
                        return true;
                    case "Second":
                        function = KnownFunction.DateSecond;
                        return true;
                    case "Millisecond":
                        function = KnownFunction.DateMillisecond;
                        return true;
                    case "DayOfYear":
                        function = KnownFunction.DateDayOfYear;
                        return true;
                    case "Microsecond":
                        function = KnownFunction.DateMicrosecond;
                        return true;
                    case "Nanosecond":
                        function = KnownFunction.DateNanosecond;
                        return true;
                    case "DayNumber":
                        function = KnownFunction.DateDayNumber;
                        return true;
                    case "TimeOfDay":
                        function = KnownFunction.DateTimeOfDay;
                        return true;
                    case "DayOfWeek":
                        function = KnownFunction.DateDayOfWeek;
                        return true;
                    case "Date":
                        function = KnownFunction.DateDate;
                        return true;
                }
            }

            // An elapsed time's parts are spelled in the plural — Hours, not Hour — which is what tells
            // them apart from a date's, and is why they are read here rather than alongside them. The
            // Total* readings are absent on purpose: each is a division rather than a part, and no
            // provider translates one.
            if (declaring == typeof(TimeSpan))
            {
                switch (member.Member.Name)
                {
                    case "Hours":
                        function = KnownFunction.TimeSpanHours;
                        return true;
                    case "Minutes":
                        function = KnownFunction.TimeSpanMinutes;
                        return true;
                    case "Seconds":
                        function = KnownFunction.TimeSpanSeconds;
                        return true;
                    case "Milliseconds":
                        function = KnownFunction.TimeSpanMilliseconds;
                        return true;
                    case "Microseconds":
                        function = KnownFunction.TimeSpanMicroseconds;
                        return true;
                    case "Nanoseconds":
                        function = KnownFunction.TimeSpanNanoseconds;
                        return true;
                }
            }

            if (declaring == typeof(string) &&
                member.Member.Name == "Length")
            {
                function = KnownFunction.StringLength;
                return true;
            }
        }

        function = default;
        return false;
    }

    static bool IsRooted(MemberExpression member, ParameterExpression root)
    {
        Expression? current = member;
        while (current is MemberExpression inner)
        {
            current = inner.Expression;
        }

        return current == root;
    }

    static List<string> MemberPath(MemberExpression member)
    {
        var path = new List<string>();
        Expression? current = member;
        while (current is MemberExpression inner)
        {
            path.Add(inner.Member.Name);
            current = inner.Expression;
        }

        path.Reverse();
        return path;
    }

    static bool ReferencesParameter(Expression expression, ParameterExpression parameter) =>
        new ParameterFinder(parameter).Found(expression);

    static int IntArgument(Expression expression) =>
        (int)Convert.ChangeType(Evaluate(expression)!, typeof(int), CultureInfo.InvariantCulture);

    static object? Evaluate(Expression expression) =>
        Expression.Lambda(expression).Compile().DynamicInvoke();

    static LambdaExpression Lambda(Expression expression) =>
        expression switch
        {
            UnaryExpression
            {
                Operand: LambdaExpression lambda
            } => lambda,
            LambdaExpression lambda => lambda,
            _ => throw new NotSupportedException("Expected a lambda expression.")
        };

    static string[] ProjectionNames(NewExpression construction)
    {
        if (construction.Members is { } members)
        {
            return members.Select(_ => _.Name).ToArray();
        }

        if (construction.Constructor is { } constructor)
        {
            return constructor.GetParameters()
                .Select(_ => Capitalize(_.Name!))
                .ToArray();
        }

        throw new NotSupportedException("Cannot determine projection member names.");
    }

    static string Capitalize(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    static BinaryOp MapBinary(ExpressionType type) =>
        type switch
        {
            ExpressionType.Equal => BinaryOp.Equal,
            ExpressionType.NotEqual => BinaryOp.NotEqual,
            ExpressionType.LessThan => BinaryOp.LessThan,
            ExpressionType.LessThanOrEqual => BinaryOp.LessThanOrEqual,
            ExpressionType.GreaterThan => BinaryOp.GreaterThan,
            ExpressionType.GreaterThanOrEqual => BinaryOp.GreaterThanOrEqual,
            ExpressionType.AndAlso => BinaryOp.AndAlso,
            ExpressionType.OrElse => BinaryOp.OrElse,
            ExpressionType.Add => BinaryOp.Add,
            ExpressionType.Subtract => BinaryOp.Subtract,
            ExpressionType.Multiply => BinaryOp.Multiply,
            ExpressionType.Divide => BinaryOp.Divide,
            ExpressionType.Modulo => BinaryOp.Modulo,
            ExpressionType.Coalesce => BinaryOp.Coalesce,
            _ => throw new NotSupportedException($"Binary operator '{type}' is not supported.")
        };

    static ConstNode ConstantOf(object? value)
    {
        var (text, tag) = ValueTag.Of(value);
        return new(text, tag);
    }

    static NotSupportedException Unsupported(Expression expression) =>
        new($"Expression '{expression.NodeType}' is not supported by Scry.");

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

    sealed class ParameterFinder(ParameterExpression target) :
        ExpressionVisitor
    {
        bool found;

        public bool Found(Expression expression)
        {
            found = false;
            Visit(expression);
            return found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == target)
            {
                found = true;
            }

            return base.VisitParameter(node);
        }
    }
}
