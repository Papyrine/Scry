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
            // A nullable's Value is the member it wraps, wherever in the path it sits — an optional
            // struct complex member is read through one — and is dropped for the reason the Value
            // case in TranslateExpr drops it at the leaf.
            if (inner.Member.Name != "Value" ||
                inner.Expression is not { } owner ||
                !IsOptional(owner))
            {
                path.Add(inner.Member.Name);
            }

            current = inner.Expression;
        }

        path.Reverse();
        return path;
    }

    static bool ReferencesParameter(Expression expression, ParameterExpression parameter) =>
        new ParameterFinder(parameter).Found(expression);

    static int IntArgument(Expression expression) =>
        (int)Convert.ChangeType(Evaluate(expression)!, typeof(int), CultureInfo.InvariantCulture);

    static object? Evaluate(Expression expression)
    {
        // Closure state reads nothing of any row. An expression that reaches a lambda parameter — the
        // row of an enclosing lambda read from a nested one, the index of an indexed Where — is not
        // that, and compiling it would fail with the expression compiler's own message about an
        // undefined variable rather than with what Scry cannot carry.
        if (UnboundParameter.In(expression) is { } parameter)
        {
            throw new NotSupportedException(
                $"'{parameter.Name}' is a lambda parameter this part of the query cannot read. A Scry query lambda reads its own row and closure state only: a nested query cannot read the row it is written inside, and an index parameter has no meaning in the database.");
        }

        // Read directly where the shape allows — a constant, or a chain of member reads rooted at
        // one, which is what a captured variable compiles to. Every other shape is compiled, which on
        // the interpreter the browser runs is the cost this exists to skip.
        return TryRead(expression, out var value)
            ? value
            : Expression.Lambda(expression).Compile().DynamicInvoke();
    }

    static bool TryRead(Expression expression, out object? value)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;

            case MemberExpression {Expression: null, Member: FieldInfo field}:
                value = field.GetValue(null);
                return true;

            case MemberExpression {Expression: null, Member: PropertyInfo property}
                when property.GetIndexParameters().Length == 0:
                value = property.GetValue(null);
                return true;

            // A null owner is left to the compiled form, which throws the null reference the
            // expression means; read here it would throw a reflection exception instead.
            case MemberExpression {Expression: { } owner} member
                when TryRead(owner, out var target) && target is not null:
                switch (member.Member)
                {
                    case FieldInfo field:
                        value = field.GetValue(target);
                        return true;
                    case PropertyInfo property when property.GetIndexParameters().Length == 0:
                        value = property.GetValue(target);
                        return true;
                }

                break;

            // A conversion that only boxes, lifts, or widens to a base type leaves the value as it is;
            // one that changes the representation is left to the compiler.
            case UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} convert
                when convert.Type.IsAssignableFrom(convert.Operand.Type) && TryRead(convert.Operand, out var inner):
                value = inner;
                return true;
        }

        value = null;
        return false;
    }

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

    // The first parameter an expression reads that no lambda inside it declares: what makes it
    // something other than closure state.
    sealed class UnboundParameter :
        ExpressionVisitor
    {
        readonly HashSet<ParameterExpression> bound = [];
        ParameterExpression? found;

        public static ParameterExpression? In(Expression expression)
        {
            var finder = new UnboundParameter();
            finder.Visit(expression);
            return finder.found;
        }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            foreach (var parameter in node.Parameters)
            {
                bound.Add(parameter);
            }

            var visited = base.VisitLambda(node);
            foreach (var parameter in node.Parameters)
            {
                bound.Remove(parameter);
            }

            return visited;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!bound.Contains(node))
            {
                found ??= node;
            }

            return base.VisitParameter(node);
        }
    }

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
