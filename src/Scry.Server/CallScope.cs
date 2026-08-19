/// <summary>
/// The per-call state the pipeline threads alongside the query being built: the request-scoped
/// services a policy resolves from, and the HTTP headers it can read and write.
/// </summary>
/// <remarks>
/// Bundled rather than passed as three parameters because every method that needs one needs all
/// three, and they travel together from the transport down to <see cref="ScryPolicyContext"/>. A
/// processor hosted outside the HTTP endpoint supplies empty header dictionaries, so a policy reads
/// nothing and its writes go nowhere rather than faulting.
/// </remarks>
readonly record struct CallScope(
    IServiceProvider Services,
    IHeaderDictionary RequestHeaders,
    IHeaderDictionary ResponseHeaders)
{
    /// <summary>
    /// Where the shapers divert <c>[BinaryTransfer]</c> values, when the transport supplied one. Only
    /// the HTTP endpoints do — multipart is a transport concern, so the public processor surface
    /// leaves this null and stays bit-identical.
    /// </summary>
    public BinaryPartCollector? Binary { get; init; }

    /// <summary>
    /// Whether the request arrived as a URL rather than as a body, which decides whether the sensitive
    /// rule applies to it. Only the query endpoint's GET sets it; a host with no URL to speak of leaves
    /// it false, and nothing changes for one.
    /// </summary>
    public bool FromUrl { get; init; }

    /// <summary>
    /// What each cached row policy answered with during this call. One per call, so the several sites
    /// that can apply the same policy to one query all read the same keys.
    /// </summary>
    public CachedDecisions Cached { get; init; } = new();

    /// <summary>
    /// Whether this call may bring a cached policy's answers up to date, which is every call that
    /// actually reads rows. Building a query without running it neither needs nor earns the work.
    /// </summary>
    public bool EnsureCachedFreshness { get; init; }
}
