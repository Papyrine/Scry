using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.Metadata;

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

        if (terminal is CountOp)
        {
            var count = Execute<int>(query, "Count");
            return Scalar(count);
        }

        if (terminal is AnyOp any)
        {
            if (any.Predicate is { } anyPredicate)
            {
                query = Apply(query, "Where", builder.BuildPredicate(anyPredicate, elementType));
            }

            return Scalar(Execute<bool>(query, "Any"));
        }

        if (terminal is PageOp page)
        {
            return Page(query, elementType, select, orderings, tailIsOrdered && !sawSkipOrTake, page, source, db);
        }

        var (projected, plan) = BuildProjected(query, elementType, groupBy, select, terminal);

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

        var rows = projected.ToList();
        var array = rows.Select(_ => Shape(_, plan)).ToArray();
        return QueryResponse.Create(ResultKind.List, JsonSerializer.SerializeToElement(array, ScryJson.Options));
    }

    (IQueryable<object[]> Query, ProjectionPlan Plan) BuildProjected(
        IQueryable query,
        Type elementType,
        GroupByOp? groupBy,
        SelectOp? select,
        QueryOp? terminal)
    {
        if (groupBy is not null)
        {
            var keySelector = builder.BuildKeySelector(groupBy.Keys[0], elementType);
            var keyType = keySelector.ReturnType;
            var grouped = ApplyGroupBy(query, keySelector, elementType, keyType);
            var groupPlan = builder.BuildGroupProjection(select!.Projection, elementType, keyType);
            return (ApplySelect(grouped, groupPlan.Selector), groupPlan);
        }

        // Terminal predicate on First/Single is applied pre-projection.
        if (terminal is FirstOp { Predicate: { } firstPredicate })
        {
            query = Apply(query, "Where", builder.BuildPredicate(firstPredicate, elementType));
        }
        else if (terminal is SingleOp { Predicate: { } singlePredicate })
        {
            query = Apply(query, "Where", builder.BuildPredicate(singlePredicate, elementType));
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

    static T Execute<T>(IQueryable query, string method) =>
        (T)query.Provider.Execute(
            CallQueryable(method, [query.ElementType], query.Expression))!;

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
