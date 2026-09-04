namespace Scry;

/// <summary>A re-emitted enum: its name and member names in declaration order.</summary>
/// <remarks>
/// The members' values, the underlying type, and <c>[Flags]</c> travel beside the names, since a
/// client re-emitting the enum without them would compute a different value for every member past
/// the first explicit one — and a combined flag, which travels by name, would then name the wrong
/// member on one side.
/// </remarks>
public sealed record ScryEnumInfo(string Name, IReadOnlyList<string> Values)
{
    /// <summary>
    /// The value each member holds, positionally matching <see cref="Values"/>, spelled as the
    /// invariant-culture decimal its declaration would use.
    /// </summary>
    public IReadOnlyList<string>? Constants { get; init; }

    /// <summary>Whether the enum carries <c>[Flags]</c>, which a re-emitted copy carries too.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsFlags { get; init; }

    /// <summary>The C# keyword of the underlying integral type — <c>int</c> unless declared otherwise.</summary>
    public string Underlying { get; init; } = "int";
}
