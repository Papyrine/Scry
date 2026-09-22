namespace Scry;

/// <summary>
/// Attached by the generator to each command class, naming the command on the wire. What
/// <see cref="ScryClient.SendCommandAsync{TCommand}"/> reads to know which command an instance is, so a command can be
/// sent through the generated facade or handed to the client directly.
/// </summary>
/// <remarks>
/// Nothing trusts this: the name is resolved against the server's own commands, and the payload bound
/// into the server's own class, on every command.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ScryCommandAttribute(string name) :
    Attribute
{
    /// <summary>The wire name of the command.</summary>
    public string Name { get; } = name;

    /// <summary>The source a targeted command acts on, or null for an untargeted one.</summary>
    public string? Target { get; set; }

    /// <summary>
    /// The properties carrying the target's key, in the target's key order. Empty for an untargeted
    /// command.
    /// </summary>
    public string[] Keys { get; set; } = [];

    /// <summary>The class the command answers with, or null for one whose outcome is all it answers with.</summary>
    public Type? Result { get; set; }
}
