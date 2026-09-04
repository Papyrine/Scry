/// <summary>
/// Reads a lambda that binds variables as the one expression it stands for, by substituting each
/// variable with the expression bound to it.
/// </summary>
/// <remarks>
/// C# cannot put a variable in an expression lambda — a statement body is refused at compile time — but
/// F# can. A <c>let</c> inside a query lambda compiles to a Block that assigns a variable and then reads
/// it, and the F# compiler emits one of its own for an anonymous record whose fields are written in an
/// order other than the declared one, so that they still evaluate in the order written. Neither is a
/// different query: a query reads the row and computes nothing else, so evaluating the bound expression
/// wherever the variable was read is the same query, spelled the way the translator already reads.
/// </remarks>
sealed class LetInliner :
    ExpressionVisitor
{
    readonly Dictionary<ParameterExpression, Expression> bindings = [];
    readonly HashSet<ParameterExpression> declared = [];

    public static Expression Inline(Expression expression) =>
        new LetInliner().Visit(expression);

    protected override Expression VisitBlock(BlockExpression node)
    {
        if (node.Expressions is not [.., var value])
        {
            throw Unsupported();
        }

        declared.UnionWith(node.Variables);

        // Every statement before the value binds one of the block's own variables, in order, so a
        // binding may read one bound before it.
        for (var i = 0; i < node.Expressions.Count - 1; i++)
        {
            if (node.Expressions[i] is not BinaryExpression
                {
                    NodeType: ExpressionType.Assign,
                    Left: ParameterExpression variable
                } binding ||
                !node.Variables.Contains(variable))
            {
                throw Unsupported();
            }

            bindings[variable] = Visit(binding.Right);
        }

        var inlined = Visit(value);

        // The variables are scoped to the block; a sibling declaring the same one starts over.
        foreach (var variable in node.Variables)
        {
            bindings.Remove(variable);
            declared.Remove(variable);
        }

        return inlined;
    }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        if (bindings.TryGetValue(node, out var bound))
        {
            return bound;
        }

        if (declared.Contains(node))
        {
            throw new NotSupportedException($"Variable '{node.Name}' is read before anything is bound to it.");
        }

        return node;
    }

    static NotSupportedException Unsupported() =>
        new("A block inside a query lambda may only bind variables and read them; a statement that does anything else is not supported by Scry.");
}
