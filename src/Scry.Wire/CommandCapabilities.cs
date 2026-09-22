namespace Scry;

/// <summary>
/// The commands this caller may send at all, by name: each command whose server-side policy allows the
/// caller, for as long as the caller's identity says so. Advisory — the server decides again on every
/// command — and so what a UI enables a button by, never what authorizes anything.
/// </summary>
/// <remarks>
/// A targeted command's rows are decided apart from this, per row, by the <c>Can{Command}</c> member its
/// target's query model carries.
/// </remarks>
public sealed record CommandCapabilities(int Version, IReadOnlyList<string> Commands)
{
    /// <summary>Creates a document stamped with <see cref="CommandRequest.CurrentVersion"/>.</summary>
    public static CommandCapabilities Create(IReadOnlyList<string> commands, string? stamp = null) =>
        new(CommandRequest.CurrentVersion, commands)
        {
            Stamp = stamp
        };

    /// <summary>The server's schema stamp, as every response carries it.</summary>
    public string? Stamp { get; init; }
}
