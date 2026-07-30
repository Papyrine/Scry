/// <summary>
/// The row a deduplicated projection is materialized as. A shaped <c>object[]</c> row has no equality
/// or ordering of its own, so a Distinct over more than one member is projected into one of these
/// instead: a record, so an in-memory source deduplicates it structurally, with one property per
/// projected leaf so a relational provider can decompose it into columns.
/// </summary>
/// <remarks>
/// The decomposition is what matters. A provider can push these into a subquery — and so order, page
/// or count them — only when the projection's <see cref="System.Linq.Expressions.NewExpression"/>
/// carries its member mappings, which is why <see cref="ExpressionBuilder"/> always supplies them.
/// </remarks>
static class DistinctRow
{
    /// <summary>The row types by arity. Beyond this a deduplicated projection can only be enumerated.</summary>
    public static readonly Type[] ByArity =
    [
        typeof(DistinctRow<>).GetGenericTypeDefinition(),
        typeof(DistinctRow<,>).GetGenericTypeDefinition(),
        typeof(DistinctRow<,,>).GetGenericTypeDefinition(),
        typeof(DistinctRow<,,,>).GetGenericTypeDefinition(),
        typeof(DistinctRow<,,,,>).GetGenericTypeDefinition(),
        typeof(DistinctRow<,,,,,>).GetGenericTypeDefinition(),
        typeof(DistinctRow<,,,,,,>).GetGenericTypeDefinition(),
        typeof(DistinctRow<,,,,,,,>).GetGenericTypeDefinition(),
    ];
}

sealed record DistinctRow<T1>(T1 Value1);

sealed record DistinctRow<T1, T2>(T1 Value1, T2 Value2);

sealed record DistinctRow<T1, T2, T3>(T1 Value1, T2 Value2, T3 Value3);

sealed record DistinctRow<T1, T2, T3, T4>(T1 Value1, T2 Value2, T3 Value3, T4 Value4);

sealed record DistinctRow<T1, T2, T3, T4, T5>(T1 Value1, T2 Value2, T3 Value3, T4 Value4, T5 Value5);

sealed record DistinctRow<T1, T2, T3, T4, T5, T6>(T1 Value1, T2 Value2, T3 Value3, T4 Value4, T5 Value5, T6 Value6);

sealed record DistinctRow<T1, T2, T3, T4, T5, T6, T7>(T1 Value1, T2 Value2, T3 Value3, T4 Value4, T5 Value5, T6 Value6, T7 Value7);

sealed record DistinctRow<T1, T2, T3, T4, T5, T6, T7, T8>(T1 Value1, T2 Value2, T3 Value3, T4 Value4, T5 Value5, T6 Value6, T7 Value7, T8 Value8);

