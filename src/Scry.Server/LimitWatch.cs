/// <summary>
/// Measures how close an accepted request came to the limits counted for a request as a whole, so
/// <see cref="ScryOptions.LimitWatchFraction" /> can report the near misses a rejection-only limit
/// leaves no trace of.
/// </summary>
/// <remarks>
/// Deliberately separate from the validator. The gate refuses a request before it walks one, and
/// measuring there would mean carrying a collector through every step of it — and walking a request
/// that is about to be refused. This runs afterwards instead, for a query that was not rejected,
/// only once an auditor exists to read the answer.
///
/// The price of being separate is that each count mirrors a rule the validator also implements, so
/// the two have to stay in step: <c>LimitWatchTests</c> pins each one by measuring a request at
/// exactly its limit and refusing the same request one past it. <see cref="ScryOptions.MaxExpressionDepth" />
/// is left out for want of that: what the validator compares is how many times it recursed, not how
/// deeply the request nests, and only some of its transitions recurse — a mirror of it would drift
/// quietly and report a comfortable number while the gate was at its limit.
/// </remarks>
static class LimitWatch
{
    public static IReadOnlyList<ApproachedLimit>? Measure(QueryRequest request, ScryOptions options)
    {
        if (options.LimitWatchFraction is not { } fraction)
        {
            return null;
        }

        List<ApproachedLimit>? approached = null;

        Add(ref approached, fraction, nameof(ScryOptions.MaxPipelineLength), request.Pipeline.Count, options.MaxPipelineLength);

        var measured = RequestBudget.Measure(request);
        Add(ref approached, fraction, nameof(ScryOptions.MaxExpressionNodes), measured.Nodes, options.MaxExpressionNodes);
        Add(ref approached, fraction, nameof(ScryOptions.MaxCorrelatedSubqueries), measured.Subqueries, options.MaxCorrelatedSubqueries);
        Add(ref approached, fraction, nameof(ScryOptions.MaxNavigationDepth), measured.NavigationDepth, options.MaxNavigationDepth);
        Add(ref approached, fraction, nameof(ScryOptions.MaxProjectionMembers), measured.ProjectionMembers, options.MaxProjectionMembers);
        Add(ref approached, fraction, nameof(ScryOptions.MaxInValues), measured.InValues, options.MaxInValues);

        if (Rows(request.Pipeline) is { } rows)
        {
            Add(ref approached, fraction, nameof(ScryOptions.MaxPageSize), rows, options.MaxPageSize);
        }

        return approached;
    }

    // What the request asked for, which is what MaxPageSize bounds: a Take's count, or a page's size
    // where it named one. A page that named none is the server's own default, so there is nothing a
    // client did to report.
    static int? Rows(IReadOnlyList<QueryOp> pipeline)
    {
        int? rows = null;

        foreach (var op in pipeline)
        {
            int? asked = op switch
            {
                TakeOp take => take.Count,
                PageOp {Size: { } size} => size,
                _ => null
            };

            if (asked is { } value &&
                (rows is null || value > rows))
            {
                rows = value;
            }
        }

        return rows;
    }

    static void Add(ref List<ApproachedLimit>? approached, double fraction, string limit, int used, int maximum)
    {
        if (used < maximum * fraction)
        {
            return;
        }

        approached ??= [];
        approached.Add(new(limit, used, maximum));
    }
}
