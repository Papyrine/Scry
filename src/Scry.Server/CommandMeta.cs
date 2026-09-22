/// <summary>
/// A command the server accepts: its wire name, the class a payload is bound into, what it acts on,
/// what it answers with, and who may send it. Built once at startup by the schema, from the model's
/// annotations, independent of what any client was generated against.
/// </summary>
sealed class CommandMeta(string name, Type clrType)
{
    public string Name { get; } = name;

    public Type ClrType { get; } = clrType;

    /// <summary>The source a targeted command acts on, or null for an untargeted one.</summary>
    public ScrySource? Target { get; init; }

    /// <summary>
    /// For a targeted command, each of the target's key members with the payload property carrying it,
    /// in the target's key order — ordinal by key member name.
    /// </summary>
    public IReadOnlyList<(Member Key, PropertyInfo Payload)> TargetKeys { get; init; } = [];

    /// <summary>
    /// The properties a payload may set: public, readable and writable, less any marked
    /// <c>[CommandIgnore]</c>. Nothing else of the class is reachable from a request.
    /// </summary>
    public IReadOnlyList<PropertyInfo> Payload { get; init; } = [];

    /// <summary>The class a handler answers with, or null for a command answering with its outcome alone.</summary>
    public Type? Result { get; init; }

    /// <summary>The properties a result is described and serialized by: its public readable ones.</summary>
    public IReadOnlyList<PropertyInfo> ResultProperties { get; init; } = [];

    /// <summary>The policy deciding who may send the command, or null where anyone may.</summary>
    public Type? Policy { get; init; }

    /// <summary>
    /// The type the policy's row interface is written against, where it implements one: the target or a
    /// base of it. Null where the policy decides for the command as a whole only.
    /// </summary>
    public Type? PolicyRows { get; init; }

    /// <summary>The capability the command adds to its target, or null for an untargeted command.</summary>
    public Member? Capability { get; init; }

    /// <summary>The deprecation the class declares with <c>[Obsolete]</c>, in introspection's form.</summary>
    public string? Obsolete { get; init; }

    /// <summary>
    /// False where the command cannot be served by this host — its target is not one the context maps.
    /// Such a command reads as denied everywhere: its capability is false and sending it is refused.
    /// </summary>
    public bool Available { get; set; } = true;
}
