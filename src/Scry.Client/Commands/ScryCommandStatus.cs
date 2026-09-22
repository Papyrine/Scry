namespace Scry;

/// <summary>Where a command is, as far as this client knows.</summary>
public enum ScryCommandStatus
{
    /// <summary>
    /// Accepted, and still being handled when the client stopped waiting for it. Its outcome arrives
    /// on <see cref="ScryCommandOutcome.Completion"/>, and meanwhile it is listed in
    /// <see cref="ScryClient.PendingWork"/>.
    /// </summary>
    Pending,

    /// <summary>
    /// Handled. What it wrote reaches every live query that reads it, on the live query's own schedule —
    /// before this outcome arrives or after it, in no set order.
    /// </summary>
    Completed,

    /// <summary>
    /// Accepted and then not done — its handler refused or failed, or its target was gone by the time
    /// it was sent — with the reason on <see cref="ScryCommandOutcome.Error"/>.
    /// </summary>
    Failed,

    /// <summary>
    /// Lost track of: the connection it was answered on ended, and the server no longer holds its
    /// outcome — retained too long, pruned, or asked of a node that never held it. It may have run.
    /// </summary>
    Unknown
}
