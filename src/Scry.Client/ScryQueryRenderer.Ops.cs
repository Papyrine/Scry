// The operator level: one wire QueryOp rendered back into one snippet operator, inverting
// QueryTranslator's operator switch.
// ReSharper disable TailRecursiveCall
partial class QueryRenderer
{
    string RenderOp(QueryOp op)
    {
        switch (op)
        {
            case WhereOp where:
                return $".Where({Lambda(where.Predicate)})";

            case OrderByOp orderBy:
                return $".{(orderBy.Descending ? "OrderByDescending" : "OrderBy")}({Lambda(orderBy.Key)})";
            case ThenByOp thenBy:
                return $".{(thenBy.Descending ? "ThenByDescending" : "ThenBy")}({Lambda(thenBy.Key)})";

            case SkipOp skip:
                return $".Skip({skip.Count.ToString(CultureInfo.InvariantCulture)})";
            case TakeOp take:
                return $".Take({take.Count.ToString(CultureInfo.InvariantCulture)})";

            case DistinctOp:
                return ".Distinct()";
            case ReverseOp:
                return ".Reverse()";

            case OfTypeOp ofType:
                currentModel = SensitiveModel.ModelFor(ofType.Type) ?? throw Refuse(RenderRefusal.UnresolvedModel);
                return $".OfType<{currentModel.Name}>()";

            case SelectManyOp many:
            {
                var text = $".SelectMany(_ => _.{string.Join('.', many.Path)})";
                currentModel = ElementModel(currentModel, many.Path);
                return text;
            }

            case GroupByOp groupBy:
                return RenderGroupBy(groupBy);

            case SelectOp select:
                return RenderSelect(select);

            case JoinOp join:
                return RenderJoin(join);

            case SetOp set:
                return RenderSet(set);

            default:
                throw Refuse(RenderRefusal.UnsupportedShape);
        }
    }

    // A lambda whose body is one node, in whichever context the pipeline is in: a plain row (`_`),
    // or the group a GroupBy left (`g`).
    string Lambda(Node body)
    {
        var scope = CurrentScope();
        return $"{scope.Parameter} => {RenderNode(body, scope)}";
    }

    Scope CurrentScope()
    {
        if (grouped)
        {
            return new("g", currentModel, Grouped: true, Depth: 0);
        }

        return new("_", currentModel, Grouped: false, Depth: 0);
    }

    string RenderGroupBy(GroupByOp op)
    {
        var scope = new Scope("_", currentModel, Grouped: false, Depth: 0);
        grouped = true;
        groupKeys = op.Keys;

        if (op.Keys.Count == 0)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        if (op.Keys.Count == 1)
        {
            groupKeyNames = null;
            return $".GroupBy(_ => {RenderNode(op.Keys[0], scope)})";
        }

        // A composite key becomes an anonymous type. The names never reach the wire — the parts
        // travel by position — so they only have to be consistent between the key and the reads of
        // it later in this same snippet: a member part is named by its last segment (deduplicated),
        // a computed part by its position.
        var names = new List<string>();
        var parts = new List<string>();
        for (var i = 0; i < op.Keys.Count; i++)
        {
            var key = op.Keys[i];
            var name = key is MemberNode {Path.Count: > 0} member ? member.Path[^1] : $"Key{i}";
            if (names.Contains(name))
            {
                var suffix = 2;
                while (names.Contains($"{name}{suffix}"))
                {
                    suffix++;
                }

                name = $"{name}{suffix}";
            }

            names.Add(name);
            var value = RenderNode(key, scope);
            parts.Add(
                key is MemberNode plain && plain.Path[^1] == name
                    ? value
                    : $"{name} = {value}");
        }

        groupKeyNames = names;
        return $".GroupBy(_ => new {{ {string.Join(", ", parts)} }})";
    }

    string RenderSelect(SelectOp op)
    {
        var scope = CurrentScope();
        var text = $".Select({scope.Parameter} => {RenderProjection(op.Projection, scope)})";

        // Whatever the projection built, the row is now an anonymous shape no model describes.
        grouped = false;
        groupKeys = null;
        groupKeyNames = null;
        currentModel = null;
        return text;
    }

    string RenderProjection(Projection projection, Scope scope)
    {
        if (projection.Members.Count == 0)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var parts = projection.Members.Select(_ => RenderProjectionMember(_, scope));
        return $"new {{ {string.Join(", ", parts)} }}";
    }

    string RenderProjectionMember(ProjectionMember member, Scope scope)
    {
        switch (member.Value)
        {
            case NodeValue node:
                var text = RenderNode(node.Node, scope);
                return Shorthand(text, member.Name) ? text : $"{member.Name} = {text}";

            case NestedValue nested:
                return $"{member.Name} = {RenderNested(nested, scope)}";

            default:
                throw Refuse(RenderRefusal.UnsupportedShape);
        }
    }

    // Whether an anonymous-type member can drop its name: the expression is a plain member chain
    // whose trailing identifier is the name C# would infer anyway.
    static bool Shorthand(string expression, string name)
    {
        if (!expression.All(_ => char.IsLetterOrDigit(_) || _ is '.' or '_'))
        {
            return false;
        }

        var dot = expression.LastIndexOf('.');
        return dot >= 0 && expression[(dot + 1)..] == name;
    }

    /// <summary>
    /// A nested projection re-spelled as the object construction it came from, each member reading
    /// its full path again. The forward pass re-derives the navigation prefix from those paths, so
    /// this only renders when that derivation lands back on the wire's own prefix — a deeper shared
    /// prefix would rebase the members differently and change the bytes.
    /// </summary>
    string RenderNested(NestedValue nested, Scope scope)
    {
        var members = new List<(string Name, Node Value)>();
        foreach (var member in nested.Projection.Members)
        {
            if (member.Value is not NodeValue node)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            members.Add((member.Name, PrependPrefix(node.Node, nested.Path)));
        }

        var paths = new List<IReadOnlyList<string>>();
        foreach (var (_, value) in members)
        {
            CollectPaths(value, paths);
        }

        if (!DerivedPrefix(paths).SequenceEqual(nested.Path, StringComparer.Ordinal))
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var parts = members.Select(
            member =>
            {
                var text = RenderNode(member.Value, scope);
                return Shorthand(text, member.Name) ? text : $"{member.Name} = {text}";
            });
        return $"new {{ {string.Join(", ", parts)} }}";
    }

    // The inverse of the translator's StripPrefix: every rooted path gets the navigation back.
    static Node PrependPrefix(Node node, IReadOnlyList<string> prefix) =>
        node switch
        {
            MemberNode member => new MemberNode([..prefix, ..member.Path]),
            SubqueryNode subquery => subquery with {Path = [..prefix, ..subquery.Path]},
            InSourceNode inSource => inSource with {Value = PrependPrefix(inSource.Value, prefix)},
            BinaryNode binary => new BinaryNode(binary.Op, PrependPrefix(binary.Left, prefix), PrependPrefix(binary.Right, prefix)),
            UnaryNode unary => new UnaryNode(unary.Op, PrependPrefix(unary.Operand, prefix)),
            CollateNode collate => collate with {Target = PrependPrefix(collate.Target, prefix)},
            ConditionalNode conditional => new ConditionalNode(
                PrependPrefix(conditional.Test, prefix),
                PrependPrefix(conditional.IfTrue, prefix),
                PrependPrefix(conditional.IfFalse, prefix)),
            CallNode call => new CallNode(
                call.Function,
                PrependPrefix(call.Target, prefix),
                [..call.Arguments.Select(_ => PrependPrefix(_, prefix))]),
            _ => node
        };

    // Mirrors the translator's CollectPaths: the member paths the forward pass will read the
    // navigation prefix from. Subquery inner expressions are rooted elsewhere and contribute only
    // their own collection path.
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

    // Mirrors the translator's CommonNavigationPrefix, so the render can predict the rebasing the
    // forward pass will perform.
    static List<string> DerivedPrefix(IReadOnlyList<IReadOnlyList<string>> paths)
    {
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

    string RenderJoin(JoinOp op)
    {
        var method = op.Kind switch
        {
            JoinKind.Inner => "Join",
            JoinKind.Left => "LeftJoin",
            JoinKind.Right => "RightJoin",
            JoinKind.Group => "GroupJoin",
            _ => throw Refuse(RenderRefusal.UnsupportedShape)
        };

        var innerModel = SensitiveModel.ModelFor(op.Root);
        var innerScope = new Scope("x", innerModel, Grouped: false, Depth: 1);
        var source = new StringBuilder("Query.").Append(op.Root);
        AppendSideOps(source, op.InnerOps, op.InnerPredicate, innerScope);

        var outerScope = new Scope("_", currentModel, Grouped: false, Depth: 0);
        var outerKey = RenderJoinKey(op.OuterKey, outerScope);
        var innerKey = RenderJoinKey(op.InnerKey, innerScope);

        var members = new List<string>();
        foreach (var member in op.Result)
        {
            if (member.Aggregate is { } aggregate)
            {
                if (op.Kind != JoinKind.Group)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                members.Add($"{member.Name} = {RenderAggregate(aggregate, "x", innerModel, depth: 1)}");
                continue;
            }

            if (member.Path.Count == 0)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            var root = member.Side == JoinSide.Outer ? "_" : "x";
            var text = $"{root}.{string.Join('.', member.Path)}";
            members.Add(member.Path[^1] == member.Name ? text : $"{member.Name} = {text}");
        }

        if (members.Count == 0)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        // The joined pair is an anonymous shape from here on.
        currentModel = null;
        return $".{method}({source}, _ => {outerKey}, x => {innerKey}, (_, x) => new {{ {string.Join(", ", members)} }})";
    }

    // A composite key becomes an anonymous type; C# demands both sides construct the same one, so
    // the parts are named by position on both. A single part never travels as a composite.
    string RenderJoinKey(Node key, Scope scope)
    {
        if (key is not CompositeKeyNode composite)
        {
            return RenderNode(key, scope);
        }

        if (composite.Parts.Count < 2)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var parts = composite.Parts.Select((part, i) => $"K{i} = {RenderNode(part, scope)}");
        return $"new {{ {string.Join(", ", parts)} }}";
    }

    string RenderSet(SetOp op)
    {
        var method = op.Kind switch
        {
            SetKind.Union => "Union",
            SetKind.Concat => "Concat",
            SetKind.Intersect => "Intersect",
            SetKind.Except => "Except",
            _ => throw Refuse(RenderRefusal.UnsupportedShape)
        };

        var operandModel = SensitiveModel.ModelFor(op.Root);
        var scope = new Scope("x", operandModel, Grouped: false, Depth: 1);
        var operand = new StringBuilder("Query.").Append(op.Root);
        AppendSideOps(operand, op.OperandOps, op.Predicate, scope);
        operand.Append($".Select(x => {RenderProjection(op.Projection, scope)})");
        return $".{method}({operand})";
    }

    // The pipeline a join's inner side or a set operand carries: filters, then an ordering bounded
    // by paging — or, in the older spelling, a single folded predicate.
    void AppendSideOps(StringBuilder builder, IReadOnlyList<QueryOp>? ops, Node? predicate, Scope scope)
    {
        if (ops is not null)
        {
            foreach (var op in ops)
            {
                builder.Append(
                    op switch
                    {
                        WhereOp where => $".Where({scope.Parameter} => {RenderNode(where.Predicate, scope)})",
                        OrderByOp orderBy => $".{(orderBy.Descending ? "OrderByDescending" : "OrderBy")}({scope.Parameter} => {RenderNode(orderBy.Key, scope)})",
                        ThenByOp thenBy => $".{(thenBy.Descending ? "ThenByDescending" : "ThenBy")}({scope.Parameter} => {RenderNode(thenBy.Key, scope)})",
                        SkipOp skip => $".Skip({skip.Count.ToString(CultureInfo.InvariantCulture)})",
                        TakeOp take => $".Take({take.Count.ToString(CultureInfo.InvariantCulture)})",
                        _ => throw Refuse(RenderRefusal.UnsupportedShape)
                    });
            }

            return;
        }

        if (predicate is not null)
        {
            builder.Append($".Where({scope.Parameter} => {RenderNode(predicate, scope)})");
        }
    }
}
