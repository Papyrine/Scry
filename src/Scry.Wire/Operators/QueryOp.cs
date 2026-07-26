namespace Scry.Wire;

/// <summary>
/// A single operator in the query pipeline, applied left-to-right. The set is closed; the server
/// validates pipeline well-formedness (e.g. <c>ThenBy</c> only after <c>OrderBy</c>, aggregates only
/// in a projection following <c>GroupBy</c>, at most one terminal).
/// </summary>
// begin-snippet: wireOperators
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WhereOp), "where")]
[JsonDerivedType(typeof(OrderByOp), "orderBy")]
[JsonDerivedType(typeof(ThenByOp), "thenBy")]
[JsonDerivedType(typeof(SkipOp), "skip")]
[JsonDerivedType(typeof(TakeOp), "take")]
[JsonDerivedType(typeof(SelectOp), "select")]
[JsonDerivedType(typeof(GroupByOp), "groupBy")]
[JsonDerivedType(typeof(CountOp), "count")]
[JsonDerivedType(typeof(AnyOp), "any")]
[JsonDerivedType(typeof(FirstOp), "first")]
[JsonDerivedType(typeof(SingleOp), "single")]
public abstract record QueryOp;
// end-snippet