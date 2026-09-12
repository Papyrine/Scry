/// <summary>
/// The lexical context a node renders in: the lambda parameter it reads the row through, the model
/// describing that row (null where none is known), whether the row is a group, and how deep in
/// nested lambdas the render is — which is what names the next one (<c>x</c>, then <c>y</c>).
/// </summary>
sealed record Scope(string Parameter, Type? Model, bool Grouped, int Depth)
{
    public string NestedParameter =>
        Depth switch
        {
            0 => "x",
            1 => "y",
            _ => $"z{Depth - 1}"
        };
}

// The node level: one wire expression rendered back into the C# that captures to it, inverting
// QueryTranslator's expression and method dispatch.
partial class QueryRenderer
{
    void RenderNode(Node node, Scope scope)
    {
        switch (node)
        {
            case MemberNode member:
                RenderMember(member, scope);
                return;

            case ElementNode:
                builder.Append(scope.Parameter);
                return;

            case ConstNode constant:
                builder.Append(RenderConst(constant, expected: null));
                return;

            case BinaryNode binary:
                RenderBinary(binary, scope);
                return;

            case UnaryNode unary:
                if (!Reads(unary.Operand))
                {
                    // The compiler would fold a unary over a constant, changing the bytes.
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                builder.Append(unary.Op == UnaryOp.Not ? "!(" : "-(");
                RenderNode(unary.Operand, scope);
                builder.Append(')');
                return;

            case ConditionalNode conditional:
                builder.Append('(');
                Operand(conditional.Test, scope);
                builder.Append(" ? ");
                Operand(conditional.IfTrue, scope);
                builder.Append(" : ");
                Operand(conditional.IfFalse, scope);
                builder.Append(')');
                return;

            case CallNode call:
                RenderCall(call, scope);
                return;

            case SubqueryNode subquery:
                RenderSubquery(subquery, scope);
                return;

            case InSourceNode inSource:
                RenderInSource(inSource, scope);
                return;

            case AggregateNode aggregate when scope.Grouped:
                RenderAggregate(aggregate, scope.Parameter, scope.Model, scope.Depth);
                return;

            case GroupKeyNode key when scope.Grouped:
                RenderGroupKey(key, scope);
                return;

            default:
                // CollateNode anywhere but under the comparisons that spell it, a group construct
                // outside a group, or a node this renderer has never heard of.
                throw Refuse(RenderRefusal.UnsupportedShape);
        }
    }

    void RenderMember(MemberNode member, Scope scope)
    {
        if (member.Path.Count == 0)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        // Inside a group the only member the wire can name is one the query grouped by, read back
        // as the group key it became.
        if (scope.Grouped)
        {
            if (groupKeys is null)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            for (var i = 0; i < groupKeys.Count; i++)
            {
                if (groupKeys[i] is MemberNode key &&
                    key.Path.SequenceEqual(member.Path, StringComparer.Ordinal))
                {
                    AppendGroupKey(scope, i);
                    return;
                }
            }

            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        builder.Append(scope.Parameter).Append('.');
        AppendPath(member.Path);
    }

    void RenderGroupKey(GroupKeyNode key, Scope scope)
    {
        if (groupKeys is null ||
            key.Index < 0 ||
            key.Index >= groupKeys.Count)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        AppendGroupKey(scope, key.Index);
    }

    void AppendGroupKey(Scope scope, int index)
    {
        builder.Append(scope.Parameter).Append(".Key");
        if (groupKeyNames is not null)
        {
            builder.Append('.').Append(groupKeyNames[index]);
        }
    }

    void RenderBinary(BinaryNode node, Scope scope)
    {
        // Both sides constant would be folded by the compiler into a single constant, so the
        // rendered snippet could not reproduce the wire's two operands.
        if (!Reads(node.Left) && !Reads(node.Right))
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        // Equality under a collation is spelled as the Equals overload that asked for it.
        if (node is
            {
                Op: BinaryOp.Equal,
                Left: CollateNode collate
            })
        {
            Target(collate.Target, scope);
            builder.Append(".Equals(");
            if (node.Right is ConstNode constant)
            {
                builder.Append(RenderConst(constant, typeof(string)));
            }
            else
            {
                Operand(node.Right, scope);
            }

            builder.Append(", ").Append(Comparison(collate.Match)).Append(')');
            return;
        }

        if (node.Left is CollateNode || node.Right is CollateNode)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var symbol = node.Op switch
        {
            BinaryOp.Equal => "==",
            BinaryOp.NotEqual => "!=",
            BinaryOp.LessThan => "<",
            BinaryOp.LessThanOrEqual => "<=",
            BinaryOp.GreaterThan => ">",
            BinaryOp.GreaterThanOrEqual => ">=",
            BinaryOp.AndAlso => "&&",
            BinaryOp.OrElse => "||",
            BinaryOp.Add => "+",
            BinaryOp.Subtract => "-",
            BinaryOp.Multiply => "*",
            BinaryOp.Divide => "/",
            BinaryOp.Modulo => "%",
            BinaryOp.Coalesce => "??",
            _ => throw Refuse(RenderRefusal.UnsupportedShape)
        };

        Side(node.Left, node.Right);
        builder.Append(' ').Append(symbol).Append(' ');
        Side(node.Right, node.Left);
        return;

        void Side(Node side, Node other)
        {
            if (side is ConstNode constant)
            {
                builder.Append(RenderConst(constant, InferType(other, scope)));
                return;
            }

            // A comparison of an enum-typed member against a numeric constant was written through a
            // cast the wire stripped; put the cast back so the snippet compiles to the same bytes.
            if (other is ConstNode {Tag: ClrTypeTag.Int32 or ClrTypeTag.Int64} &&
                InferType(side, scope) is { } inferred)
            {
                var underlying = Nullable.GetUnderlyingType(inferred);
                if ((underlying ?? inferred).IsEnum)
                {
                    builder.Append(underlying is null ? "(int)" : "(int?)");
                }
            }

            Operand(side, scope);
        }
    }

    // An operand keeps its own parentheses where the surrounding operator would otherwise re-group
    // it. Extra parentheses never change the captured tree, so grouping errs toward wrapping.
    void Operand(Node node, Scope scope)
    {
        if (node is BinaryNode or ConditionalNode)
        {
            builder.Append('(');
            RenderNode(node, scope);
            builder.Append(')');
            return;
        }

        RenderNode(node, scope);
    }

    // The receiver of an instance call has to be a primary expression; anything composite — and a
    // bare literal, whose dot the lexer would eat — is wrapped.
    void Target(Node node, Scope scope)
    {
        if (node is BinaryNode or ConditionalNode or UnaryNode or ConstNode)
        {
            builder.Append('(');
            RenderNode(node, scope);
            builder.Append(')');
            return;
        }

        RenderNode(node, scope);
    }

    // Whether a node reads the row at all. A subtree that reads nothing is closure state, which the
    // forward pass evaluates into a single constant — so it can never faithfully re-spell a wire
    // node that kept its structure.
    static bool Reads(Node node) =>
        node switch
        {
            MemberNode or ElementNode or SubqueryNode or InSourceNode or AggregateNode or GroupKeyNode or CompositeKeyNode => true,
            ConstNode => false,
            BinaryNode binary => Reads(binary.Left) || Reads(binary.Right),
            UnaryNode unary => Reads(unary.Operand),
            CollateNode collate => Reads(collate.Target),
            ConditionalNode conditional => Reads(conditional.Test) || Reads(conditional.IfTrue) || Reads(conditional.IfFalse),
            CallNode call => Reads(call.Target) || call.Arguments.Any(Reads),
            _ => true
        };

    static string Comparison(StringMatch match)
    {
        if (match == StringMatch.CaseSensitive)
        {
            return "StringComparison.Ordinal";
        }

        return "StringComparison.OrdinalIgnoreCase";
    }

    void RenderSubquery(SubqueryNode subquery, Scope scope)
    {
        if (scope.Grouped ||
            subquery.Path.Count == 0)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var elementModel = Walk(scope.Model, subquery.Path) is { } property
            ? SensitiveModel.Element(property.PropertyType)
            : null;
        var inner = new Scope(scope.NestedParameter, elementModel, Grouped: false, Depth: scope.Depth + 1);

        builder.Append(scope.Parameter).Append('.');
        AppendPath(subquery.Path);

        switch (subquery.Function)
        {
            case SubqueryFn.Any:
                Fold("Any", subquery.Predicate);
                return;

            case SubqueryFn.All:
                if (subquery.Predicate is null)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                Fold("All", subquery.Predicate);
                return;

            case SubqueryFn.Count:
                Fold("Count", subquery.Predicate);
                return;

            case SubqueryFn.Sum or SubqueryFn.Average or SubqueryFn.Min or SubqueryFn.Max:
                if (subquery.Selector is null)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                if (subquery.Predicate is { } predicate)
                {
                    Fold("Where", predicate);
                }

                Fold(subquery.Function.ToString(), subquery.Selector is ElementNode ? null : subquery.Selector);
                return;

            default:
                throw Refuse(RenderRefusal.UnsupportedShape);
        }

        void Fold(string name, Node? body)
        {
            builder.Append('.').Append(name).Append('(');
            if (body is not null)
            {
                builder.Append(inner.Parameter).Append(" => ");
                RenderNode(body, inner);
            }

            builder.Append(')');
        }
    }

    void RenderInSource(InSourceNode inSource, Scope scope)
    {
        var sourceModel = SensitiveModel.ModelFor(inSource.Root);
        var inner = new Scope(scope.NestedParameter, sourceModel, Grouped: false, Depth: scope.Depth + 1);

        builder.Append("Query.").Append(inSource.Root);
        if (inSource.Predicate is { } predicate)
        {
            SideLambda("Where", predicate, inner);
        }

        SideLambda("Select", inSource.Selector, inner);
        builder.Append(".Contains(");
        RenderNode(inSource.Value, scope);
        builder.Append(')');
    }

    /// <summary>
    /// An aggregate folding a group, in the grammar the forward pass reads back:
    /// <c>g [.Where(x =&gt; P)] [.Select(x =&gt; S) [.Distinct()]] .Fold(…)</c> — with
    /// <c>Count(x =&gt; P)</c> abbreviating the filtered count, and <c>string.Join</c> as the text
    /// fold.
    /// </summary>
    void RenderAggregate(AggregateNode aggregate, string group, Type? elementModel, int depth)
    {
        var inner = new Scope(depth == 0 ? "x" : "y", elementModel, Grouped: false, Depth: depth + 1);

        if (aggregate.Function == AggregateFn.Join)
        {
            if (aggregate.Predicate is not null ||
                aggregate.Distinct ||
                aggregate.Selector is not MemberNode)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            builder
                .Append("string.Join(")
                .Append(CSharpLiteral.String(aggregate.Separator ?? ""))
                .Append(", ")
                .Append(group);
            SideLambda("Select", aggregate.Selector, inner);
            builder.Append(')');
            return;
        }

        builder.Append(group);
        if (aggregate.Predicate is { } predicate)
        {
            // A bare filtered count folds the predicate into Count itself, which is the exact
            // abbreviation the forward pass records the same way.
            var abbreviated = aggregate is {Function: AggregateFn.Count, Distinct: false, Selector: null};
            SideLambda(abbreviated ? "Count" : "Where", predicate, inner);
            if (abbreviated)
            {
                return;
            }
        }

        if (aggregate.Function == AggregateFn.Count)
        {
            if (!aggregate.Distinct)
            {
                if (aggregate.Selector is not null)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                builder.Append(".Count()");
                return;
            }

            if (aggregate.Selector is null or ElementNode)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            SideLambda("Select", aggregate.Selector, inner);
            builder.Append(".Distinct().Count()");
            return;
        }

        if (aggregate.Function is not (AggregateFn.Sum or AggregateFn.Average or AggregateFn.Min or AggregateFn.Max) ||
            aggregate.Selector is null or ElementNode)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var fold = aggregate.Function.ToString();
        if (aggregate.Distinct)
        {
            SideLambda("Select", aggregate.Selector, inner);
            builder.Append(".Distinct().").Append(fold).Append("()");
            return;
        }

        SideLambda(fold, aggregate.Selector, inner);
    }
}
