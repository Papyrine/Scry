namespace Scry;

/// <summary>
/// Opts an EF Core complex type (a value object, typically mapped to a JSON column) into client
/// querying as a <em>traversable member type</em>. Unlike <see cref="QueryableAttribute"/>, a complex
/// type is never a root source: it produces no entry point on the generated query facade and no
/// server resolver. It is reachable only by traversing into it from an opted-in
/// entity/view/POCO — for example <c>Employee.Address.City</c> — or, when the owner holds a
/// collection of it opted in with <see cref="QueryableCollectionAttribute"/>, by aggregating or
/// flattening that JSON array. All public readable properties are exposed unless marked
/// <see cref="QueryIgnoreAttribute"/> (default-deny).
/// </summary>
/// <remarks>
/// Having no source of its own, a complex type can carry no row policy: there is nothing for one to
/// filter, so <see cref="ReturnableWithAttribute"/> here is refused at startup rather than silently
/// ignored. Filter on the type that owns it instead.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class QueryableComplexAttribute :
    Attribute;
