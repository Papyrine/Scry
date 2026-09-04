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
    string RenderNode(Node node, Scope scope)
    {
        switch (node)
        {
            case MemberNode member:
                return RenderMember(member, scope);

            case ElementNode:
                return scope.Parameter;

            case ConstNode constant:
                return RenderConst(constant, expected: null);

            case BinaryNode binary:
                return RenderBinary(binary, scope);

            case UnaryNode unary:
                if (!Reads(unary.Operand))
                {
                    // The compiler would fold a unary over a constant, changing the bytes.
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                return unary.Op == UnaryOp.Not
                    ? $"!({RenderNode(unary.Operand, scope)})"
                    : $"-({RenderNode(unary.Operand, scope)})";

            case ConditionalNode conditional:
                return $"({Operand(conditional.Test, scope)} ? {Operand(conditional.IfTrue, scope)} : {Operand(conditional.IfFalse, scope)})";

            case CallNode call:
                return RenderCall(call, scope);

            case SubqueryNode subquery:
                return RenderSubquery(subquery, scope);

            case InSourceNode inSource:
                return RenderInSource(inSource, scope);

            case AggregateNode aggregate when scope.Grouped:
                return RenderAggregate(aggregate, scope.Parameter, scope.Model, scope.Depth);

            case GroupKeyNode key when scope.Grouped:
                return RenderGroupKey(key, scope);

            default:
                // CollateNode anywhere but under the comparisons that spell it, a group construct
                // outside a group, or a node this renderer has never heard of.
                throw Refuse(RenderRefusal.UnsupportedShape);
        }
    }

    string RenderMember(MemberNode member, Scope scope)
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
                    return groupKeyNames is null ? $"{scope.Parameter}.Key" : $"{scope.Parameter}.Key.{groupKeyNames[i]}";
                }
            }

            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        return $"{scope.Parameter}.{string.Join('.', member.Path)}";
    }

    string RenderGroupKey(GroupKeyNode key, Scope scope)
    {
        if (groupKeys is null ||
            key.Index < 0 ||
            key.Index >= groupKeys.Count)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        return groupKeyNames is null ? $"{scope.Parameter}.Key" : $"{scope.Parameter}.Key.{groupKeyNames[key.Index]}";
    }

    string RenderBinary(BinaryNode node, Scope scope)
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
            var argument = node.Right is ConstNode constant
                ? RenderConst(constant, typeof(string))
                : Operand(node.Right, scope);
            return $"{Target(collate.Target, scope)}.Equals({argument}, {Comparison(collate.Match)})";
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

        return $"{Side(node.Left, node.Right)} {symbol} {Side(node.Right, node.Left)}";

        string Side(Node side, Node other)
        {
            if (side is ConstNode constant)
            {
                return RenderConst(constant, InferType(other, scope));
            }

            var text = Operand(side, scope);

            // A comparison of an enum-typed member against a numeric constant was written through a
            // cast the wire stripped; put the cast back so the snippet compiles to the same bytes.
            if (other is ConstNode {Tag: ClrTypeTag.Int32 or ClrTypeTag.Int64} &&
                InferType(side, scope) is { } inferred)
            {
                var underlying = Nullable.GetUnderlyingType(inferred);
                if ((underlying ?? inferred).IsEnum)
                {
                    return underlying is null ? $"(int){text}" : $"(int?){text}";
                }
            }

            return text;
        }
    }

    // An operand keeps its own parentheses where the surrounding operator would otherwise re-group
    // it. Extra parentheses never change the captured tree, so grouping errs toward wrapping.
    string Operand(Node node, Scope scope)
    {
        var text = RenderNode(node, scope);
        return node is BinaryNode or ConditionalNode ? $"({text})" : text;
    }

    // The receiver of an instance call has to be a primary expression; anything composite — and a
    // bare literal, whose dot the lexer would eat — is wrapped.
    string Target(Node node, Scope scope)
    {
        var text = RenderNode(node, scope);
        return node is BinaryNode or ConditionalNode or UnaryNode or ConstNode ? $"({text})" : text;
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

    string RenderSubquery(SubqueryNode subquery, Scope scope)
    {
        if (scope.Grouped ||
            subquery.Path.Count == 0)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var collection = $"{scope.Parameter}.{string.Join('.', subquery.Path)}";
        var elementModel = Walk(scope.Model, subquery.Path) is { } property
            ? SensitiveModel.Element(property.PropertyType)
            : null;
        var inner = new Scope(scope.NestedParameter, elementModel, Grouped: false, Depth: scope.Depth + 1);

        string Fold(string name, Node? body)
        {
            if (body is null)
            {
                return $"{collection}.{name}()";
            }

            return $"{collection}.{name}({inner.Parameter} => {RenderNode(body, inner)})";
        }

        switch (subquery.Function)
        {
            case SubqueryFn.Any:
                return Fold("Any", subquery.Predicate);

            case SubqueryFn.All:
                return subquery.Predicate is null
                    ? throw Refuse(RenderRefusal.UnsupportedShape)
                    : Fold("All", subquery.Predicate);

            case SubqueryFn.Count:
                return Fold("Count", subquery.Predicate);

            case SubqueryFn.Sum or SubqueryFn.Average or SubqueryFn.Min or SubqueryFn.Max:
            {
                var name = subquery.Function.ToString();
                if (subquery.Predicate is not null)
                {
                    collection = $"{collection}.Where({inner.Parameter} => {RenderNode(subquery.Predicate, inner)})";
                }

                return subquery.Selector switch
                {
                    ElementNode => $"{collection}.{name}()",
                    null => throw Refuse(RenderRefusal.UnsupportedShape),
                    _ => $"{collection}.{name}({inner.Parameter} => {RenderNode(subquery.Selector, inner)})"
                };
            }

            default:
                throw Refuse(RenderRefusal.UnsupportedShape);
        }
    }

    string RenderInSource(InSourceNode inSource, Scope scope)
    {
        var sourceModel = SensitiveModel.ModelFor(inSource.Root);
        var inner = new Scope(scope.NestedParameter, sourceModel, Grouped: false, Depth: scope.Depth + 1);
        var builder = new StringBuilder("Query.").Append(inSource.Root);
        if (inSource.Predicate is { } predicate)
        {
            builder.Append($".Where({inner.Parameter} => {RenderNode(predicate, inner)})");
        }

        builder.Append($".Select({inner.Parameter} => {RenderNode(inSource.Selector, inner)})");
        builder.Append($".Contains({RenderNode(inSource.Value, scope)})");
        return builder.ToString();
    }

    /// <summary>
    /// An aggregate folding a group, in the grammar the forward pass reads back:
    /// <c>g [.Where(x =&gt; P)] [.Select(x =&gt; S) [.Distinct()]] .Fold(…)</c> — with
    /// <c>Count(x =&gt; P)</c> abbreviating the filtered count, and <c>string.Join</c> as the text
    /// fold.
    /// </summary>
    string RenderAggregate(AggregateNode aggregate, string group, Type? elementModel, int depth)
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

            var selected = RenderNode(aggregate.Selector, inner);
            return $"string.Join({CSharpLiteral.String(aggregate.Separator ?? "")}, {group}.Select({inner.Parameter} => {selected}))";
        }

        var source = group;
        if (aggregate.Predicate is { } predicate)
        {
            // A bare filtered count folds the predicate into Count itself, which is the exact
            // abbreviation the forward pass records the same way.
            if (aggregate is {Function: AggregateFn.Count, Distinct: false, Selector: null})
            {
                return $"{group}.Count({inner.Parameter} => {RenderNode(predicate, inner)})";
            }

            source = $"{source}.Where({inner.Parameter} => {RenderNode(predicate, inner)})";
        }

        if (aggregate.Function == AggregateFn.Count)
        {
            if (aggregate.Distinct)
            {
                if (aggregate.Selector is null or ElementNode)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                return $"{source}.Select({inner.Parameter} => {RenderNode(aggregate.Selector, inner)}).Distinct().Count()";
            }

            if (aggregate.Selector is not null)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            return $"{source}.Count()";
        }

        if (aggregate.Function is not (AggregateFn.Sum or AggregateFn.Average or AggregateFn.Min or AggregateFn.Max) ||
            aggregate.Selector is null or ElementNode)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var fold = aggregate.Function.ToString();
        var selector = RenderNode(aggregate.Selector, inner);
        if (aggregate.Distinct)
        {
            return $"{source}.Select({inner.Parameter} => {selector}).Distinct().{fold}()";
        }

        return $"{source}.{fold}({inner.Parameter} => {selector})";
    }
}
