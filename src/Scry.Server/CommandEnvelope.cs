namespace Scry;

/// <summary>
/// A command ready to be handed on: the bound instance, and what its outcome is reported under.
/// </summary>
/// <param name="Id">The id the client gave the command. Report the outcome under this.</param>
/// <param name="Name">The command's wire name.</param>
/// <param name="CommandType">The server's class the payload was bound into.</param>
/// <param name="Command">The bound instance, holding only what the client may set.</param>
/// <param name="Caller">Who sent it, as <see cref="ScryOptions.Caller"/> said.</param>
/// <param name="Source">The source a targeted command acts on; null for an untargeted one.</param>
/// <param name="Keys">The target row's key values, in key order; empty for an untargeted command.</param>
/// <param name="ResultType">The class a handler answers with, or null.</param>
public sealed record CommandEnvelope(
    Guid Id,
    string Name,
    Type CommandType,
    object Command,
    string? Caller,
    string? Source,
    IReadOnlyList<object> Keys,
    Type? ResultType);
