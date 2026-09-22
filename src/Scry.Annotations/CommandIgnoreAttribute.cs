namespace Scry;

/// <summary>
/// Excludes a property from a command's payload. The generated client never sees it and a request
/// carrying it is refused; the server fills it — from the caller's identity, the clock, anything a
/// handler knows and a client must not choose.
/// </summary>
/// <remarks>
/// The command counterpart of <see cref="QueryIgnoreAttribute"/>, which is refused on a command property
/// rather than read as this: hiding a member from queries and hiding it from a payload are different
/// decisions, and a model that meant one should not be taken to mean the other.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CommandIgnoreAttribute :
    Attribute;
