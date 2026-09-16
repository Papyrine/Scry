namespace Scry;

/// <summary>
/// A limit one query came close to without exceeding, reported when
/// <see cref="ScryOptions.LimitWatchFraction" /> is set. See
/// <see cref="ScryAuditEntry.ApproachedLimits" />.
/// </summary>
/// <param name="Limit">
/// The name of the option, for example <c>MaxExpressionNodes</c>, so an auditor can group by it
/// without matching on prose.
/// </param>
/// <param name="Used">What the query used. Never more than <paramref name="Maximum" />, since a
/// query over a limit is rejected rather than reported.</param>
/// <param name="Maximum">The limit it was measured against, as configured.</param>
public sealed record ApproachedLimit(string Limit, int Used, int Maximum);
