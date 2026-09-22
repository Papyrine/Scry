namespace Scry;

/// <summary>
/// What a command handler is handed beside the command: the scope it runs in, the context it writes
/// through, and who sent the command, against which row.
/// </summary>
public sealed class ScryCommandContext(
    IServiceProvider services,
    DbContext db,
    Guid commandId,
    string command,
    string? caller,
    IReadOnlyList<object> targetKeys)
{
    /// <summary>The command's own service scope — not the request's, which may be long gone.</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>The context resolved from <see cref="Services"/>, which is saved after the handler returns.</summary>
    public DbContext Db { get; } = db;

    /// <summary>The id the client gave the command, which its outcome is reported under.</summary>
    public Guid CommandId { get; } = commandId;

    /// <summary>The command's wire name.</summary>
    public string Command { get; } = command;

    /// <summary>Who sent the command, as <see cref="ScryOptions.Caller"/> said; null for an anonymous caller.</summary>
    public string? Caller { get; } = caller;

    /// <summary>
    /// For a targeted command, the key values of the row it acts on — already read through the target's
    /// policies and the command's own — in the target's key order. Empty for an untargeted command.
    /// </summary>
    public IReadOnlyList<object> TargetKeys { get; } = targetKeys;

    /// <summary>
    /// Whether the context is saved after the handler returns, where it holds changes. On by default;
    /// a handler that saves for itself, or writes nothing through the context, turns it off.
    /// </summary>
    public bool SaveChanges { get; set; } = true;
}
