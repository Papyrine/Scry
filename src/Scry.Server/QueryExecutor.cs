/// <summary>
/// Orchestrates the full server pipeline for one request: validate against the allow-list, resolve
/// the source, apply the row policy, rebind the AST to an EF query, execute, and shape the result.
/// </summary>
sealed class QueryExecutor(Schema schema, ScryOptions options)
{
    QueryValidator validator = new(schema, options);

    public QueryResponse Execute(QueryRequest request, DbContext db, IServiceProvider services)
    {
        var source = validator.Validate(request);
        var elementType = source.ClrType;

        // Built per request so a node that reads another source resolves it the same way the root was
        // resolved — through the schema, and policy-filtered — rather than reaching a DbSet directly.
        var builder = new ExpressionBuilder(schema, options, name => ResolveSource(name, db, services));

        var query = source.Resolve(db, services);
        query = ApplyPolicy(query, source, db, services);

        GroupByOp? groupBy = null;
        SelectOp? select = null;
        QueryOp? terminal = null;
        Node? groupFilter = null;
        JoinOp? join = null;
        // Captured alongside inline application so the page terminal can rebuild the ordering into a
        // total order (append the primary key) and a keyset seek predicate.
        List<(Node Key, bool Descending)> orderings = [];
        // A cursor can only append its primary-key tiebreak when the ordering is the trailing restricting
        // op (so the query is still IOrderedQueryable) and no offset (Skip/Take) is in play.
        var tailIsOrdered = false;
        var sawSkipOrTake = false;
        // Distinct deduplicates the projected rows, so it is applied after the projection is built
        // rather than inline with the operators over the entity query. Ordering and paging written
        // after it describe those deduplicated values and are held back with it.
        var distinct = false;
        List<QueryOp> afterDistinct = [];

        foreach (var op in request.Pipeline)
        {
            switch (op)
            {
                // A Where after GroupBy filters the groups, not the rows, so it cannot be applied to the
                // entity query — it waits for the grouping to be built. Several of them conjoin.
                case WhereOp having when groupBy is not null:
                    if (groupFilter is null)
                    {
                        groupFilter = having.Predicate;
                    }
                    else
                    {
                        groupFilter = new BinaryNode(BinaryOp.AndAlso, groupFilter, having.Predicate);
                    }
                    break;
                case WhereOp where:
                    tailIsOrdered = false;
                    query = Apply(query, "Where", builder.BuildPredicate(where.Predicate, elementType));
                    break;
                case ReverseOp:
                    // The declared ordering no longer describes the rows, so a keyset cursor cannot
                    // seek over it; paging falls back to offset.
                    tailIsOrdered = false;
                    query = query.Provider.CreateQuery(
                        CallQueryable("Reverse", [query.ElementType], query.Expression));
                    break;
                // Over a deduplicated query the ordering describes the projected values, so it is held
                // back and applied to those rather than to the rows that fed them.
                case OrderByOp deduplicated when distinct:
                    afterDistinct.Add(deduplicated);
                    break;
                case SkipOp or TakeOp when distinct:
                    afterDistinct.Add(op);
                    break;
                case OrderByOp orderBy:
                    orderings.Add((orderBy.Key, orderBy.Descending));
                    tailIsOrdered = true;
                    query = ApplyOrder(query, builder.BuildKeySelector(orderBy.Key, elementType), orderBy.Descending, then: false);
                    break;
                case ThenByOp thenBy:
                    orderings.Add((thenBy.Key, thenBy.Descending));
                    tailIsOrdered = true;
                    query = ApplyOrder(query, builder.BuildKeySelector(thenBy.Key, elementType), thenBy.Descending, then: true);
                    break;
                case SkipOp skip:
                    sawSkipOrTake = true;
                    tailIsOrdered = false;
                    query = ApplyPaging(query, "Skip", skip.Count);
                    break;
                case TakeOp take:
                    sawSkipOrTake = true;
                    tailIsOrdered = false;
                    query = ApplyPaging(query, "Take", take.Count);
                    break;
                case DistinctOp:
                    distinct = true;
                    break;
                case JoinOp joined:
                    join = joined;
                    break;
                case GroupByOp group:
                    groupBy = group;
                    break;
                case SelectOp projection:
                    select = projection;
                    break;
                default:
                    terminal = op;
                    break;
            }
        }

        // Every terminal predicate narrows the rows before anything else the terminal does, so they are
        // all applied here rather than each terminal repeating it.
        if (TerminalPredicate(terminal) is { } predicate)
        {
            query = Apply(query, "Where", builder.BuildPredicate(predicate, elementType));
        }

        if (terminal is PageOp page)
        {
            return Page(builder, query, elementType, select, orderings, tailIsOrdered && !sawSkipOrTake, page, source, db);
        }

        // A join projects straight to the shaped row, so it replaces the projection step entirely and
        // the folding terminals below fold the joined rows.
        if (join is not null)
        {
            var (joined, joinPlan) = BuildJoined(builder, query, elementType, join, db, services);

            return terminal switch
            {
                CountOp => Scalar(Execute<int>(joined, "Count")),
                LongCountOp => Scalar(Execute<long>(joined, "LongCount")),
                AnyOp => Scalar(Execute<bool>(joined, "Any")),
                FirstOp firstRow => Single(ExecuteRow(joined, firstRow.OrDefault ? "FirstOrDefault" : "First"), joinPlan),
                SingleOp singleRow => Single(ExecuteRow(joined, singleRow.OrDefault ? "SingleOrDefault" : "Single"), joinPlan),
                _ => QueryResponse.Create(
                    ResultKind.List,
                    JsonSerializer.SerializeToElement(
                        joined.ToList().Select(_ => Shape(_, joinPlan)).ToArray(),
                        ScryJson.Options))
            };
        }

        // Ordering, paging and folding all need the deduplicated rows to have equality and ordering of
        // their own, which a shaped object[] does not. Those go through a row type instead; plain
        // enumeration keeps the object[] path below, which has no arity limit.
        if (distinct &&
            (terminal is CountOp or LongCountOp or AnyOp || afterDistinct.Count > 0))
        {
            if (builder.BuildDistinctRow(select!.Projection, elementType) is not var (selector, shape))
            {
                throw new ScryValidationException(
                    $"A Distinct query of more than {DistinctRow.ByArity.Length} projected members can only be enumerated.");
            }

            var rowType = selector.ReturnType;
            var deduped = ApplySelectTyped(query, selector).Provider.CreateQuery(
                CallQueryable("Distinct", [rowType], ApplySelectTyped(query, selector).Expression));

            foreach (var op in afterDistinct)
            {
                deduped = op switch
                {
                    OrderByOp order => ApplyOrder(
                        deduped,
                        ExpressionBuilder.BuildDistinctRowKey(rowType, DistinctLeaf(select.Projection, order.Key)),
                        order.Descending,
                        then: false),
                    SkipOp skip => ApplyPaging(deduped, "Skip", skip.Count),
                    TakeOp take => ApplyPaging(deduped, "Take", take.Count),
                    _ => throw new ScryValidationException($"Unsupported operator '{op.GetType().Name}' after Distinct.")
                };
            }

            switch (terminal)
            {
                case CountOp:
                    return Scalar(Execute<int>(deduped, "Count"));
                case LongCountOp:
                    return Scalar(Execute<long>(deduped, "LongCount"));
                case AnyOp:
                    return Scalar(Execute<bool>(deduped, "Any"));
            }

            var rowPlan = new ProjectionPlan(selector, shape);
            var shaped = new List<Dictionary<string, object?>>();
            foreach (var row in deduped)
            {
                shaped.Add(Shape(ExpressionBuilder.ReadDistinctRow(row!, shape.Count), rowPlan));
            }

            return QueryResponse.Create(ResultKind.List, JsonSerializer.SerializeToElement(shaped, ScryJson.Options));
        }

        // Every other folding terminal reads the rows themselves and projects nothing. None of them can
        // co-occur with a Distinct: the validator allows only the three folded above.
        switch (terminal)
        {
            case CountOp:
                return Scalar(Execute<int>(query, "Count"));
            case LongCountOp:
                return Scalar(Execute<long>(query, "LongCount"));
            case AnyOp:
                return Scalar(Execute<bool>(query, "Any"));
            case AllOp all:
                return Scalar(
                    Execute<bool>(query, "All", Expression.Quote(builder.BuildPredicate(all.Predicate, elementType))));
            case AggregateOp aggregate:
                return Aggregate(builder, query, aggregate, elementType);
        }

        var (projected, plan) = BuildProjected(builder, query, elementType, groupBy, select, groupFilter);

        if (distinct)
        {
            projected = ApplyDistinct(projected, source.Kind);
        }

        if (terminal is FirstOp first)
        {
            var row = ExecuteRow(projected, first.OrDefault ? "FirstOrDefault" : "First");
            return Single(row, plan);
        }

        if (terminal is SingleOp single)
        {
            var row = ExecuteRow(projected, single.OrDefault ? "SingleOrDefault" : "Single");
            return Single(row, plan);
        }

        if (terminal is LastOp last)
        {
            var row = ExecuteRow(projected, last.OrDefault ? "LastOrDefault" : "Last");
            return Single(row, plan);
        }

        var rows = projected.ToList();
        var array = rows.Select(_ => Shape(_, plan)).ToArray();
        return QueryResponse.Create(ResultKind.List, JsonSerializer.SerializeToElement(array, ScryJson.Options));
    }

    /// <summary>
    /// Resolves the joined source and combines it with the outer query. The inner source goes through
    /// the same resolution and <b>row policy</b> the outer did, before the two meet — so a join can
    /// only narrow, and no row hidden from a direct query of the inner source is observable through one.
    /// </summary>
    (IQueryable<object[]> Query, ProjectionPlan Plan) BuildJoined(
        ExpressionBuilder builder,
        IQueryable outer,
        Type outerType,
        JoinOp join,
        DbContext db,
        IServiceProvider services)
    {
        if (!schema.TryGetSource(join.Root, out var innerSource))
        {
            throw new ScryValidationException($"Unknown source '{join.Root}'.");
        }

        var innerType = innerSource.ClrType;
        var inner = ApplyPolicy(innerSource.Resolve(db, services), innerSource, db, services);

        if (join.InnerPredicate is { } predicate)
        {
            inner = Apply(inner, "Where", builder.BuildPredicate(predicate, innerType));
        }

        var (outerKey, innerKey) = builder.BuildJoinKeys(join.OuterKey, outerType, join.InnerKey, innerType);
        var (selector, shape) = builder.BuildJoinProjection(join.Result, outerType, innerType, join.Kind);

        var joined = (IQueryable<object[]>)outer.Provider.CreateQuery(
            CallQueryable(
                join.Kind == JoinKind.Left ? "LeftJoin" : "Join",
                [outerType, innerType, outerKey.ReturnType, typeof(object[])],
                outer.Expression,
                inner.Expression,
                Expression.Quote(outerKey),
                Expression.Quote(innerKey),
                Expression.Quote(selector)));

        return (joined, new(selector, shape));
    }

    static Node? TerminalPredicate(QueryOp? terminal) =>
        terminal switch
        {
            CountOp count => count.Predicate,
            LongCountOp longCount => longCount.Predicate,
            AnyOp any => any.Predicate,
            FirstOp first => first.Predicate,
            SingleOp single => single.Predicate,
            LastOp last => last.Predicate,
            _ => null
        };

    /// <summary>
    /// Folds the whole sequence to one scalar. The selected values are projected first so the provider
    /// picks its aggregate overload from the value type, which <see cref="ExpressionBuilder"/> has
    /// already widened to one that exists.
    /// </summary>
    static QueryResponse Aggregate(ExpressionBuilder builder, IQueryable query, AggregateOp aggregate, Type elementType)
    {
        var selector = builder.BuildAggregateSelector(aggregate.Selector, elementType, aggregate.Function);
        var values = ApplySelectTyped(query, selector);
        var name = aggregate.Function.ToString();

        // Min/Max are generic in the value type; Sum/Average have one overload per numeric type.
        MethodCallExpression call;
        if (aggregate.Function is AggregateFn.Min or AggregateFn.Max)
        {
            call = Expression.Call(typeof(Queryable), name, [values.ElementType], values.Expression);
        }
        else
        {
            call = Expression.Call(typeof(Queryable), name, null, values.Expression);
        }

        return Scalar(values.Provider.Execute(call));
    }

    IQueryable ResolveSource(string name, DbContext db, IServiceProvider services)
    {
        if (!schema.TryGetSource(name, out var source))
        {
            throw new ScryValidationException($"Unknown source '{name}'.");
        }

        return ApplyPolicy(source.Resolve(db, services), source, db, services);
    }

    static (IQueryable<object[]> Query, ProjectionPlan Plan) BuildProjected(
        ExpressionBuilder builder,
        IQueryable query,
        Type elementType,
        GroupByOp? groupBy,
        SelectOp? select,
        Node? groupFilter)
    {
        if (groupBy is not null)
        {
            var keySelector = builder.BuildKeySelector(groupBy.Keys[0], elementType);
            var keyType = keySelector.ReturnType;
            var grouped = ApplyGroupBy(query, keySelector, elementType, keyType);
            if (groupFilter is not null)
            {
                grouped = Apply(grouped, "Where", builder.BuildGroupPredicate(groupFilter, elementType, keyType));
            }

            var groupPlan = builder.BuildGroupProjection(select!.Projection, elementType, keyType);
            return (ApplySelect(grouped, groupPlan.Selector), groupPlan);
        }

        var plan = select is null
            ? builder.BuildDefaultProjection(elementType)
            : builder.BuildProjection(select.Projection, elementType);
        return (ApplySelect(query, plan.Selector), plan);
    }

    // The IReturnablePolicy<T>.Filter method, keyed by the policied source type. Bounded by the schema.
    static readonly ConcurrentDictionary<Type, MethodInfo> policyFilters = new();

    static IQueryable ApplyPolicy(IQueryable query, ScrySource source, DbContext db, IServiceProvider services)
    {
        if (source.PolicyType is not { } policyType)
        {
            return query;
        }

        var policy = services.GetService(policyType) ?? Activator.CreateInstance(policyType);
        if (policy is null)
        {
            throw new($"Could not create policy '{policyType.Name}'.");
        }

        var filter = policyFilters.GetOrAdd(
            source.ClrType,
            clrType => typeof(IReturnablePolicy<>)
                .MakeGenericType(clrType)
                .GetMethod(nameof(IReturnablePolicy<>.Filter))!);
        var context = new ScryPolicyContext(services, db);
        return (IQueryable)filter.Invoke(policy, [query, context])!;
    }

    static IQueryable Apply(IQueryable query, string method, LambdaExpression argument) =>
        query.Provider.CreateQuery(
            CallQueryable(method, [query.ElementType], query.Expression, Expression.Quote(argument)));

    static IQueryable ApplyOrder(IQueryable query, LambdaExpression keySelector, bool descending, bool then)
    {
        var method = (then ? "ThenBy" : "OrderBy") + (descending ? "Descending" : "");
        return query.Provider.CreateQuery(
            CallQueryable(
                method,
                [query.ElementType, keySelector.ReturnType],
                query.Expression,
                Expression.Quote(keySelector)));
    }

    static IQueryable ApplyPaging(IQueryable query, string method, int count) =>
        query.Provider.CreateQuery(
            CallQueryable(method, [query.ElementType], query.Expression, Expression.Constant(count)));

    static IQueryable ApplyGroupBy(IQueryable query, LambdaExpression keySelector, Type elementType, Type keyType) =>
        query.Provider.CreateQuery(
            CallQueryable("GroupBy", [elementType, keyType], query.Expression, Expression.Quote(keySelector)));

    static IQueryable<object[]> ApplySelect(IQueryable query, LambdaExpression selector) =>
        (IQueryable<object[]>)query.Provider.CreateQuery(
            CallQueryable("Select", [query.ElementType, typeof(object[])], query.Expression, Expression.Quote(selector)));

    // Which projected leaf an ordering names. The validator has already established that the key names
    // one of them and that none of them is nested, so member position is leaf position.
    static int DistinctLeaf(Projection projection, Node key)
    {
        if (key is MemberNode { Path: [var name] })
        {
            for (var i = 0; i < projection.Members.Count; i++)
            {
                if (string.Equals(projection.Members[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        throw new ScryValidationException("An ordering after Distinct must name a projected member.");
    }

    static IQueryable ApplySelectTyped(IQueryable query, LambdaExpression selector) =>
        query.Provider.CreateQuery(
            CallQueryable("Select", [query.ElementType, selector.ReturnType], query.Expression, Expression.Quote(selector)));

    /// <summary>
    /// Deduplicates the projected rows. A relational provider turns this into <c>SELECT DISTINCT</c>
    /// over the projected columns. An in-memory POCO source runs the same operator under LINQ to
    /// Objects, where <c>object[]</c> compares by reference and nothing would ever match, so those
    /// compare the row values instead.
    /// </summary>
    static IQueryable<object[]> ApplyDistinct(IQueryable<object[]> query, SourceKind kind)
    {
        if (kind == SourceKind.Poco)
        {
            return query.Distinct(RowComparer.Instance);
        }

        return (IQueryable<object[]>)query.Provider.CreateQuery(
            CallQueryable("Distinct", [typeof(object[])], query.Expression));
    }

    sealed class RowComparer :
        IEqualityComparer<object[]>
    {
        public static readonly RowComparer Instance = new();

        public bool Equals(object[]? x, object[]? y) =>
            x is not null &&
            y is not null &&
            x.Length == y.Length &&
            x.Zip(y).All(_ => Equals(_.First, _.Second));

        public int GetHashCode(object[] row)
        {
            var hash = new HashCode();
            foreach (var value in row)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    static T Execute<T>(IQueryable query, string method, params Expression[] arguments) =>
        (T)query.Provider.Execute(
            CallQueryable(method, [query.ElementType], [query.Expression, ..arguments]))!;

    static readonly ConcurrentDictionary<string, MethodInfo> queryableMethods = new();

    /// <summary>
    /// Builds a call to a generic <see cref="Queryable"/> method. Each method name is used with exactly
    /// one overload shape here, so the first call lets the framework's name-based binder pick the right
    /// overload, then caches its open generic definition. Later calls close that definition over
    /// <paramref name="typeArgs"/> and bind directly — skipping the metadata scan and overload
    /// resolution the name-based <see cref="Expression.Call(Type, string, Type[], Expression[])"/> does
    /// on every invocation.
    /// </summary>
    static MethodCallExpression CallQueryable(string method, Type[] typeArgs, params Expression[] arguments)
    {
        if (queryableMethods.TryGetValue(method, out var open))
        {
            return Expression.Call(open.MakeGenericMethod(typeArgs), arguments);
        }

        var call = Expression.Call(typeof(Queryable), method, typeArgs, arguments);
        queryableMethods.TryAdd(method, call.Method.GetGenericMethodDefinition());
        return call;
    }

    static object[]? ExecuteRow(IQueryable<object[]> projected, string method) =>
        method switch
        {
            "First" => projected.First(),
            "FirstOrDefault" => projected.FirstOrDefault(),
            "Single" => projected.Single(),
            "SingleOrDefault" => projected.SingleOrDefault(),
            "Last" => projected.Last(),
            "LastOrDefault" => projected.LastOrDefault(),
            _ => throw new($"Unknown row method '{method}'.")
        };

    QueryResponse Page(
        ExpressionBuilder builder,
        IQueryable query,
        Type elementType,
        SelectOp? select,
        IReadOnlyList<(Node Key, bool Descending)> orderings,
        bool seekEligible,
        PageOp page,
        ScrySource source,
        DbContext db)
    {
        var size = Math.Min(page.Size ?? options.DefaultPageSize, options.MaxPageSize);

        var (seekSafe, keys) = PlanSeek(orderings, seekEligible, source, elementType, db);
        if (!seekSafe && page.Cursor is not null)
        {
            throw new ScryValidationException(
                "Cursor paging requires an ordered query over an entity with non-nullable ordering keys.");
        }

        if (seekSafe)
        {
            // Append the primary key as a trailing ascending tiebreaker so the order — and the cursor
            // that resumes it — is total.
            foreach (var (key, _) in keys.Skip(orderings.Count))
            {
                query = ApplyOrder(query, builder.BuildKeySelector(key, elementType), descending: false, then: true);
            }

            if (page.Cursor is not null)
            {
                var values = CursorCodec.Decode(page.Cursor, SigningKey());
                if (values.Count != keys.Count)
                {
                    throw new ScryValidationException("Paging cursor does not match the query ordering.");
                }

                query = Apply(query, "Where", builder.BuildSeekPredicate(keys, values, elementType));
            }
        }

        var effectiveKeys = seekSafe ? keys : Array.Empty<(Node Key, bool Descending)>();
        var (selector, shape, keyCount) = builder.BuildPageProjection(select?.Projection, effectiveKeys, elementType);
        var projected = ApplySelect(query, selector);
        var plan = new ProjectionPlan(selector, shape);

        // Fetch one extra row to detect a further page without issuing a second COUNT query.
        var rows = projected.Take(size + 1).ToList();
        var hasMore = rows.Count > size;
        if (hasMore)
        {
            rows.RemoveRange(size, rows.Count - size);
        }

        var items = rows.Select(_ => Shape(_, plan)).ToArray();

        // The next cursor is the ordering-key tuple of the last returned row — omitted on the last page
        // (nothing more to resume) and when the query is not seek-safe (offset paging only).
        string? cursor = null;
        if (seekSafe &&
            hasMore &&
            rows.Count > 0)
        {
            var last = rows[^1];
            var keyValues = new (string?, ClrTypeTag)[keyCount];
            for (var i = 0; i < keyCount; i++)
            {
                keyValues[i] = CursorCodec.TagValue(last[shape.Count + i]);
            }

            cursor = CursorCodec.Encode(keyValues, SigningKey());
        }

        var envelope = new ScryPage<Dictionary<string, object?>>(items, hasMore, cursor);
        return QueryResponse.Create(ResultKind.Page, JsonSerializer.SerializeToElement(envelope, ScryJson.Options));
    }

    // Decides whether a page can be resumed by a keyset cursor and, if so, the total ordering to seek
    // over: the client's ordering keys plus the primary key appended as a tiebreaker. A cursor is only
    // safe over an entity ordered by single-segment, non-nullable scalar members (so the seek is a true
    // total order); anything else — a view/POCO, no ordering, a nav-path or nullable key — falls back to
    // offset paging with no cursor.
    (bool SeekSafe, IReadOnlyList<(Node Key, bool Descending)> Keys) PlanSeek(
        IReadOnlyList<(Node Key, bool Descending)> orderings,
        bool seekEligible,
        ScrySource source,
        Type elementType,
        DbContext db)
    {
        if (!seekEligible ||
            source.Kind != SourceKind.Entity ||
            orderings.Count == 0)
        {
            return (false, orderings);
        }

        foreach (var (key, _) in orderings)
        {
            if (key is not MemberNode { Path: [var single] } ||
                !IsNonNullableScalar(db, elementType, single))
            {
                return (false, orderings);
            }
        }

        var primaryKey = db.Model.FindEntityType(elementType)?.FindPrimaryKey();
        if (primaryKey is null ||
            !schema.TryGetType(elementType, out var meta))
        {
            return (false, orderings);
        }

        var pkKeys = new List<(Node, bool)>();
        foreach (var property in primaryKey.Properties)
        {
            // The key must be an exposable scalar to build a member node for the tiebreaker/cursor.
            if (!meta.Members.TryGetValue(property.Name, out var member) ||
                member.Kind != MemberKind.Scalar)
            {
                return (false, orderings);
            }

            if (!orderings.Any(_ => _.Key is MemberNode { Path: [var name] } &&
                                    name == property.Name))
            {
                pkKeys.Add((new MemberNode([property.Name]), false));
            }
        }

        return (true, [.. orderings, .. pkKeys]);
    }

    static bool IsNonNullableScalar(DbContext db, Type elementType, string member) =>
        db.Model.FindEntityType(elementType)?.FindProperty(member) is { IsNullable: false };

    byte[] SigningKey() =>
        options.CursorSigningKey ?? ephemeralSigningKey;

    // Used when no CursorSigningKey is configured: cursors are valid only within this process's lifetime.
    static readonly byte[] ephemeralSigningKey = RandomNumberGenerator.GetBytes(32);

    static QueryResponse Scalar<T>(T value) =>
        QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement(value, ScryJson.Options));

    static QueryResponse Single(object[]? row, ProjectionPlan plan)
    {
        var payload = row is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(Shape(row, plan), ScryJson.Options);
        return QueryResponse.Create(ResultKind.Single, payload);
    }

    static Dictionary<string, object?> Shape(object[] row, ProjectionPlan plan)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Shape.Count; i++)
        {
            var path = plan.Shape[i];
            var node = root;
            for (var segment = 0; segment < path.Count - 1; segment++)
            {
                if (node.TryGetValue(path[segment], out var existing) &&
                    existing is Dictionary<string, object?> childExisting)
                {
                    node = childExisting;
                }
                else
                {
                    var child = new Dictionary<string, object?>(StringComparer.Ordinal);
                    node[path[segment]] = child;
                    node = child;
                }
            }

            node[path[^1]] = row[i];
        }

        return root;
    }
}
