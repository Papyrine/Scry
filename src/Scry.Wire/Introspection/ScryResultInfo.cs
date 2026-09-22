namespace Scry;

/// <summary>
/// The class a command's handler answers with, as the source generator emits it: its generated name and
/// properties.
/// </summary>
public sealed record ScryResultInfo(string Name, IReadOnlyList<ScryMemberInfo> Properties);
