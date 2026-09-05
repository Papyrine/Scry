// The attachment leaves a projection may carry, and the keys each one is matched back to.
sealed partial class QueryTranslator
{
    // Attachment members met while translating the projection, resolved against it once it is whole:
    // the keys an attachment needs are sibling members of the same projection, so none of them is
    // known to be present until every member has been translated.
    readonly List<Pending> pendingAttachments = [];

    /// <summary>An attachment leaf met in a projection, before its keys have been looked for.</summary>
    /// <param name="Target">Where it sits in the projected object.</param>
    /// <param name="Prefix">
    /// The member path of the row it hangs off — empty for the query's own row, or the navigation a
    /// nested projection descended into. Its key members are read relative to this.
    /// </param>
    /// <param name="Root">The name of the source the attachment is fetched from.</param>
    /// <param name="Member">The attachment member on that source's row.</param>
    /// <param name="Keys">The row's key members, named relative to <paramref name="Prefix"/>.</param>
    sealed record Pending(
        IReadOnlyList<string> Target,
        IReadOnlyList<string> Prefix,
        string Root,
        string Member,
        IReadOnlyList<string> Keys);

    /// <summary>
    /// Records an attachment leaf and reports that it was one, so the caller leaves it out of the wire
    /// projection. Nothing is validated here: whether its keys were projected too is a question about
    /// the whole projection, answered once every member has been seen.
    /// </summary>
    bool TryAttachment(
        Expression expression,
        ParameterExpression parameter,
        bool grouped,
        IReadOnlyList<string> target,
        IReadOnlyList<string> prefix)
    {
        if (expression is not MemberExpression member ||
            member.Type != typeof(ScryAttachment) ||
            !IsRooted(member, parameter))
        {
            return false;
        }

        // A grouped projection reads the group, not a row — there is no single row left for a key to
        // identify, and the aggregate the group folds to has no attachment either.
        if (grouped)
        {
            throw new NotSupportedException(
                $"Attachment '{member.Member.Name}' cannot be projected out of a group. A group is many rows folded to one, so there is no row key to fetch an attachment by.");
        }

        var path = MemberPath(member);
        var declaring = member.Expression!.Type;
        var (root, keys) = ScryModels.Fetching(declaring, member.Member.Name);

        // The row the attachment hangs off: the query's own where the path is a bare member, or the
        // navigation the path traversed to reach it.
        var owner = prefix.Concat(path.Take(path.Count - 1)).ToList();
        pendingAttachments.Add(new(target, owner, root, member.Member.Name, keys));
        return true;
    }

    /// <summary>
    /// Matches every attachment met in the projection to the key members it is fetched by, which must
    /// have been projected as leaves of the same row. A missing one is refused here rather than
    /// producing a handle that would fail at fetch time with nothing to say why.
    /// </summary>
    IReadOnlyList<AttachmentBinding> ResolveAttachments(IReadOnlyList<QueryOp> ops)
    {
        if (pendingAttachments.Count == 0)
        {
            return [];
        }

        // These rewrite what a row is — deduplicated, flattened, combined, or built from two sources —
        // so a key projected beside an attachment no longer identifies one row of one source.
        if (ops.FirstOrDefault(_ => _ is
                DistinctOp or
                SelectManyOp or
                JoinOp or
                SetOp or
                GroupByOp)
            is { } refused)
        {
            throw new NotSupportedException(
                $"An attachment cannot be carried through {refused.GetType().Name.Replace("Op", "")}. The result's rows no longer correspond to single rows of the source the attachment is fetched from.");
        }

        var projection = ops.OfType<SelectOp>().Single().Projection;
        var leaves = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        CollectLeaves(projection, [], leaves);

        var bindings = new List<AttachmentBinding>(pendingAttachments.Count);
        foreach (var pending in pendingAttachments)
        {
            var sources = new List<IReadOnlyList<string>>(pending.Keys.Count);
            foreach (var key in pending.Keys)
            {
                var wanted = pending.Prefix.Append(key).ToList();
                if (!leaves.TryGetValue(string.Join('.', wanted), out var source))
                {
                    throw new NotSupportedException(
                        $"Attachment '{pending.Member}' needs '_.{string.Join('.', wanted)}' projected beside it: an attachment is fetched by its row's key, so the key has to come back with the row. Add it to the projection.");
                }

                sources.Add(source);
            }

            bindings.Add(new(pending.Target, pending.Root, pending.Member, sources));
        }

        return bindings;
    }

    // Every member path the projection reads, mapped to where its value lands in the result object.
    // Only plain member reads are collected: a computed leaf is not a key, whatever it was computed
    // from, so one cannot stand in for the key an attachment names.
    static void CollectLeaves(
        Projection projection,
        IReadOnlyList<string> memberPrefix,
        Dictionary<string, IReadOnlyList<string>> leaves,
        IReadOnlyList<string>? outputPrefix = null)
    {
        foreach (var member in projection.Members)
        {
            var output = (outputPrefix ?? []).Append(member.Name).ToList();
            switch (member.Value)
            {
                case NodeValue {Node: MemberNode node}:
                    leaves[string.Join('.', memberPrefix.Concat(node.Path))] = output;
                    break;

                case NestedValue nested:
                    CollectLeaves(nested.Projection, [.. memberPrefix, .. nested.Path], leaves, output);
                    break;
            }
        }
    }
}
