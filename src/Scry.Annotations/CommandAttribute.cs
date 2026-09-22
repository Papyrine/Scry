namespace Scry;

/// <summary>
/// Opts a message class into being sent by a client as a command: bound, authorized and handed to a
/// handler on the server, with its outcome answered back. Without this attribute a type is never
/// exposed (default-deny). The payload is the class's public readable and writable properties, less any
/// marked <see cref="CommandIgnoreAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// A targeted command names the queryable entity it acts on, and carries that entity's key as
/// properties named like the key members (<c>Id</c>) or prefixed with the entity's type name
/// (<c>EmployeeId</c>). The server reads the row through the entity's policies before the command is
/// handled, and the entity's generated query model gains a <c>Can{Command}</c> member saying, row by
/// row, whether the caller may send it.
/// </para>
/// <para>
/// Not inherited: a class deriving from a command is a command only where it carries the attribute
/// itself, so a message hierarchy never exposes a subclass nobody opted in.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CommandAttribute :
    Attribute
{
    /// <summary>An untargeted command: one that acts on no particular row.</summary>
    public CommandAttribute()
    {
    }

    /// <summary>A command acting on one row of <paramref name="target"/>, an opted-in queryable entity.</summary>
    public CommandAttribute(Type target) =>
        Target = target;

    /// <summary>The queryable entity the command acts on, or null for an untargeted command.</summary>
    public Type? Target { get; }

    /// <summary>
    /// Overrides the command name exposed to clients — the name a wire request carries and the method
    /// emitted on the generated facade. Defaults to the type name. Blank is treated as unset.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The type a handler answers with, or null for a command whose outcome is all it answers with. A
    /// plain class of public readable and writable properties, like a payload.
    /// </summary>
    public Type? Result { get; set; }

    /// <summary>
    /// The server-side policy deciding who may send the command, and for a targeted command which rows
    /// it may be sent against. Must implement <c>ICommandPolicy&lt;TCommand&gt;</c>, and may also
    /// implement <c>ICommandPolicy&lt;TCommand, TEntity&gt;</c>. Client-irrelevant.
    /// </summary>
    public Type? Policy { get; set; }
}
