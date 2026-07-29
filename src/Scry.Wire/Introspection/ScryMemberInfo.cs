namespace Scry.Wire;

/// <summary>
/// An allow-listed member. <see cref="TypeDisplay"/> is the exact C# the source generator would emit
/// (e.g. <c>int</c>, <c>string</c>, <c>global::System.DateOnly</c>, <c>Status?</c>,
/// <c>EmployeeQueryModel?</c>) so the explorer can synthesize an identical model.
/// </summary>
/// <param name="IsCollection">
/// True for an aggregable collection navigation. Like a navigation it is not a projection leaf, so it
/// is excluded from the default projection; unlike one it cannot be traversed in a member path.
/// </param>
public sealed record ScryMemberInfo(
    string Name,
    string TypeDisplay,
    bool NeedsNullDefault,
    bool IsNavigation,
    bool IsCollection = false);