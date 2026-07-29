/// <summary>
/// Orchestrates the full server pipeline for one request: validate against the allow-list, resolve
/// the source, apply the row policy, rebind the AST to an EF query, execute, and shape the result.
/// </summary>
sealed class QueryExecutor(Schema schema, ScryOptions options)
{
    QueryValidator validator = new(schema, options);
    ExpressionBuilder builder = new(schema);

    public QueryResponse Execute(QueryRequest request, DbContext db, IServiceProvider services)
    {
        var source = validator.Validate(request);
        var elementType = source.ClrType;

        var query = source.Resolve(db, services);
        query = ApplyPolicy(query, source, db, services);

        GroupByOp? groupBy = null;
        SelectOp? select = null;
        QueryOp? terminal = null;
        // Captured alongside inline application so the page terminal can rebuild the ordering into a
        // total order (append the primary key) and a keyset seek predicate.
        List<(Node Key, bool Descending)> orderings = [];
        // A cursor can only append its primary-key tiebreak when the ordering is the trailing restricting
        // op (so the query is still IOrderedQueryable) and no offset (Skip/Take) is in play.
        var tailIsOrdered = false;
        var sawSkipOrTake = false;
        // Distinct deduplicates the projected rows, so it is applied after the projection is built
        // rather than inline with the operators over the entity query.
        var distinct = false;

        foreach (var op in request.Pipeline)
        {
            switch (op)
            {
                case WhereOp where:
                    tailIsOrdered = false;
                    query = Apply(query, "Where", builder.BuildPredicate(where.Predicate, elementType));
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
            return Page(query, elementType, select, orderings, tailIsOrdered && !sawSkipOrTake, page, source, db);
        }

        // Folding a deduplicated sequence reads one value per row, so it is projected as that value's
        // own type and deduplicated there. The shaped object[] a row projection produces has no
        // equality of its own for a provider to deduplicate on — only enumeration can shape it back
        // afterwards — which is why the validator confines this to a single projected member.
        if (distinct &&
            terminal is CountOp or LongCountOp or AnyOp)
        {
            var values = ApplySelectTyped(query, builder.BuildSingleValueSelector(select!.Projection, elementType));
            var deduped = values.Provider.CreateQuery(
                CallQueryable("Distinct", [values.ElementType], values.Expression));

            return terminal switch
            {
                CountOp => Scalar(Execute<int>(deduped, "Count")),
                LongCountOp => Scalar(Execute<long>(deduped, "LongCount")),
                _ => Scalar(Execute<bool>(deduped, "Any"))
            };
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
                return Aggregate(query, aggregate, elementType);
        }

        var (projected, plan) = BuildProjected(query, elementType, groupBy, select);

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
    QueryResponse Aggregate(IQueryable query, AggregateOp aggregate, Type elementType)
    {
        var selector = builder.BuildAggregateSelector(aggregate.Selector, elementType, aggregate.Function);
        var values = ApplySelectTyped(query, selector);
        var name = aggregate.Function.ToString();

        // Min/Max are generic in the value type; Sum/Average have one overload per numeric type.
        var call = aggregate.Function is AggregateFn.Min or AggregateFn.Max
            ? Expression.Call(typeof(Queryable), name, [values.ElementType], values.Expression)
            : Expression.Call(typeof(Queryable), name, null, values.Expression);

        return Scalar(values.Provider.Execute(call));
    }

    (IQueryable<object[]> Query, ProjectionPlan Plan) BuildProjected(
        IQueryable query,
        Type elementType,
        GroupByOp? groupBy,
        SelectOp? select)
    {
        if (groupBy is not null)
        {
            var keySelector = builder.BuildKeySelector(groupBy.Keys[0], elementType);
            var keyType = keySelector.ReturnType;
            var grouped = ApplyGroupBy(query, keySelector, elementType, keyType);
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

            if (!orderings.Any(_ => _.Key is MemberNode { Path: [var name] } && name == property.Name))
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
