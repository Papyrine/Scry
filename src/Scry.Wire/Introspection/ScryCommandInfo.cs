namespace Scry;

/// <summary>
/// A command a client may send: its name and payload properties, and for a targeted command the source
/// it acts on and which payload properties carry that source's key. <see cref="ScryMemberInfo.TypeDisplay"/>
/// on each property is the exact C# the source generator emits on the command's generated class.
/// </summary>
/// <remarks>
/// Like the rest of the document it carries no policy and no handler: who may send a command is decided
/// on the server each time, and published per caller by the capabilities endpoint rather than here.
/// </remarks>
public sealed record ScryCommandInfo(string Name, IReadOnlyList<ScryMemberInfo> Properties)
{
    /// <summary>The source a targeted command acts on, by the name a query root uses. Null for an untargeted command.</summary>
    public string? Target { get; init; }

    /// <summary>
    /// For a targeted command, the payload properties carrying the target's key, in the target's key
    /// order (ordinal by key member name). Null for an untargeted command.
    /// </summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary>What a handler answers with, where the command declares a result.</summary>
    public ScryResultInfo? Result { get; init; }

    /// <summary>
    /// The deprecation the command class declares with <c>[Obsolete]</c>, in the same form as
    /// <see cref="ScryMemberInfo.Obsolete"/>.
    /// </summary>
    public string? Obsolete { get; init; }
}
