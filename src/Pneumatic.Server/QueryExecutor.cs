using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pneumatic.Wire;

namespace Pneumatic;

/// <summary>
/// Orchestrates the full server pipeline for one request: validate against the allow-list, resolve
/// the source, apply the row policy, rebind the AST to an EF query, execute, and shape the result.
/// </summary>
sealed class QueryExecutor(PneumaticSchema schema, PneumaticOptions options)
{
    readonly QueryValidator validator = new(schema, options);
    readonly ExpressionBuilder builder = new(schema);

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
        return QueryResponse.Create(ResultKind.List, JsonSerializer.SerializeToElement(array, PneumaticJson.Options));
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

    IQueryable ApplyPolicy(IQueryable query, PneumaticSource source, DbContext db, IServiceProvider services)
    {
        if (source.PolicyType is not { } policyType)
        {
            return query;
        }

        var policy = services.GetService(policyType) ?? Activator.CreateInstance(policyType);
        if (policy is null)
        {
            throw new InvalidOperationException($"Could not create policy '{policyType.Name}'.");
        }

        var filter = typeof(IReturnablePolicy<>)
            .MakeGenericType(source.ClrType)
            .GetMethod(nameof(IReturnablePolicy<object>.Filter))!;
        var context = new PneumaticPolicyContext(services, db);
        return (IQueryable)filter.Invoke(policy, [query, context])!;
    }

    static IQueryable Apply(IQueryable query, string method, LambdaExpression argument) =>
        query.Provider.CreateQuery(
            Expression.Call(
                typeof(Queryable),
                method,
                [query.ElementType],
                query.Expression,
                Expression.Quote(argument)));

    static IQueryable ApplyOrder(IQueryable query, LambdaExpression keySelector, bool descending, bool then)
    {
        var method = (then ? "ThenBy" : "OrderBy") + (descending ? "Descending" : "");
        return query.Provider.CreateQuery(
            Expression.Call(
                typeof(Queryable),
                method,
                [query.ElementType, keySelector.ReturnType],
                query.Expression,
                Expression.Quote(keySelector)));
    }

    static IQueryable ApplyPaging(IQueryable query, string method, int count) =>
        query.Provider.CreateQuery(
            Expression.Call(
                typeof(Queryable),
                method,
                [query.ElementType],
                query.Expression,
                Expression.Constant(count)));

    static IQueryable ApplyGroupBy(IQueryable query, LambdaExpression keySelector, Type elementType, Type keyType) =>
        query.Provider.CreateQuery(
            Expression.Call(
                typeof(Queryable),
                "GroupBy",
                [elementType, keyType],
                query.Expression,
                Expression.Quote(keySelector)));

    static IQueryable ApplySelect(IQueryable query, LambdaExpression selector) =>
        query.Provider.CreateQuery(
            Expression.Call(
                typeof(Queryable),
                "Select",
                [query.ElementType, typeof(object[])],
                query.Expression,
                Expression.Quote(selector)));

    static T Execute<T>(IQueryable query, string method) =>
        (T)query.Provider.Execute(
            Expression.Call(typeof(Queryable), method, [query.ElementType], query.Expression))!;

    static object[]? ExecuteRow(IQueryable projected, string method)
    {
        var typed = (IQueryable<object[]>)projected;
        return method switch
        {
            "First" => typed.First(),
            "FirstOrDefault" => typed.FirstOrDefault(),
            "Single" => typed.Single(),
            "SingleOrDefault" => typed.SingleOrDefault(),
            _ => throw new InvalidOperationException($"Unknown row method '{method}'.")
        };
    }

    static QueryResponse Scalar<T>(T value) =>
        QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement(value, PneumaticJson.Options));

    static QueryResponse Single(object[]? row, ProjectionPlan plan)
    {
        var payload = row is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(Shape(row, plan), PneumaticJson.Options);
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
