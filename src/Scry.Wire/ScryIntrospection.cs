using System.Collections.Generic;

namespace Scry.Wire;

/// <summary>
/// A read-only description of a Scry server's allow-listed query surface, served to tooling (the
/// query explorer) so it can reconstruct the generated client query models and drive IntelliSense.
/// Carries no security-sensitive detail (no policies, resolvers, or CLR internals).
/// </summary>
public sealed record ScryIntrospection(
    int Version,
    int MaxPageSize,
    IReadOnlyList<ScrySourceInfo> Sources,
    IReadOnlyList<ScryTypeInfo> Types,
    IReadOnlyList<ScryEnumInfo> Enums)
{
    /// <summary>Current introspection contract version.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>A queryable source (the root of a query): its name, kind, and the model type it yields.</summary>
public sealed record ScrySourceInfo(string Name, string Kind, string ModelName);

/// <summary>A generated client query-model type and its allow-listed members.</summary>
public sealed record ScryTypeInfo(string ModelName, IReadOnlyList<ScryMemberInfo> Members);

/// <summary>
/// An allow-listed member. <see cref="TypeDisplay"/> is the exact C# the source generator would emit
/// (e.g. <c>int</c>, <c>string</c>, <c>global::System.DateOnly</c>, <c>Status?</c>,
/// <c>EmployeeQueryModel?</c>) so the explorer can synthesize an identical model.
/// </summary>
public sealed record ScryMemberInfo(string Name, string TypeDisplay, bool NeedsNullDefault, bool IsNavigation);

/// <summary>A re-emitted enum: its name and member names in declaration order.</summary>
public sealed record ScryEnumInfo(string Name, IReadOnlyList<string> Values);
