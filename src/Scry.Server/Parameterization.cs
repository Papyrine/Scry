/// <summary>
/// How a client-supplied value reaches the provider: as a bound parameter, never as statement text.
/// </summary>
static class Parameterization
{
    /// <summary>
    /// Emits a value the way a captured variable reaches a query, so the provider binds it as a
    /// parameter rather than writing it into the statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bare <see cref="Expression.Constant(object?)"/> is inlined into the SQL text — correctly
    /// escaped by the provider's type mapping, but text nonetheless. A member read off a captured
    /// object is what the compiler produces for a closure, and what the provider's funcletizer lifts
    /// into a parameter. Client values take that shape so a value is bound rather than escaped.
    /// </para>
    /// <para>
    /// The reason is as much about cost as about escaping: an inlined value makes the statement text
    /// differ per value, so every distinct value a client sends compiles and caches its own plan.
    /// Bound values let one plan serve them all. Null is left as a constant — there is one of it, so
    /// nothing is gained, and a literal null keeps the provider's <c>IS NULL</c> rewriting simple.
    /// </para>
    /// </remarks>
    public static Expression Parameterize(object value, Type type)
    {
        var holder = holders.GetOrAdd(
            type,
            _ =>
            {
                var closed = typeof(ValueHolder<>).MakeGenericType(_);
                return (closed.GetConstructors().Single(), closed.GetField("Value")!);
            });

        return Expression.Field(
            Expression.Constant(holder.Constructor.Invoke([value]), holder.Field.DeclaringType!),
            holder.Field);
    }

    static readonly ConcurrentDictionary<Type, (ConstructorInfo Constructor, FieldInfo Field)> holders = new();

    /// <summary>Stands in for the closure a captured variable would live on.</summary>
    sealed class ValueHolder<T>(T value)
    {
        public readonly T Value = value;
    }
}
