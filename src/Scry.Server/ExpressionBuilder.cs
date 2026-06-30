/// <summary>
/// Rebinds a validated query AST onto real CLR <see cref="Expression"/> trees over the server's
/// entity types. This is the only place CLR types are introduced — always from the schema, never
/// from the wire.
/// </summary>
sealed class ExpressionBuilder(ScrySchema schema)
{
    /// <summary>Builds a predicate lambda <c>TElement =&gt; bool</c>.</summary>
    public LambdaExpression BuildPredicate(Expr predicate, Type elementType)
    {
        var parameter = Expression.Parameter(elementType, "e");
        var body = Build(predicate, parameter, typeof(bool));
        return Expression.Lambda(body, parameter);
    }

    /// <summary>Builds a key selector lambda <c>TElement =&gt; TKey</c>.</summary>
    public LambdaExpression BuildKeySelector(Expr key, Type elementType)
    {
        var parameter = Expression.Parameter(elementType, "e");
        var body = Build(key, parameter, null);
        return Expression.Lambda(body, parameter);
    }

    /// <summary>
    /// Builds a selector <c>TElement =&gt; object[]</c> projecting the requested scalar leaves, plus a
    /// shape describing how to fold the array back into (possibly nested) JSON.
    /// </summary>
    public ProjectionPlan BuildProjection(Projection projection, Type elementType)
    {
        var parameter = Expression.Parameter(elementType, "e");
        var leaves = new List<Expression>();
        var shape = new List<IReadOnlyList<string>>();
        Flatten(projection, parameter, [], leaves, shape);
        var selector = Expression.Lambda(ToObjectArray(leaves), parameter);
        return new(selector, shape);
    }

    /// <summary>Builds a default projection of every allow-listed scalar member of the source.</summary>
    public ProjectionPlan BuildDefaultProjection(Type elementType)
    {
        if (!schema.TryGetType(elementType, out var meta))
        {
            throw new ScryValidationException($"Type '{elementType.Name}' is not queryable.");
        }

        var parameter = Expression.Parameter(elementType, "e");
        var leaves = new List<Expression>();
        var shape = new List<IReadOnlyList<string>>();
        foreach (var member in meta.Members.Values.Where(_ => _.Kind == MemberKind.Scalar))
        {
            leaves.Add(Expression.Property(parameter, member.Property));
            shape.Add([member.Name]);
        }

        var selector = Expression.Lambda(ToObjectArray(leaves), parameter);
        return new(selector, shape);
    }

    /// <summary>
    /// Builds a grouped selector <c>IGrouping&lt;TKey,TElement&gt; =&gt; object[]</c> over a single key,
    /// supporting the group key and aggregate members.
    /// </summary>
    public ProjectionPlan BuildGroupProjection(Projection projection, Type elementType, Type keyType)
    {
        var groupingType = typeof(IGrouping<,>).MakeGenericType(keyType, elementType);
        var parameter = Expression.Parameter(groupingType, "g");
        var leaves = new List<Expression>();
        var shape = new List<IReadOnlyList<string>>();

        foreach (var member in projection.Members)
        {
            var expression = ((ExprValue)member.Value).Expression;
            var leaf = expression switch
            {
                AggregateExpr aggregate => BuildAggregate(aggregate, parameter, elementType),
                MemberExpr => Expression.Property(parameter, "Key"),
                _ => throw new ScryValidationException("Unsupported grouped projection member.")
            };

            leaves.Add(Box(leaf));
            shape.Add([member.Name]);
        }

        var selector = Expression.Lambda(Expression.NewArrayInit(typeof(object), leaves), parameter);
        return new(selector, shape);
    }

    void Flatten(
        Projection projection,
        Expression root,
        IReadOnlyList<string> jsonPrefix,
        List<Expression> leaves,
        List<IReadOnlyList<string>> shape)
    {
        foreach (var member in projection.Members)
        {
            var jsonPath = jsonPrefix.Append(member.Name).ToArray();
            switch (member.Value)
            {
                case ExprValue { Expression: MemberExpr memberExpr }:
                    leaves.Add(BuildMemberAccess(root, memberExpr.Path));
                    shape.Add(jsonPath);
                    break;

                case NestedValue nested:
                    var navTarget = BuildMemberAccess(root, nested.Path);
                    Flatten(nested.Projection, navTarget, jsonPath, leaves, shape);
                    break;

                default:
                    throw new ScryValidationException("Unsupported projection member.");
            }
        }
    }

    Expression Build(Expr expr, ParameterExpression parameter, Type? expected) =>
        expr switch
        {
            MemberExpr member => BuildMemberAccess(parameter, member.Path),
            ConstExpr constant => BuildConstant(constant, expected),
            BinaryExpr binary => BuildBinary(binary, parameter),
            UnaryExpr unary => BuildUnary(unary, parameter),
            CallExpr call => BuildCall(call, parameter),
            _ => throw new ScryValidationException($"Unsupported expression '{expr.GetType().Name}'.")
        };

    Expression BuildMemberAccess(Expression root, IReadOnlyList<string> path)
    {
        var expression = root;
        foreach (var segment in path)
        {
            if (!schema.TryGetType(expression.Type, out var meta) ||
                !meta.Members.TryGetValue(segment, out var member))
            {
                throw new ScryValidationException(
                    $"Property '{segment}' is not allow-listed on '{expression.Type.Name}'.");
            }

            expression = Expression.Property(expression, member.Property);
        }

        return expression;
    }

    Expression BuildBinary(BinaryExpr binary, ParameterExpression parameter)
    {
        if (binary.Op is BinaryOp.AndAlso or BinaryOp.OrElse)
        {
            var leftBool = Build(binary.Left, parameter, typeof(bool));
            var rightBool = Build(binary.Right, parameter, typeof(bool));
            if (binary.Op == BinaryOp.AndAlso)
            {
                return Expression.AndAlso(leftBool, rightBool);
            }

            return Expression.OrElse(leftBool, rightBool);
        }

        // Infer the constant's type from the typed (non-constant) operand.
        Expression left;
        Expression right;
        if (binary is {Left: ConstExpr, Right: not ConstExpr})
        {
            right = Build(binary.Right, parameter, null);
            left = Build(binary.Left, parameter, right.Type);
        }
        else
        {
            left = Build(binary.Left, parameter, null);
            right = Build(binary.Right, parameter, left.Type);
        }

        Coerce(ref left, ref right);

        return binary.Op switch
        {
            BinaryOp.Equal => Expression.Equal(left, right),
            BinaryOp.NotEqual => Expression.NotEqual(left, right),
            BinaryOp.LessThan => Expression.LessThan(left, right),
            BinaryOp.LessThanOrEqual => Expression.LessThanOrEqual(left, right),
            BinaryOp.GreaterThan => Expression.GreaterThan(left, right),
            BinaryOp.GreaterThanOrEqual => Expression.GreaterThanOrEqual(left, right),
            BinaryOp.Add => Expression.Add(left, right),
            BinaryOp.Subtract => Expression.Subtract(left, right),
            BinaryOp.Multiply => Expression.Multiply(left, right),
            BinaryOp.Divide => Expression.Divide(left, right),
            _ => throw new ScryValidationException($"Unsupported binary operator '{binary.Op}'.")
        };
    }

    Expression BuildUnary(UnaryExpr unary, ParameterExpression parameter)
    {
        var operand = Build(unary.Operand, parameter, unary.Op == UnaryOp.Not ? typeof(bool) : null);
        return unary.Op switch
        {
            UnaryOp.Not => Expression.Not(operand),
            UnaryOp.Negate => Expression.Negate(operand),
            _ => throw new ScryValidationException($"Unsupported unary operator '{unary.Op}'.")
        };
    }

    Expression BuildCall(CallExpr call, ParameterExpression parameter)
    {
        var target = Build(call.Target, parameter, null);
        var arguments = call.Arguments.Select(_ => Build(_, parameter, typeof(string))).ToArray();

        return call.Function switch
        {
            KnownFunction.StringContains => Expression.Call(target, StringMethod("Contains", typeof(string)), arguments[0]),
            KnownFunction.StringStartsWith => Expression.Call(target, StringMethod("StartsWith", typeof(string)), arguments[0]),
            KnownFunction.StringEndsWith => Expression.Call(target, StringMethod("EndsWith", typeof(string)), arguments[0]),
            KnownFunction.StringToLower => Expression.Call(target, StringMethod("ToLower")),
            KnownFunction.StringToUpper => Expression.Call(target, StringMethod("ToUpper")),
            KnownFunction.StringIsNullOrEmpty => Expression.Call(typeof(string), nameof(string.IsNullOrEmpty), null, target),
            KnownFunction.DateYear => Expression.Property(target, "Year"),
            KnownFunction.DateMonth => Expression.Property(target, "Month"),
            KnownFunction.DateDay => Expression.Property(target, "Day"),
            _ => throw new ScryValidationException($"Unsupported function '{call.Function}'.")
        };
    }

    static Expression BuildConstant(ConstExpr constant, Type? expected)
    {
        var target = expected ?? TagToType(constant.Tag);
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (constant.Value is null ||
            constant.Tag == ClrTypeTag.Null)
        {
            if (target.IsValueType &&
                Nullable.GetUnderlyingType(target) is null)
            {
                var nullable = typeof(Nullable<>).MakeGenericType(target);
                return Expression.Constant(null, nullable);
            }

            return Expression.Constant(null, target);
        }

        var parsed = ParseValue(constant.Value, underlying);
        return Expression.Constant(parsed, underlying);
    }

    Expression BuildAggregate(AggregateExpr aggregate, ParameterExpression group, Type elementType)
    {
        if (aggregate.Function == AggregateFn.Count)
        {
            var count = typeof(Enumerable).GetMethods()
                .Single(_ => _ is { Name: "Count", IsGenericMethodDefinition: true } && _.GetParameters().Length == 1)
                .MakeGenericMethod(elementType);
            return Expression.Call(count, group);
        }

        if (aggregate.Selector is not MemberExpr memberExpr)
        {
            throw new ScryValidationException($"Aggregate '{aggregate.Function}' requires a member selector.");
        }

        var selectorParameter = Expression.Parameter(elementType, "x");
        var selectorBody = BuildMemberAccess(selectorParameter, memberExpr.Path);
        var selector = Expression.Lambda(selectorBody, selectorParameter);
        var returnType = selectorBody.Type;

        return aggregate.Function switch
        {
            AggregateFn.Sum => Expression.Call(SumOrAverage("Sum", elementType, returnType), group, selector),
            AggregateFn.Average => Expression.Call(SumOrAverage("Average", elementType, returnType), group, selector),
            AggregateFn.Min => Expression.Call(MinOrMax("Min", elementType, returnType), group, selector),
            AggregateFn.Max => Expression.Call(MinOrMax("Max", elementType, returnType), group, selector),
            _ => throw new ScryValidationException($"Unsupported aggregate '{aggregate.Function}'.")
        };
    }

    static MethodInfo SumOrAverage(string name, Type elementType, Type selectorReturnType) =>
        typeof(Enumerable).GetMethods()
            .Single(_ =>
                _.Name == name &&
                _.IsGenericMethodDefinition &&
                _.GetParameters().Length == 2 &&
                _.GetParameters()[1].ParameterType.GetGenericArguments()[1] == selectorReturnType)
            .MakeGenericMethod(elementType);

    static MethodInfo MinOrMax(string name, Type elementType, Type selectorReturnType) =>
        typeof(Enumerable).GetMethods()
            .Single(_ =>
                _.Name == name &&
                _.IsGenericMethodDefinition &&
                _.GetGenericArguments().Length == 2 &&
                _.GetParameters().Length == 2)
            .MakeGenericMethod(elementType, selectorReturnType);

    static Expression ToObjectArray(List<Expression> leaves) =>
        Expression.NewArrayInit(typeof(object), leaves.Select(Box));

    static Expression Box(Expression expression) =>
        expression.Type == typeof(object) ? expression : Expression.Convert(expression, typeof(object));

    static void Coerce(ref Expression left, ref Expression right)
    {
        if (left.Type == right.Type)
        {
            return;
        }

        var leftNullable = Nullable.GetUnderlyingType(left.Type);
        var rightNullable = Nullable.GetUnderlyingType(right.Type);

        if (leftNullable == right.Type)
        {
            right = Expression.Convert(right, left.Type);
        }
        else if (rightNullable == left.Type)
        {
            left = Expression.Convert(left, right.Type);
        }
    }

    static MethodInfo StringMethod(string name, params Type[] parameters) =>
        typeof(string).GetMethod(name, parameters) ??
        throw new ScryValidationException($"string.{name} not found.");

    static Type TagToType(ClrTypeTag tag) =>
        tag switch
        {
            ClrTypeTag.String => typeof(string),
            ClrTypeTag.Boolean => typeof(bool),
            ClrTypeTag.Int32 => typeof(int),
            ClrTypeTag.Int64 => typeof(long),
            ClrTypeTag.Decimal => typeof(decimal),
            ClrTypeTag.Double => typeof(double),
            ClrTypeTag.DateTime => typeof(DateTime),
            ClrTypeTag.DateOnly => typeof(Date),
            ClrTypeTag.Guid => typeof(Guid),
            _ => typeof(string)
        };

    static object ParseValue(string value, Type underlying)
    {
        if (underlying.IsEnum)
        {
            return Enum.Parse(underlying, value);
        }

        var culture = CultureInfo.InvariantCulture;
        if (underlying == typeof(string))
        {
            return value;
        }

        if (underlying == typeof(bool))
        {
            return bool.Parse(value);
        }

        if (underlying == typeof(int))
        {
            return int.Parse(value, culture);
        }

        if (underlying == typeof(long))
        {
            return long.Parse(value, culture);
        }

        if (underlying == typeof(decimal))
        {
            return decimal.Parse(value, culture);
        }

        if (underlying == typeof(double))
        {
            return double.Parse(value, culture);
        }

        if (underlying == typeof(DateTime))
        {
            return DateTime.Parse(value, culture, DateTimeStyles.RoundtripKind);
        }

        if (underlying == typeof(Date))
        {
            return Date.Parse(value, culture);
        }

        if (underlying == typeof(Time))
        {
            return Time.Parse(value, culture);
        }

        if (underlying == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(value, culture, DateTimeStyles.RoundtripKind);
        }

        if (underlying == typeof(Guid))
        {
            return Guid.Parse(value);
        }

        return Convert.ChangeType(value, underlying, culture);
    }
}