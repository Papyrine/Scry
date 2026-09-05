/// <summary>
/// A question asked of the database before a query runs: did the query read a row that a policy
/// denies and that policy is configured to fail the request rather than hide? Planned where the
/// query is built and asked with the others before it runs, so the executor can ask it however it
/// asks everything else — blocking, or awaited.
/// </summary>
/// <remarks>
/// A separate question from the query itself, and asked before it runs: the row must never be
/// materialized, and a folding terminal (a count, an aggregate) would leave nothing to inspect
/// afterwards even if it could be. The executed query still applies every policy, so a row inserted
/// between the two statements is at worst hidden, never returned.
/// </remarks>
sealed class DeniedRowProbe
{
    readonly IQueryable query;
    readonly Expression? exists;
    readonly IQueryable? full;

    DeniedRowProbe(IQueryable query, Expression? exists, IQueryable? full)
    {
        this.query = query;
        this.exists = exists;
        this.full = full;
    }

    /// <summary>
    /// Given two builds of the same query — one carrying only the policies that hide, one carrying
    /// the whole chain — whether the first has a row the second does not. Both describe the same
    /// rows through the same operators and differ only in which policies they carry.
    /// </summary>
    public static DeniedRowProbe Rows(IQueryable hide, IQueryable full, DbContext db) =>
        Correlated(hide, full, db) is { } exists
            ? new(hide, exists, null)
            : new(hide, null, full);

    /// <summary>
    /// Whether any row of <paramref name="owners"/> satisfies <paramref name="denied"/>, a condition
    /// over one owner row. How a traversal into a policied source is probed.
    /// </summary>
    public static DeniedRowProbe Owners(IQueryable owners, LambdaExpression denied) =>
        new(owners, Any(owners.ElementType, owners.Expression, denied), null);

    /// <summary>Throws where the answer is yes.</summary>
    public void Ensure()
    {
        if (Denied())
        {
            throw Denial();
        }
    }

    public async ValueTask EnsureAsync(Cancel cancel)
    {
        if (await DeniedAsync(cancel))
        {
            throw Denial();
        }
    }

    static ScryPermissionException Denial() =>
        new(ScryPermissionException.DeniedMessage);

    bool Denied()
    {
        if (exists is not null)
        {
            return (bool)Execution.Run(query, exists)!;
        }

        // No key to correlate on — a keyless view, a POCO source, a key EF holds in shadow. Every
        // policy narrows, so the denied set is empty exactly when the two agree on how many rows there
        // are, which costs a second round trip but asks nothing of the row's shape.
        return (int)Execution.Run(query, Total(query))! > (int)Execution.Run(full!, Total(full!))!;
    }

    async ValueTask<bool> DeniedAsync(Cancel cancel)
    {
        if (exists is not null)
        {
            return (bool)(await Execution.RunAsync(query, exists, cancel))!;
        }

        var hidden = (int)(await Execution.RunAsync(query, Total(query), cancel))!;
        var whole = (int)(await Execution.RunAsync(full!, Total(full!), cancel))!;
        return hidden > whole;
    }

    /// <summary>
    /// <c>hide.Any(row =&gt; !full.Any(other =&gt; other.Key == row.Key))</c>, or null where the element
    /// has no key made of readable properties. One statement, and the database stops at the first row
    /// it finds.
    /// </summary>
    static Expression? Correlated(IQueryable hide, IQueryable full, DbContext db)
    {
        var element = hide.ElementType;
        if (db.Model.FindEntityType(element)?.FindPrimaryKey() is not { } key)
        {
            return null;
        }

        var row = Expression.Parameter(element, "_");
        var other = Expression.Parameter(element, "other");
        Expression? match = null;
        foreach (var property in key.Properties)
        {
            // A shadow key has nothing to read on the CLR type, so the comparison cannot be written.
            if (property.PropertyInfo is not { } clr)
            {
                return null;
            }

            var equal = Expression.Equal(Expression.Property(other, clr), Expression.Property(row, clr));
            match = match is null ? equal : Expression.AndAlso(match, equal);
        }

        var seen = Any(element, full.Expression, Expression.Lambda(match!, other));
        return Any(element, hide.Expression, Expression.Lambda(Expression.Not(seen), row));
    }

    static MethodCallExpression Any(Type element, Expression source, LambdaExpression predicate) =>
        Expression.Call(anyWithPredicate.MakeGenericMethod(element), source, Expression.Quote(predicate));

    static MethodCallExpression Total(IQueryable query) =>
        Expression.Call(count.MakeGenericMethod(query.ElementType), query.Expression);

    // Resolved by parameter count rather than through the executor's shared helper, which caches one
    // overload per method name and already holds the predicate-less Any and Count.
    static readonly MethodInfo anyWithPredicate = Overload(nameof(Queryable.Any), 2);
    static readonly MethodInfo count = Overload(nameof(Queryable.Count), 1);

    static MethodInfo Overload(string name, int parameters) =>
        typeof(Queryable).GetMethods()
            .Single(_ => _.Name == name && _.GetParameters().Length == parameters);
}
