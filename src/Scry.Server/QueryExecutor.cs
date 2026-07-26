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

        foreach (var op in request.Pipeline)
        {
            switch (op)
            {
                case WhereOp where:
                    query = Apply(query, "Where", builder.BuildPredicate(where.Predicate, elementType));
                    break;
                case OrderByOp orderBy:
                    query = ApplyOrder(query, builder.BuildKeySelector(orderBy.Key, elementType), orderBy.Descending, then: false);
                    break;
                case ThenByOp thenBy:
                    query = ApplyOrder(query, builder.BuildKeySelector(thenBy.Key, elementType), thenBy.Descending, then: true);
                    break;
                case SkipOp skip:
                    query = ApplyPaging(query, "Skip", skip.Count);
                    break;
                case TakeOp take:
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

        var rows = ((IQueryable<object[]>)projected).ToList();
        var array = rows.Select(_ => Shape(_, plan)).ToArray();
        return QueryResponse.Create(ResultKind.List, JsonSerializer.SerializeToElement(array, ScryJson.Options));
    }

    (IQueryable Query, ProjectionPlan Plan) BuildProjected(
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

    static IQueryable ApplySelect(IQueryable query, LambdaExpression selector) =>
        query.Provider.CreateQuery(
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
    static Expression CallQueryable(string method, Type[] typeArgs, params Expression[] arguments)
    {
        if (queryableMethods.TryGetValue(method, out var open))
        {
            return Expression.Call(open.MakeGenericMethod(typeArgs), arguments);
        }

        var call = Expression.Call(typeof(Queryable), method, typeArgs, arguments);
        queryableMethods.TryAdd(method, call.Method.GetGenericMethodDefinition());
        return call;
    }

    static object[]? ExecuteRow(IQueryable projected, string method)
    {
        var typed = (IQueryable<object[]>)projected;
        return method switch
        {
            "First" => typed.First(),
            "FirstOrDefault" => typed.FirstOrDefault(),
            "Single" => typed.Single(),
            "SingleOrDefault" => typed.SingleOrDefault(),
            _ => throw new($"Unknown row method '{method}'.")
        };
    }

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
