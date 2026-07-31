namespace Scry;

/// <summary>A re-emitted enum: its name and member names in declaration order.</summary>
public sealed record ScryEnumInfo(string Name, IReadOnlyList<string> Values);