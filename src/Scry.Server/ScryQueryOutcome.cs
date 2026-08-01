namespace Scry;

/// <summary>How a query ended, as reported to <see cref="IScryAuditor"/>.</summary>
public enum ScryQueryOutcome
{
    /// <summary>Validated, executed, and every row of the result delivered.</summary>
    Success,

    /// <summary>
    /// Rejected — by validation (the allow-list, a resource limit, a malformed pipeline) before
    /// anything ran, or, for a stream, by <see cref="ScryOptions.MaxStreamRows"/> mid-read. Either
    /// way the rejection is deliberate and its message is safe to show a client.
    /// </summary>
    Rejected,

    /// <summary>
    /// Validation passed but execution threw — a provider failure, a faulted policy. The client saw
    /// a generic error; <see cref="ScryAuditEntry.Error"/> carries the real message.
    /// </summary>
    Failed,

    /// <summary>
    /// A streamed read that ended before every row was delivered: canceled, or its consumer stopped
    /// reading.
    /// </summary>
    Canceled
}
