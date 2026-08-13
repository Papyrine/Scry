// The small questions every other part asks: what a value is, where a member path runs, and how a
// closure expression becomes a constant.
sealed partial class QueryTranslator
{
    /// <summary>
    /// Whether a type is one of the values a query reads directly, rather than a row whose members it
    /// names. Mirrors the server's <c>Schema.IsScalar</c>: the two only have to agree about what makes
    /// the lambda parameter itself readable, and a disagreement costs a rejected request rather than
    /// anything worse.
    /// </summary>
    static bool IsValue(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive ||
               underlying.IsEnum ||
               underlying == typeof(string) ||
               underlying == typeof(decimal) ||
               underlying == typeof(DateTime) ||
               underlying == typeof(Date) ||
               underlying == typeof(Time) ||
               underlying == typeof(DateTimeOffset) ||
               underlying == typeof(TimeSpan) ||
               underlying == typeof(Guid) ||
               underlying == typeof(byte[]);
    }

    // Whether the expression is an optional value, whose Value and HasValue members are carried as the
    // member itself and as a comparison against null.
    static bool IsOptional(Expression expression) =>
        Nullable.GetUnderlyingType(expression.Type) is not null;

    static bool IsTemporal(Type? type) =>
        type == typeof(DateTime) ||
        type == typeof(Date) ||
        type == typeof(DateTimeOffset) ||
        type == typeof(Time);

    // The types the server compares three ways: numbers, text, and dates. Mirrors the server's own
    // allow-list, so an unsupported target refuses at translation rather than as a rejected request.
    // Enums are excluded by hand: their type code reports the underlying number's.
    static bool IsThreeWayComparable(Type type) =>
        type == typeof(string) ||
        IsTemporal(type) ||
        (!type.IsEnum &&
         Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte
             or TypeCode.Int16 or TypeCode.UInt16
             or TypeCode.Int32 or TypeCode.UInt32
             or TypeCode.Int64 or TypeCode.UInt64
             or TypeCode.Single or TypeCode.Double or TypeCode.Decimal);

    // The two operands of an Equals that means ==: the instance and its one argument, or the two
    // arguments of the static spelling. Any other shape is an overload the set does not carry.
    static (Expression Left, Expression Right)? EqualityOperands(MethodCallExpression call) =>
        call switch
        {
            {Object: { } instance, Arguments: [var argument]} => (instance, argument),
            {Object: null, Arguments: [var first, var second]} => (first, second),
            _ => null
        };

    static bool IsRooted(MemberExpression member, ParameterExpression root)
    {
        Expression? current = member;
        while (current is MemberExpression inner)
        {
            current = inner.Expression;
        }

        return current == root;
    }

    static List<string> MemberPath(MemberExpression member)
    {
        var path = new List<string>();
        Expression? current = member;
        while (current is MemberExpression inner)
        {
            path.Add(inner.Member.Name);
            current = inner.Expression;
        }

        path.Reverse();
        return path;
    }

    static bool ReferencesParameter(Expression expression, ParameterExpression parameter) =>
        new ParameterFinder(parameter).Found(expression);

    static int IntArgument(Expression expression) =>
        (int)Convert.ChangeType(Evaluate(expression)!, typeof(int), CultureInfo.InvariantCulture);

    static object? Evaluate(Expression expression) =>
        Expression.Lambda(expression).Compile().DynamicInvoke();

    static LambdaExpression Lambda(Expression expression) =>
        expression switch
        {
            UnaryExpression
            {
                Operand: LambdaExpression lambda
            } => lambda,
            LambdaExpression lambda => lambda,
            _ => throw new NotSupportedException("Expected a lambda expression.")
        };

    static BinaryOp MapBinary(ExpressionType type) =>
        type switch
        {
            ExpressionType.Equal => BinaryOp.Equal,
            ExpressionType.NotEqual => BinaryOp.NotEqual,
            ExpressionType.LessThan => BinaryOp.LessThan,
            ExpressionType.LessThanOrEqual => BinaryOp.LessThanOrEqual,
            ExpressionType.GreaterThan => BinaryOp.GreaterThan,
            ExpressionType.GreaterThanOrEqual => BinaryOp.GreaterThanOrEqual,
            ExpressionType.AndAlso => BinaryOp.AndAlso,
            ExpressionType.OrElse => BinaryOp.OrElse,
            ExpressionType.Add => BinaryOp.Add,
            ExpressionType.Subtract => BinaryOp.Subtract,
            ExpressionType.Multiply => BinaryOp.Multiply,
            ExpressionType.Divide => BinaryOp.Divide,
            ExpressionType.Modulo => BinaryOp.Modulo,
            ExpressionType.Coalesce => BinaryOp.Coalesce,
            _ => throw new NotSupportedException($"Binary operator '{type}' is not supported.")
        };

    static ConstNode ConstantOf(object? value)
    {
        var (text, tag) = ValueTag.Of(value);
        return new(text, tag);
    }

    static NotSupportedException Unsupported(Expression expression) =>
        new($"Expression '{expression.NodeType}' is not supported by Scry.");

    sealed class ParameterFinder(ParameterExpression target) :
        ExpressionVisitor
    {
        bool found;

        public bool Found(Expression expression)
        {
            found = false;
            Visit(expression);
            return found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == target)
            {
                found = true;
            }

            return base.VisitParameter(node);
        }
    }
}
