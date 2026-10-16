/// <summary>
/// What the answer to a query was recorded as, kept so that a later <c>304</c> for the same query can
/// be recorded without running it. Whether a query is recorded, and what its record names, depend on
/// the query and the server's settings and on nothing else: not on who asks, and not on the rows.
/// </summary>
/// <param name="Source">The source the answer was about.</param>
/// <param name="Kind">The kind of answer it was.</param>
/// <param name="Request">The address of the request, which the store holds from the first answer.</param>
/// <param name="Shape">The address of the members it read.</param>
/// <param name="Sensitive">Whether it returned a member the model marks sensitive.</param>
sealed record DisclosureMemo(
    string Source,
    ScryDisclosureKind Kind,
    ScryDisclosureAddress? Request,
    ScryDisclosureAddress? Shape,
    bool Sensitive)
{
    /// <summary>The answer read nothing but sources the host left out of the record, and was not recorded.</summary>
    public static DisclosureMemo Unrecorded { get; } = new("", ScryDisclosureKind.List, null, null, false);
}
