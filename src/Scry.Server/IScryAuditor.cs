namespace Scry;

/// <summary>
/// Records every query <see cref="ScryProcessor"/> handles — succeeded, rejected, failed, or
/// canceled. Register any number of implementations in DI; nothing is recorded while none is
/// registered. Resolution happens per query from the provider the processor is called with — the
/// request scope, on the HTTP endpoint — so a scoped auditor can read the current user.
/// </summary>
/// <remarks>
/// Auditors run after the result (or failure) is settled, and they fail closed: an auditor that
/// throws fails the request, because an audit trail that silently drops entries is worse than a
/// failed query. An implementation that must not block should hand the entry to a queue and return.
/// </remarks>
// begin-snippet: auditorInterface
public interface IScryAuditor
{
    /// <summary>Called once per query, after it completes. See <see cref="ScryAuditEntry"/>.</summary>
    void Record(ScryAuditEntry entry);
}
// end-snippet
