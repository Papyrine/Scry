namespace Scry;

/// <summary>
/// What a row a policy denies produces in the response, for one of the positions a row can be read
/// from. Declared per policy, so where several apply each answers for the rows it alone denies.
/// </summary>
public enum DeniedRowMode
{
    /// <summary>
    /// The row is simply not there: filtered out of a list, absent from a single-row result, read as
    /// null through a navigation. The default, and the only non-disclosing answer — a caller cannot
    /// tell a denied row from one that never existed.
    /// </summary>
    Hide,

    /// <summary>
    /// The whole request fails with a permission error (HTTP 403). Deliberately discloses that rows
    /// the caller may not see matched the query, which is the very thing <see cref="Hide"/> withholds;
    /// use it only where "you lack permission" is itself not sensitive.
    /// </summary>
    Error
}
