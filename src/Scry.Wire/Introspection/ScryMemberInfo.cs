namespace Scry;

/// <summary>
/// An allow-listed member. <see cref="TypeDisplay"/> is the exact C# the source generator would emit
/// (e.g. <c>int</c>, <c>string</c>, <c>global::System.DateOnly</c>, <c>Status?</c>,
/// <c>EmployeeQueryModel?</c>) so the explorer can synthesize an identical model.
/// </summary>
/// <remarks>
/// <c>IsCollection</c> marks an aggregable collection navigation. Like a navigation it is not a
/// projection leaf, so it is excluded from the default projection; unlike one it cannot be traversed
/// in a member path.
/// </remarks>
public sealed record ScryMemberInfo(
    string Name,
    string TypeDisplay,
    bool NeedsNullDefault,
    bool IsNavigation,
    bool IsCollection = false)
{
    /// <summary>
    /// The deprecation the model declares with <c>[Obsolete]</c>: null when the member is not
    /// deprecated, otherwise its message, or empty when the attribute carried none. Replicated onto
    /// the synthesized member so a snippet warns exactly where generated client code would.
    /// </summary>
    /// <remarks>
    /// Advisory only, and deliberately outside the schema stamp: an obsolete member is still allowed,
    /// still validated, and still executed, so deprecating one leaves the queryable surface unchanged.
    /// </remarks>
    public string? Obsolete { get; init; }
}