// Reading and rewriting the member paths inside a translated node, which is how a nested projection
// finds the navigation it descends into and rebases onto it.
sealed partial class QueryTranslator
{
    // ReSharper disable TailRecursiveCall
    /// <summary>
    /// Gathers every member path an expression reads from the row. The predicate and selector of a
    /// subquery or a membership test are skipped: they are rooted at the other sequence's element, not
    /// at the row, so they say nothing about which navigation the enclosing projection descends into.
    /// </summary>
    static void CollectPaths(Node node, List<IReadOnlyList<string>> paths)
    {
        switch (node)
        {
            case MemberNode member:
                paths.Add(member.Path);
                break;
            case SubqueryNode subquery:
                paths.Add(subquery.Path);
                break;
            case InSourceNode inSource:
                CollectPaths(inSource.Value, paths);
                break;
            case BinaryNode binary:
                CollectPaths(binary.Left, paths);
                CollectPaths(binary.Right, paths);
                break;
            case UnaryNode unary:
                CollectPaths(unary.Operand, paths);
                break;
            case CollateNode collate:
                CollectPaths(collate.Target, paths);
                break;
            case ConditionalNode conditional:
                CollectPaths(conditional.Test, paths);
                CollectPaths(conditional.IfTrue, paths);
                CollectPaths(conditional.IfFalse, paths);
                break;
            case CallNode call:
                CollectPaths(call.Target, paths);
                foreach (var argument in call.Arguments)
                {
                    CollectPaths(argument, paths);
                }

                break;
        }
    }
    // ReSharper restore TailRecursiveCall

    /// <summary>
    /// Rebases an expression onto the navigation the nested projection descends into, by dropping the
    /// shared leading segments from every path it reads.
    /// </summary>
    static Node StripPrefix(Node node, int prefix) =>
        node switch
        {
            MemberNode member => new MemberNode([..member.Path.Skip(prefix)]),
            SubqueryNode subquery => subquery with { Path = [..subquery.Path.Skip(prefix)] },
            InSourceNode inSource => inSource with { Value = StripPrefix(inSource.Value, prefix) },
            BinaryNode binary => new BinaryNode(binary.Op, StripPrefix(binary.Left, prefix), StripPrefix(binary.Right, prefix)),
            UnaryNode unary => new UnaryNode(unary.Op, StripPrefix(unary.Operand, prefix)),
            CollateNode collate => collate with { Target = StripPrefix(collate.Target, prefix) },
            ConditionalNode conditional => new ConditionalNode(
                StripPrefix(conditional.Test, prefix),
                StripPrefix(conditional.IfTrue, prefix),
                StripPrefix(conditional.IfFalse, prefix)),
            CallNode call => new CallNode(
                call.Function,
                StripPrefix(call.Target, prefix),
                [..call.Arguments.Select(_ => StripPrefix(_, prefix))]),
            _ => node
        };

    // The navigation a nested projection descends into: the shared leading segments of every member
    // path, stopping before any member's final (scalar) segment so each keeps a non-empty relative
    // path. Empty means the members do not share a single navigation, which is unsupported.
    static List<string> CommonNavigationPrefix(IReadOnlyList<IReadOnlyList<string>> paths)
    {
        // Reading nothing from the row shares no navigation either, and the caller says so. Checked
        // here rather than left to the loop, whose All() is vacuously true on an empty list and would
        // then index into it — which is how a node type CollectPaths has no case for would surface.
        if (paths.Count == 0)
        {
            return [];
        }

        var prefix = new List<string>();
        while (paths.All(_ => _.Count > prefix.Count + 1))
        {
            var segment = paths[0][prefix.Count];
            if (paths.Any(_ => _[prefix.Count] != segment))
            {
                break;
            }

            prefix.Add(segment);
        }

        return prefix;
    }
}
