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
}
