// The operator level: one wire QueryOp rendered back into one snippet operator, inverting
// QueryTranslator's operator switch.
// ReSharper disable TailRecursiveCall
partial class QueryRenderer
{
    // A statement rather than an expression, since every arm appends instead of producing a value.
    // The exhaustiveness a switch expression would give lives in IsTerminal, which is where the
    // compiler stops a new operator from slipping past unspelled.
    void RenderOp(QueryOp op)
    {
        switch (op)
        {
            case WhereOp where:
                builder.Append(".Where(");
                Lambda(where.Predicate);
                builder.Append(')');
                return;

            case OrderByOp orderBy:
                Ordering(orderBy.Descending ? "OrderByDescending" : "OrderBy", orderBy.Key);
                return;

            case ThenByOp thenBy:
                Ordering(thenBy.Descending ? "ThenByDescending" : "ThenBy", thenBy.Key);
                return;

            case SkipOp skip:
                builder.Append(".Skip(").Append(skip.Count.ToString(CultureInfo.InvariantCulture)).Append(')');
                return;

            case TakeOp take:
                builder.Append(".Take(").Append(take.Count.ToString(CultureInfo.InvariantCulture)).Append(')');
                return;

            case DistinctOp:
                builder.Append(".Distinct()");
                return;

            case ReverseOp:
                builder.Append(".Reverse()");
                return;

            case OfTypeOp ofType:
                RenderOfType(ofType);
                return;

            case SelectManyOp many:
                RenderSelectMany(many);
                return;

            case GroupByOp groupBy:
                RenderGroupBy(groupBy);
                return;

            case SelectOp select:
                RenderSelect(select);
                return;

            case JoinOp join:
                RenderJoin(join);
                return;

            case SetOp set:
                RenderSet(set);
                return;

            // A terminal never reaches here — Render splits the trailing one off and refuses any
            // other — and neither does an operator no arm above spells.
            default:
                throw Refuse(RenderRefusal.UnsupportedShape);
        }
    }

    void Ordering(string method, Node key)
    {
        builder.Append('.').Append(method).Append('(');
        Lambda(key);
        builder.Append(')');
    }

    void RenderOfType(OfTypeOp op)
    {
        currentModel = SensitiveModel.ModelFor(op.Type) ?? throw Refuse(RenderRefusal.UnresolvedModel);
        builder.Append(".OfType<").Append(currentModel.Name).Append(">()");
    }

    void RenderSelectMany(SelectManyOp op)
    {
        builder.Append(".SelectMany(_ => _.");
        AppendPath(op.Path);
        builder.Append(')');
        currentModel = ElementModel(currentModel, op.Path);
    }

    // A lambda whose body is one node, in whichever context the pipeline is in: a plain row (`_`),
    // or the group a GroupBy left (`g`).
    void Lambda(Node body)
    {
        var scope = CurrentScope();
        builder.Append(scope.Parameter).Append(" => ");
        RenderNode(body, scope);
    }

    Scope CurrentScope()
    {
        if (grouped)
        {
            return new("g", currentModel, Grouped: true, Depth: 0);
        }

        return new("_", currentModel, Grouped: false, Depth: 0);
    }

    void AppendPath(IReadOnlyList<string> path)
    {
        for (var i = 0; i < path.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('.');
            }

            builder.Append(path[i]);
        }
    }

    void RenderGroupBy(GroupByOp op)
    {
        var scope = new Scope("_", currentModel, Grouped: false, Depth: 0);
        grouped = true;
        groupKeys = op.Keys;

        if (op.Keys.Count == 0)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        builder.Append(".GroupBy(_ => ");
        if (op.Keys.Count == 1)
        {
            groupKeyNames = null;
            RenderNode(op.Keys[0], scope);
            builder.Append(')');
            return;
        }

        // A composite key becomes an anonymous type. The names never reach the wire — the parts
        // travel by position — so they only have to be consistent between the key and the reads of
        // it later in this same snippet: a member part is named by its last segment (deduplicated),
        // a computed part by its position.
        var names = new List<string>();
        builder.Append("new { ");
        for (var i = 0; i < op.Keys.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

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
            if (key is not MemberNode plain ||
                plain.Path[^1] != name)
            {
                builder.Append(name).Append(" = ");
            }

            RenderNode(key, scope);
        }

        builder.Append(" })");
        groupKeyNames = names;
    }

    void RenderSelect(SelectOp op)
    {
        var scope = CurrentScope();
        builder.Append(".Select(").Append(scope.Parameter).Append(" => ");
        RenderProjection(op.Projection, scope);
        builder.Append(')');

        // Whatever the projection built, the row is now an anonymous shape no model describes.
        grouped = false;
        groupKeys = null;
        groupKeyNames = null;
        currentModel = null;
    }

    void RenderProjection(Projection projection, Scope scope)
    {
        if (projection.Members.Count == 0)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        builder.Append("new { ");
        for (var i = 0; i < projection.Members.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var member = projection.Members[i];
            switch (member.Value)
            {
                case NodeValue node:
                    RenderNamed(member.Name, node.Node, scope);
                    break;
                case NestedValue nested:
                    builder.Append(member.Name).Append(" = ");
                    RenderNested(nested, scope);
                    break;
            }
        }

        builder.Append(" }");
    }

    // An anonymous-type member, written without its name wherever C# would infer that same name.
    // Whether it would is a question about the rendered text, so the value goes down first and the
    // name is spliced in front of it only once the text says one is needed.
    void RenderNamed(string name, Node value, Scope scope)
    {
        var start = builder.Length;
        RenderNode(value, scope);
        if (!Shorthand(start, name))
        {
            builder.Insert(start, $"{name} = ");
        }
    }

    // Whether the expression just appended is a plain member chain whose trailing identifier is the
    // name C# would infer from it.
    bool Shorthand(int start, string name)
    {
        var identifier = -1;
        for (var i = start; i < builder.Length; i++)
        {
            var character = builder[i];
            if (character == '.')
            {
                identifier = i + 1;
                continue;
            }

            if (!char.IsLetterOrDigit(character) &&
                character != '_')
            {
                return false;
            }
        }

        if (identifier < 0 ||
            builder.Length - identifier != name.Length)
        {
            return false;
        }

        for (var i = 0; i < name.Length; i++)
        {
            if (builder[identifier + i] != name[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A nested projection re-spelled as the object construction it came from, each member reading
    /// its full path again. The forward pass re-derives the navigation prefix from those paths, so
    /// this only renders when that derivation lands back on the wire's own prefix — a deeper shared
    /// prefix would rebase the members differently and change the bytes.
    /// </summary>
    void RenderNested(NestedValue nested, Scope scope)
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

        builder.Append("new { ");
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var (name, value) = members[i];
            RenderNamed(name, value, scope);
        }

        builder.Append(" }");
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

    void RenderJoin(JoinOp op)
    {
        var method = op.Kind switch
        {
            JoinKind.Inner => "Join",
            JoinKind.Left => "LeftJoin",
            JoinKind.Right => "RightJoin",
            JoinKind.Group => "GroupJoin",
            _ => throw Refuse(RenderRefusal.UnsupportedShape)
        };

        if (op.Result.Count == 0)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var innerModel = SensitiveModel.ModelFor(op.Root);
        var innerScope = new Scope("x", innerModel, Grouped: false, Depth: 1);
        var outerScope = new Scope("_", currentModel, Grouped: false, Depth: 0);

        builder.Append('.').Append(method).Append("(Query.").Append(op.Root);
        AppendSideOps(op.InnerOps, op.InnerPredicate, innerScope);

        builder.Append(", _ => ");
        RenderJoinKey(op.OuterKey, outerScope);
        builder.Append(", x => ");
        RenderJoinKey(op.InnerKey, innerScope);

        builder.Append(", (_, x) => new { ");
        for (var i = 0; i < op.Result.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var member = op.Result[i];
            if (member.Aggregate is { } aggregate)
            {
                if (op.Kind != JoinKind.Group)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                builder.Append(member.Name).Append(" = ");
                RenderAggregate(aggregate, "x", innerModel, depth: 1);
                continue;
            }

            if (member.Path.Count == 0)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            if (member.Path[^1] != member.Name)
            {
                builder.Append(member.Name).Append(" = ");
            }

            builder.Append(member.Side == JoinSide.Outer ? "_." : "x.");
            AppendPath(member.Path);
        }

        builder.Append(" })");

        // The joined pair is an anonymous shape from here on.
        currentModel = null;
    }

    // A composite key becomes an anonymous type; C# demands both sides construct the same one, so
    // the parts are named by position on both. A single part never travels as a composite.
    void RenderJoinKey(Node key, Scope scope)
    {
        if (key is not CompositeKeyNode composite)
        {
            RenderNode(key, scope);
            return;
        }

        if (composite.Parts.Count < 2)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        builder.Append("new { ");
        for (var i = 0; i < composite.Parts.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append('K').Append(i.ToString(CultureInfo.InvariantCulture)).Append(" = ");
            RenderNode(composite.Parts[i], scope);
        }

        builder.Append(" }");
    }

    void RenderSet(SetOp op)
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

        builder.Append('.').Append(method).Append("(Query.").Append(op.Root);
        AppendSideOps(op.OperandOps, op.Predicate, scope);
        builder.Append(".Select(x => ");
        RenderProjection(op.Projection, scope);
        builder.Append("))");
    }

    // The pipeline a join's inner side or a set operand carries: filters, then an ordering bounded
    // by paging — or, in the older spelling, a single folded predicate.
    void AppendSideOps(IReadOnlyList<QueryOp>? ops, Node? predicate, Scope scope)
    {
        if (ops is null)
        {
            if (predicate is not null)
            {
                SideLambda("Where", predicate, scope);
            }

            return;
        }

        foreach (var op in ops)
        {
            switch (op)
            {
                case WhereOp where:
                    SideLambda("Where", where.Predicate, scope);
                    break;
                case OrderByOp orderBy:
                    SideLambda(orderBy.Descending ? "OrderByDescending" : "OrderBy", orderBy.Key, scope);
                    break;
                case ThenByOp thenBy:
                    SideLambda(thenBy.Descending ? "ThenByDescending" : "ThenBy", thenBy.Key, scope);
                    break;
                case SkipOp skip:
                    builder.Append(".Skip(").Append(skip.Count.ToString(CultureInfo.InvariantCulture)).Append(')');
                    break;
                case TakeOp take:
                    builder.Append(".Take(").Append(take.Count.ToString(CultureInfo.InvariantCulture)).Append(')');
                    break;
                default:
                    throw Refuse(RenderRefusal.UnsupportedShape);
            }
        }
    }

    void SideLambda(string method, Node body, Scope scope)
    {
        builder.Append('.').Append(method).Append('(').Append(scope.Parameter).Append(" => ");
        RenderNode(body, scope);
        builder.Append(')');
    }
}
