/// <summary>
/// Answers <see cref="SensitiveWalk"/>'s question from the schema: is this member path, off this
/// source, one the model marked <c>[Sensitive]</c>?
/// </summary>
/// <remarks>
/// The counterpart to the client's own resolver, and deliberately as blunt in the same places. Where
/// the walk cannot say which row a path is read off — after a flatten, a group, a join — this answers
/// with whether <b>any</b> allow-listed type marks a member of that name, which is what the client
/// does with the same question. The bluntness is what keeps the two agreeing: a server that resolved
/// more precisely than the client would refuse queries the client had no way to know to send as a
/// body.
/// </remarks>
sealed class SensitiveSchema(Schema schema)
{
    // Every marked member name, off any allow-listed type. Built with the schema rather than on the
    // first unresolved path: the processor is a singleton, and two first requests filling a lazy table
    // at once would race on it.
    readonly HashSet<string> anyName = schema.Types
        .SelectMany(_ => _.Members.Values)
        .Where(_ => _.Sensitive)
        .Select(_ => _.Name)
        .ToHashSet(StringComparer.Ordinal);

    public bool IsSensitive(string? source, IReadOnlyList<string> path)
    {
        if (source is null ||
            !schema.TryGetSource(source, out var resolved) ||
            !schema.TryGetType(resolved.ClrType, out var meta))
        {
            return Unresolved(path);
        }

        // An empty path asks about the source itself: what a query with no Select returns.
        if (path.Count == 0)
        {
            return meta.Sensitive || meta.Members.Values.Any(_ => _.Sensitive);
        }

        foreach (var segment in path)
        {
            if (meta.Sensitive)
            {
                return true;
            }

            if (!meta.TryGetMember(segment, out var member))
            {
                // Not a member of this type. Validation will have its own say about that; here the
                // path is simply one this walk could not follow.
                return Unresolved(path);
            }

            if (member.Sensitive)
            {
                return true;
            }

            if (!schema.TryGetType(member.Target, out var following))
            {
                // A scalar leaf, which is where a path ends anyway.
                return false;
            }

            meta = following;
        }

        return meta.Sensitive;
    }

    bool Unresolved(IReadOnlyList<string> path) =>
        path.Any(anyName.Contains);
}
