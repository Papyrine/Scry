/// <summary>
/// Rebinds a validated query AST onto real CLR <see cref="Expression"/> trees over the server's
/// entity types. This is the only place CLR types are introduced — always from the schema, never
/// from the wire.
/// </summary>
sealed class ExpressionBuilder(ScrySchema schema)
{
    /// <summary>Builds a predicate lambda <c>TElement =&gt; bool</c>.</summary>
    public LambdaExpression BuildPredicate(Node predicate, Type type)
    {
        var parameter = Expression.Parameter(type, "e");
        var body = Build(predicate, parameter, typeof(bool));
        return Expression.Lambda(body, parameter);
    }

    /// <summary>Builds a key selector lambda <c>TElement =&gt; TKey</c>.</summary>
    public LambdaExpression BuildKeySelector(Node key, Type type)
    {
        var parameter = Expression.Parameter(type, "e");
        var body = Build(key, parameter, null);
        return Expression.Lambda(body, parameter);
    }

    /// <summary>
    /// Builds a selector <c>TElement =&gt; object[]</c> projecting the requested scalar leaves, plus a
    /// shape describing how to fold the array back into (possibly nested) JSON.
    /// </summary>
    public ProjectionPlan BuildProjection(Projection projection, Type type)
    {
        var parameter = Expression.Parameter(type, "e");
        var leaves = new List<Expression>();
        var shape = new List<IReadOnlyList<string>>();
        Flatten(projection, parameter, [], leaves, shape);
        var selector = Expression.Lambda(ToObjectArray(leaves), parameter);
        return new(selector, shape);
    }

    /// <summary>Builds a default projection of every allow-listed scalar member of the source.</summary>
    public ProjectionPlan BuildDefaultProjection(Type type)
    {
        if (!schema.TryGetType(type, out var meta))
        {
            throw new ScryValidationException($"Type '{type.Name}' is not queryable.");
        }

        var parameter = Expression.Parameter(type, "e");
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
    public ProjectionPlan BuildGroupProjection(Projection projection, Type element, Type key)
    {
        var groupingType = typeof(IGrouping<,>).MakeGenericType(key, element);
        var parameter = Expression.Parameter(groupingType, "g");
        var leaves = new List<Expression>();
        var shape = new List<IReadOnlyList<string>>();

        foreach (var member in projection.Members)
        {
            var expression = ((ExprValue)member.Value).Expression;
            var leaf = expression switch
            {
                AggregateNode aggregate => BuildAggregate(aggregate, parameter, element),
                MemberNode => Expression.Property(parameter, "Key"),
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
                case ExprValue { Expression: MemberNode memberNode }:
                    leaves.Add(BuildMemberAccess(root, memberNode.Path));
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

    Expression Build(Node node, ParameterExpression parameter, Type? expected) =>
        node switch
        {
            MemberNode member => BuildMemberAccess(parameter, member.Path),
            ConstNode constant => BuildConstant(constant, expected),
            BinaryNode binary => BuildBinary(binary, parameter),
            UnaryNode unary => BuildUnary(unary, parameter),
            CallNode call => BuildCall(call, parameter),
            _ => throw new ScryValidationException($"Unsupported expression '{node.GetType().Name}'.")
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

    Expression BuildBinary(BinaryNode binary, ParameterExpression parameter)
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
        if (binary is {Left: ConstNode, Right: not ConstNode})
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

    Expression BuildUnary(UnaryNode unary, ParameterExpression parameter)
    {
        var operand = Build(unary.Operand, parameter, unary.Op == UnaryOp.Not ? typeof(bool) : null);
        return unary.Op switch
        {
            UnaryOp.Not => Expression.Not(operand),
            UnaryOp.Negate => Expression.Negate(operand),
            _ => throw new ScryValidationException($"Unsupported unary operator '{unary.Op}'.")
        };
    }

    Expression BuildCall(CallNode call, ParameterExpression parameter)
    {
        var target = Build(call.Target, parameter, null);
        var arguments = call.Arguments.Select(_ => Build(_, parameter, typeof(string))).ToArray();

        return call.Function switch
        {
            KnownFunction.StringContains => Expression.Call(target, stringContains, arguments[0]),
            KnownFunction.StringStartsWith => Expression.Call(target, stringStartsWith, arguments[0]),
            KnownFunction.StringEndsWith => Expression.Call(target, stringEndsWith, arguments[0]),
            KnownFunction.StringToLower => Expression.Call(target, stringToLower),
            KnownFunction.StringToUpper => Expression.Call(target, stringToUpper),
            KnownFunction.StringIsNullOrEmpty => Expression.Call(stringIsNullOrEmpty, target),
            KnownFunction.DateYear => Expression.Property(target, "Year"),
            KnownFunction.DateMonth => Expression.Property(target, "Month"),
            KnownFunction.DateDay => Expression.Property(target, "Day"),
            _ => throw new ScryValidationException($"Unsupported function '{call.Function}'.")
        };
    }

    static Expression BuildConstant(ConstNode constant, Type? expected)
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

    Expression BuildAggregate(AggregateNode aggregate, ParameterExpression group, Type element)
    {
        if (aggregate.Function == AggregateFn.Count)
        {
            return Expression.Call(enumerableCount.MakeGenericMethod(element), group);
        }

        if (aggregate.Selector is not MemberNode member)
        {
            throw new ScryValidationException($"Aggregate '{aggregate.Function}' requires a member selector.");
        }

        var selectorParameter = Expression.Parameter(element, "x");
        var selectorBody = BuildMemberAccess(selectorParameter, member.Path);
        var selector = Expression.Lambda(selectorBody, selectorParameter);
        var returnType = selectorBody.Type;

        return aggregate.Function switch
        {
            AggregateFn.Sum => Expression.Call(SumOrAverage("Sum", element, returnType), group, selector),
            AggregateFn.Average => Expression.Call(SumOrAverage("Average", element, returnType), group, selector),
            AggregateFn.Min => Expression.Call(MinOrMax("Min", element, returnType), group, selector),
            AggregateFn.Max => Expression.Call(MinOrMax("Max", element, returnType), group, selector),
            _ => throw new ScryValidationException($"Unsupported aggregate '{aggregate.Function}'.")
        };
    }

    static MethodInfo SumOrAverage(string name, Type element, Type selectorReturnType) =>
        aggregateMethods.GetOrAdd(
            (name, element, selectorReturnType),
            // Sum/Average have one selector overload per numeric type (int, decimal, …), generic only
            // in the source element — pick it by the selector's return type, then close it.
            key => typeof(Enumerable).GetMethods()
                .Single(_ =>
                    _.Name == key.name &&
                    _.IsGenericMethodDefinition &&
                    _.GetParameters().Length == 2 &&
                    _.GetParameters()[1].ParameterType.GetGenericArguments()[1] == key.result)
                .MakeGenericMethod(key.element));

    static MethodInfo MinOrMax(string name, Type element, Type selectorReturnType) =>
        aggregateMethods.GetOrAdd(
            (name, element, selectorReturnType),
            // Min/Max use the fully generic Min<TSource,TResult>(source, selector) overload, closed
            // over both the element and the selector's return type.
            key => typeof(Enumerable).GetMethods()
                .Single(_ =>
                    _.Name == key.name &&
                    _.IsGenericMethodDefinition &&
                    _.GetGenericArguments().Length == 2 &&
                    _.GetParameters().Length == 2)
                .MakeGenericMethod(key.element, key.result));

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

    // The methods the builder can emit are a fixed, deterministic set, so scanning string/Enumerable
    // metadata on every query is wasted work. Resolve them once. One ExpressionBuilder is shared across
    // all concurrent requests (it hangs off the singleton ScryProcessor), so these caches are static
    // and thread-safe.
    static readonly MethodInfo stringContains = StringMethod("Contains", typeof(string));
    static readonly MethodInfo stringStartsWith = StringMethod("StartsWith", typeof(string));
    static readonly MethodInfo stringEndsWith = StringMethod("EndsWith", typeof(string));
    static readonly MethodInfo stringToLower = StringMethod("ToLower");
    static readonly MethodInfo stringToUpper = StringMethod("ToUpper");
    static readonly MethodInfo stringIsNullOrEmpty = StringMethod("IsNullOrEmpty", typeof(string));

    // The generic Count<TSource>(source) definition is type-independent; only MakeGenericMethod varies.
    static readonly MethodInfo enumerableCount = typeof(Enumerable).GetMethods()
        .Single(_ =>
            _ is { Name: "Count", IsGenericMethodDefinition: true } &&
            _.GetParameters().Length == 1);

    // Closed Sum/Average/Min/Max methods, keyed by (name, element type, selector return type). The
    // key space is bounded by the queryable schema, so this never grows unboundedly.
    static readonly ConcurrentDictionary<(string name, Type element, Type result), MethodInfo> aggregateMethods = new();

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