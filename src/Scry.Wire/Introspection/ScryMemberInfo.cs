namespace Scry.Wire;

/// <summary>
/// An allow-listed member. <see cref="TypeDisplay"/> is the exact C# the source generator would emit
/// (e.g. <c>int</c>, <c>string</c>, <c>global::System.DateOnly</c>, <c>Status?</c>,
/// <c>EmployeeQueryModel?</c>) so the explorer can synthesize an identical model.
/// </summary>
public sealed record ScryMemberInfo(string Name, string TypeDisplay, bool NeedsNullDefault, bool IsNavigation);