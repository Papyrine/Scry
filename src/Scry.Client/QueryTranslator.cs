namespace Scry.Client;

/// <summary>
/// Translates a captured LINQ expression tree into the wire AST. Supports the closed operator set;
/// anything outside it throws a clear <see cref="NotSupportedException"/> at translation time.
/// </summary>
sealed class QueryTranslator
{
    IReadOnlyList<string>? groupKey;

    public static IReadOnlyList<QueryOp> Translate(Expression expression)
    {
        var translator = new QueryTranslator();
        var ops = new List<QueryOp>();
        translator.Visit(expression, ops);
        return ops;
    }

    void Visit(Expression expression, List<QueryOp> ops)
    {
        if (expression is ConstantExpression)
        {
            return;
        }

        if (expression is not MethodCallExpression call)
        {
            throw Unsupported(expression);
        }

        Visit(call.Arguments[0], ops);
        ops.Add(TranslateCall(call));
    }

    QueryOp TranslateCall(MethodCallExpression call)
    {
        switch (call.Method.Name)
        {
            case "Where":
                var where = Lambda(call.Arguments[1]);
                return new WhereOp(TranslateExpr(where.Body, where.Parameters[0]));

            case "OrderBy":
                return new OrderByOp(TranslateKey(call), Descending: false);
            case "OrderByDescending":
                return new OrderByOp(TranslateKey(call), Descending: true);
            case "ThenBy":
                return new ThenByOp(TranslateKey(call), Descending: false);
            case "ThenByDescending":
                return new ThenByOp(TranslateKey(call), Descending: true);

            case "Skip":
                return new SkipOp(IntArgument(call.Arguments[1]));
            case "Take":
                return new TakeOp(IntArgument(call.Arguments[1]));

            case "GroupBy":
                var keyLambda = Lambda(call.Arguments[1]);
                var key = TranslateExpr(keyLambda.Body, keyLambda.Parameters[0]);
                groupKey = (key as MemberExpr)?.Path ??
                           throw new NotSupportedException("GroupBy key must be a member access.");
                return new GroupByOp([key]);

            case "Select":
                return new SelectOp(TranslateProjection(Lambda(call.Arguments[1])));

            default:
                throw new NotSupportedException($"LINQ operator '{call.Method.Name}' is not supported by Scry.");
        }
    }

    Expr TranslateKey(MethodCallExpression call)
    {
        var lambda = Lambda(call.Arguments[1]);
        return TranslateExpr(lambda.Body, lambda.Parameters[0]);
    }

    Projection TranslateProjection(LambdaExpression selector)
    {
        var parameter = selector.Parameters[0];
        var grouped = parameter.Type.IsGenericType &&
                      parameter.Type.GetGenericTypeDefinition() == typeof(IGrouping<,>);

        return selector.Body switch
        {
            NewExpression construction => FromNew(construction, parameter, grouped),
            MemberInitExpression init => FromMemberInit(init, parameter, grouped),
            _ => throw new NotSupportedException("A projection must construct an object (anonymous type, record, or object initializer).")
        };
    }

    Projection FromNew(NewExpression construction, ParameterExpression parameter, bool grouped)
    {
        var members = new List<ProjectionMember>();
        var names = ProjectionNames(construction);
        var arguments = construction.Arguments;
        for (var i = 0; i < arguments.Count; i++)
        {
            members.Add(new(names[i], ProjectionValue(arguments[i], parameter, grouped)));
        }

        return new(members);
    }

    Projection FromMemberInit(MemberInitExpression init, ParameterExpression parameter, bool grouped)
    {
        var members = new List<ProjectionMember>();
        foreach (var binding in init.Bindings)
        {
            if (binding is not MemberAssignment assignment)
            {
                throw new NotSupportedException("Only simple member assignments are supported in a projection.");
            }

            members.Add(new(assignment.Member.Name, ProjectionValue(assignment.Expression, parameter, grouped)));
        }

        return new(members);
    }

    ProjectionValue ProjectionValue(Expression expression, ParameterExpression parameter, bool grouped)
    {
        if (grouped)
        {
            if (expression is MemberExpression
                {
                    Member.Name: "Key"
                } memberKey &&
                memberKey.Expression == parameter)
            {
                return new ExprValue(new MemberExpr(groupKey ?? throw new NotSupportedException("No group key in scope.")));
            }

            if (expression is MethodCallExpression aggregate)
            {
                return new ExprValue(TranslateAggregate(aggregate));
            }

            throw new NotSupportedException("A grouped projection may only use the group key or aggregates.");
        }

        return new ExprValue(TranslateExpr(expression, parameter));
    }

    AggregateExpr TranslateAggregate(MethodCallExpression call)
    {
        if (call.Method.Name == "Count")
        {
            return new(AggregateFn.Count, Selector: null);
        }

        var selector = Lambda(call.Arguments[1]);
        var member = TranslateExpr(selector.Body, selector.Parameters[0]);
        var function = call.Method.Name switch
        {
            "Sum" => AggregateFn.Sum,
            "Average" => AggregateFn.Average,
            "Min" => AggregateFn.Min,
            "Max" => AggregateFn.Max,
            _ => throw new NotSupportedException($"Aggregate '{call.Method.Name}' is not supported.")
        };

        return new(function, member);
    }

    Expr TranslateExpr(Expression expression, ParameterExpression root)
    {
        while (true)
        {
            switch (expression)
            {
                case UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} convert:
                    expression = convert.Operand;
                    continue;

                case UnaryExpression {NodeType: ExpressionType.Not} not:
                    return new UnaryExpr(UnaryOp.Not, TranslateExpr(not.Operand, root));

                case UnaryExpression {NodeType: ExpressionType.Negate} negate:
                    return new UnaryExpr(UnaryOp.Negate, TranslateExpr(negate.Operand, root));

                case BinaryExpression binary:
                    return new BinaryExpr(MapBinary(binary.NodeType), TranslateExpr(binary.Left, root), TranslateExpr(binary.Right, root));

                case MemberExpression member when IsDatePart(member, out var function):
                    return new CallExpr(function, TranslateExpr(member.Expression!, root), []);

                case MemberExpression member when IsRooted(member, root):
                    return new MemberExpr(MemberPath(member));

                case MemberExpression member:
                    return ConstantOf(Evaluate(member));

                case ConstantExpression constant:
                    return ConstantOf(constant.Value);

                case MethodCallExpression call:
                    return TranslateMethod(call, root);

                default:
                    throw Unsupported(expression);
            }
        }
    }

    Expr TranslateMethod(MethodCallExpression call, ParameterExpression root)
    {
        if (call.Method.DeclaringType == typeof(string))
        {
            return call.Method.Name switch
            {
                "Contains" => new CallExpr(KnownFunction.StringContains, TranslateExpr(call.Object!, root), [TranslateExpr(call.Arguments[0], root)]),
                "StartsWith" => new CallExpr(KnownFunction.StringStartsWith, TranslateExpr(call.Object!, root), [TranslateExpr(call.Arguments[0], root)]),
                "EndsWith" => new CallExpr(KnownFunction.StringEndsWith, TranslateExpr(call.Object!, root), [TranslateExpr(call.Arguments[0], root)]),
                "ToLower" => new CallExpr(KnownFunction.StringToLower, TranslateExpr(call.Object!, root), []),
                "ToUpper" => new CallExpr(KnownFunction.StringToUpper, TranslateExpr(call.Object!, root), []),
                "IsNullOrEmpty" => new CallExpr(KnownFunction.StringIsNullOrEmpty, TranslateExpr(call.Arguments[0], root), []),
                _ => throw Unsupported(call)
            };
        }

        // A call that does not touch the parameter is a closure value — evaluate it.
        if (!ReferencesParameter(call, root))
        {
            return ConstantOf(Evaluate(call));
        }

        throw Unsupported(call);
    }

    static bool IsDatePart(MemberExpression member, out KnownFunction function)
    {
        var declaring = member.Member.DeclaringType;
        if (member.Expression is not null &&
            (declaring == typeof(DateTime) ||
             declaring == typeof(Date)))
        {
            switch (member.Member.Name)
            {
                case "Year":
                    function = KnownFunction.DateYear;
                    return true;
                case "Month":
                    function = KnownFunction.DateMonth;
                    return true;
                case "Day":
                    function = KnownFunction.DateDay;
                    return true;
            }
        }

        function = default;
        return false;
    }

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

    static IReadOnlyList<string> ProjectionNames(NewExpression construction)
    {
        if (construction.Members is { } members)
        {
            return members.Select(_ => _.Name).ToArray();
        }

        if (construction.Constructor is { } constructor)
        {
            return constructor.GetParameters()
                .Select(_ => Capitalize(_.Name!))
                .ToArray();
        }

        throw new NotSupportedException("Cannot determine projection member names.");
    }

    static string Capitalize(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

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
            _ => throw new NotSupportedException($"Binary operator '{type}' is not supported.")
        };

    static ConstExpr ConstantOf(object? value)
    {
        var culture = CultureInfo.InvariantCulture;
        return value switch
        {
            null => new(null, ClrTypeTag.Null),
            string text => new(text, ClrTypeTag.String),
            bool flag => new(flag ? "true" : "false", ClrTypeTag.Boolean),
            Enum enumeration => new(enumeration.ToString(), ClrTypeTag.Enum),
            int number => new(number.ToString(culture), ClrTypeTag.Int32),
            long number => new(number.ToString(culture), ClrTypeTag.Int64),
            short number => new(number.ToString(culture), ClrTypeTag.Int32),
            byte number => new(number.ToString(culture), ClrTypeTag.Int32),
            decimal number => new(number.ToString(culture), ClrTypeTag.Decimal),
            double number => new(number.ToString(culture), ClrTypeTag.Double),
            float number => new(number.ToString(culture), ClrTypeTag.Double),
            DateTime date => new(date.ToString("o", culture), ClrTypeTag.DateTime),
            Date date => new(date.ToString("yyyy-MM-dd", culture), ClrTypeTag.DateOnly),
            Guid guid => new(guid.ToString(), ClrTypeTag.Guid),
            _ => new(Convert.ToString(value, culture) ?? "", ClrTypeTag.String)
        };
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
