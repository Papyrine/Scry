// The node level: one expression the row is read through, translated into one wire node.
sealed partial class QueryTranslator
{
    Node TranslateExpr(Expression expression, ParameterExpression root)
    {
        while (true)
        {
            switch (expression)
            {
                case UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} convert:
                    expression = convert.Operand;
                    continue;

                case UnaryExpression {NodeType: ExpressionType.Not} not:
                    return new UnaryNode(UnaryOp.Not, TranslateExpr(not.Operand, root));

                case UnaryExpression {NodeType: ExpressionType.Negate} negate:
                    return new UnaryNode(UnaryOp.Negate, TranslateExpr(negate.Operand, root));

                // A binary member's Length is an ArrayLength node rather than a member access, since
                // the CLR spells an array's length as an operator.
                case UnaryExpression {NodeType: ExpressionType.ArrayLength} length
                    when length.Operand.Type == typeof(byte[]) && ReferencesParameter(length, root):
                    return new CallNode(KnownFunction.BytesLength, TranslateExpr(length.Operand, root), []);

                // C# compiles string concatenation to an Add carrying string.Concat as its method. The
                // operator alone cannot say which was meant — an Add of a string and a number is a
                // concatenation, an Add of two numbers is arithmetic — so the intent is recorded here,
                // where the method is still visible, rather than guessed from the operand types later.
                case BinaryExpression {NodeType: ExpressionType.Add, Method: {Name: "Concat"} method} concat
                    when method.DeclaringType == typeof(string):
                    return new CallNode(
                        KnownFunction.StringConcat,
                        TranslateExpr(concat.Left, root),
                        [TranslateExpr(concat.Right, root)]);

                case BinaryExpression binary:
                    return new BinaryNode(MapBinary(binary.NodeType), TranslateExpr(binary.Left, root), TranslateExpr(binary.Right, root));

                case ConditionalExpression conditional:
                    return new ConditionalNode(
                        TranslateExpr(conditional.Test, root),
                        TranslateExpr(conditional.IfTrue, root),
                        TranslateExpr(conditional.IfFalse, root));

                // Inside a grouped Where the row being read is a group: its Key is whatever the query
                // grouped by, and a call taking the group itself folds that group's rows.
                // One part of a composite key: 'g.Key.Region' is the member the query grouped by.
                case MemberExpression {Expression: MemberExpression {Member.Name: "Key"} owner} part
                    when owner.Expression == root && IsGrouping(root.Type) && groupKeyParts is not null:
                    return groupKeyParts.TryGetValue(part.Member.Name, out var resolved)
                        ? resolved
                        : throw new NotSupportedException(
                            $"'{part.Member.Name}' is not one of the query's group keys.");

                case MemberExpression {Member.Name: "Key"} key
                    when key.Expression == root && IsGrouping(root.Type):
                    return groupKeyNode ?? throw new NotSupportedException("No group key in scope.");

                case MethodCallExpression aggregate
                    when IsGrouping(root.Type) && IsChainOver(aggregate, root):
                    return TranslateAggregate(aggregate);

                case MethodCallExpression joined
                    when IsGrouping(root.Type) && TryJoinText(joined, root) is { } text:
                    return text;

                // The Count property a collection carries means the same as calling Count().
                case MemberExpression {Member.Name: "Count", Expression: { } owner}
                    when IsRootedCollection(owner, root):
                    return new SubqueryNode(MemberPath((MemberExpression)owner), SubqueryFn.Count, null, null);

                // A nullable's Value is the member it wraps. Every wire operand is already optional, so
                // there is no wrapper to strip on the far side — carried as a path segment it would
                // only read as a member the server cannot find.
                case MemberExpression {Member.Name: "Value", Expression: { } optional} valued
                    when IsOptional(optional) && IsRooted(valued, root):
                    expression = optional;
                    continue;

                // HasValue asks the one thing the wire already spells as a comparison: whether the
                // member is there.
                case MemberExpression {Member.Name: "HasValue", Expression: { } asked} present
                    when IsOptional(asked) && IsRooted(present, root):
                    return new BinaryNode(BinaryOp.NotEqual, TranslateExpr(asked, root), ConstantOf(null));

                case MemberExpression member when IsKnownProperty(member, out var function):
                    return new CallNode(function, TranslateExpr(member.Expression!, root), []);

                // An attachment reached anywhere an expression is being built. A projection leaf is
                // handled before this, so arriving here means it was used as a value — compared,
                // ordered by, aggregated — and its value is the one thing no query has.
                case MemberExpression member
                    when member.Type == typeof(ScryAttachment) && IsRooted(member, root):
                    throw new NotSupportedException(
                        $"Attachment '{member.Member.Name}' is not a value: no query reads it, so it cannot be filtered, ordered, or computed on. Fetch it from the row with OpenAsync instead.");

                case MemberExpression member when IsRooted(member, root):
                    return new MemberNode(MemberPath(member));

                case MemberExpression member:
                    return ConstantOf(Evaluate(member));

                case ConstantExpression constant:
                    return ConstantOf(constant.Value);

                // The lambda parameter read as a value rather than traversed: the element of a
                // collection of values, which has no member to name. A parameter standing for a row is
                // deliberately left out, so projecting or comparing a whole row still fails here rather
                // than as a rejected request.
                case ParameterExpression parameter when parameter == root && IsValue(parameter.Type):
                    return new ElementNode();

                case MethodCallExpression call:
                    return TranslateMethod(call, root);

                default:
                    // Anything else that does not read the row is closure state — a constructed value
                    // such as new DateTime(…), an indexer, a cast — so it is evaluated into a constant.
                    if (!ReferencesParameter(expression, root))
                    {
                        return ConstantOf(Evaluate(expression));
                    }

                    throw Unsupported(expression);
            }
        }
    }
}
