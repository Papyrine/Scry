namespace Skry;

/// <summary>
/// Excludes a property from an opted-in queryable type. The property will not appear in generated
/// client code and is rejected by server-side validation.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class QueryIgnoreAttribute :
    Attribute;
