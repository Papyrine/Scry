using System.Text.Json.Serialization;

namespace Scry.Wire;

/// <summary>
/// A single operator in the query pipeline, applied left-to-right. The set is closed; the server
/// validates pipeline well-formedness (e.g. <c>ThenBy</c> only after <c>OrderBy</c>, aggregates only
/// in a projection following <c>GroupBy</c>, at most one terminal).
/// </summary>
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

/// <summary>Filters the sequence by a predicate.</summary>
public sealed record WhereOp(Expr Predicate) :
    QueryOp;

/// <summary>Orders the sequence by a key. Must be the first ordering operator.</summary>
public sealed record OrderByOp(Expr Key, bool Descending) :
    QueryOp;

/// <summary>Adds a secondary ordering. Only valid after an <see cref="OrderByOp"/>.</summary>
public sealed record ThenByOp(Expr Key, bool Descending) :
    QueryOp;

/// <summary>Skips a number of elements.</summary>
public sealed record SkipOp(int Count) :
    QueryOp;

/// <summary>Takes at most a number of elements (capped by the server page-size limit).</summary>
public sealed record TakeOp(int Count) :
    QueryOp;

/// <summary>Projects each element to the requested shape.</summary>
public sealed record SelectOp(Projection Projection) :
    QueryOp;

/// <summary>Groups the sequence by one or more keys. A following <see cref="SelectOp"/> may use
/// aggregates and the group key.</summary>
public sealed record GroupByOp(IReadOnlyList<Expr> Keys) :
    QueryOp;

/// <summary>Terminal: returns the element count as a scalar.</summary>
public sealed record CountOp :
    QueryOp;

/// <summary>Terminal: returns whether any element matches the optional predicate.</summary>
public sealed record AnyOp(Expr? Predicate) :
    QueryOp;

/// <summary>Terminal: returns the first element (or default) optionally matching a predicate.</summary>
public sealed record FirstOp(bool OrDefault, Expr? Predicate) :
    QueryOp;

/// <summary>Terminal: returns the single element (or default) optionally matching a predicate.</summary>
public sealed record SingleOp(bool OrDefault, Expr? Predicate) :
    QueryOp;
