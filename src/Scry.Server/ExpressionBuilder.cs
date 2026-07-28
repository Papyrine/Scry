/// <summary>
/// Rebinds a validated query AST onto real CLR <see cref="Expression"/> trees over the server's
/// entity types. This is the only place CLR types are introduced — always from the schema, never
/// from the wire.
/// </summary>
sealed class ExpressionBuilder(Schema schema)
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

    /// <summary>
    /// Builds a page selector <c>TElement =&gt; object[]</c> whose leading slots are the requested (or
    /// default) projection leaves and whose trailing <c>KeyCount</c> slots are the ordering-key values.
    /// One materialization then yields both the shaped rows and the last row's key values (for the next
    /// cursor). <c>Shape</c> describes only the leading projection slots.
    /// </summary>
    public (LambdaExpression Selector, IReadOnlyList<IReadOnlyList<string>> Shape, int KeyCount) BuildPageProjection(
        Projection? projection,
        IReadOnlyList<(Node Key, bool Descending)> keys,
        Type type)
    {
        var parameter = Expression.Parameter(type, "e");
        var leaves = new List<Expression>();
        var shape = new List<IReadOnlyList<string>>();

        if (projection is null)
        {
            if (!schema.TryGetType(type, out var meta))
            {
                throw new ScryValidationException($"Type '{type.Name}' is not queryable.");
            }

            foreach (var member in meta.Members.Values.Where(_ => _.Kind == MemberKind.Scalar))
            {
                leaves.Add(Expression.Property(parameter, member.Property));
                shape.Add([member.Name]);
            }
        }
        else
        {
            Flatten(projection, parameter, [], leaves, shape);
        }

        // Trailing slots: the ordering-key values, used only to build the next page's cursor.
        foreach (var (key, _) in keys)
        {
            leaves.Add(Build(key, parameter, null));
        }

        var selector = Expression.Lambda(ToObjectArray(leaves), parameter);
        return (selector, shape, keys.Count);
    }

    /// <summary>
    /// Builds the keyset seek predicate <c>TElement =&gt; bool</c> that resumes past a cursor:
    /// <c>OR_i ( AND_{j&lt;i} (k_j == c_j)  AND  (k_i &gt; c_i for ascending, k_i &lt; c_i for descending) )</c>.
    /// The keys are the (already validated) ordering members plus the appended primary key; the values
    /// are the decoded cursor constants, rebound against each key's real type.
    /// </summary>
    public LambdaExpression BuildSeekPredicate(
        IReadOnlyList<(Node Key, bool Descending)> keys,
        IReadOnlyList<ConstNode> values,
        Type type)
    {
        var parameter = Expression.Parameter(type, "e");

        Expression? disjunction = null;
        for (var i = 0; i < keys.Count; i++)
        {
            Expression? conjunction = null;
            for (var j = 0; j < i; j++)
            {
                var equal = CompareKey(keys[j].Key, values[j], parameter, strictGreater: null, descending: false);
                conjunction = conjunction is null ? equal : Expression.AndAlso(conjunction, equal);
            }

            var strict = CompareKey(keys[i].Key, values[i], parameter, strictGreater: true, keys[i].Descending);
            var term = conjunction is null ? strict : Expression.AndAlso(conjunction, strict);
            disjunction = disjunction is null ? term : Expression.OrElse(disjunction, term);
        }

        return Expression.Lambda(disjunction!, parameter);
    }

    // Builds one comparison of an ordering key against a cursor value. strictGreater null => equality;
    // otherwise a strict inequality flipped by descending. Strings compare via string.Compare so EF
    // translates them to a SQL relational comparison under the column collation (matching the ORDER BY).
    Expression CompareKey(Node keyNode, ConstNode value, ParameterExpression parameter, bool? strictGreater, bool descending)
    {
        var left = Build(keyNode, parameter, null);
        var right = BuildConstant(value, left.Type);
        Coerce(ref left, ref right);

        if (strictGreater is null)
        {
            return Expression.Equal(left, right);
        }

        var greater = strictGreater.Value ^ descending;

        if (left.Type == typeof(string))
        {
            var comparison = Expression.Call(stringCompare, left, right);
            var zero = Expression.Constant(0);
            return greater ? Expression.GreaterThan(comparison, zero) : Expression.LessThan(comparison, zero);
        }

        // Enums do not carry relational operators through Expression; compare their underlying value.
        if (left.Type.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(left.Type);
            left = Expression.Convert(left, underlying);
            right = Expression.Convert(right, underlying);
        }

        return greater ? Expression.GreaterThan(left, right) : Expression.LessThan(left, right);
    }

    /// <summary>
    /// Builds a default projection of every allow-listed scalar member of the source. Only reached for
    /// a request that named no members: a generated client always sends an explicit projection, so its
    /// response keys are its own names rather than the server's.
    /// </summary>
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
            var expression = ((NodeValue)member.Value).Node;
            switch (expression)
            {
                case AggregateNode aggregate:
                {
                    var leaf = BuildAggregate(aggregate, parameter, element);
                    leaves.Add(Box(leaf));
                    break;
                }
                case MemberNode:
                {
                    var leaf = Expression.Property(parameter, "Key");
                    leaves.Add(Box(leaf));
                    break;
                }
                default:
                    throw new ScryValidationException("Unsupported grouped projection member.");
            }

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
                case NodeValue { Node: MemberNode memberNode }:
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
            // Traversing into an optional struct complex member (Nullable<T>): resolve against the
            // underlying type and unwrap via .Value before accessing the child property.
            var underlying = Nullable.GetUnderlyingType(expression.Type);
            var ownerType = underlying ?? expression.Type;
            if (!schema.TryGetType(ownerType, out var meta) ||
                !meta.TryGetMember(segment, out var member))
            {
                throw new ScryValidationException(
                    $"Property '{segment}' is not allow-listed on '{ownerType.Name}'.");
            }

            if (underlying is not null)
            {
                expression = Expression.Property(expression, "Value");
            }

            expression = Expression.Property(expression, member.Property);
        }

        return expression;
    }

    BinaryExpression BuildBinary(BinaryNode binary, ParameterExpression parameter)
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

    UnaryExpression BuildUnary(UnaryNode unary, ParameterExpression parameter)
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

    Expression BuildConstant(ConstNode constant, Type? expected)
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

    MethodCallExpression BuildAggregate(AggregateNode aggregate, ParameterExpression group, Type element)
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

    static NewArrayExpression ToObjectArray(List<Expression> leaves) =>
        Expression.NewArrayInit(typeof(object), leaves.Select(Box));

    static Expression Box(Expression expression)
    {
        if (expression.Type == typeof(object))
        {
            return expression;
        }

        return Expression.Convert(expression, typeof(object));
    }

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

    // string.Compare(string, string) — the seek predicate uses it so a string ordering key rebinds to
    // a SQL relational comparison (EF has no translation for the > / < operators on string directly).
    static readonly MethodInfo stringCompare = StringMethod("Compare", typeof(string), typeof(string));

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
            ClrTypeTag.Bytes => typeof(byte[]),
            _ => typeof(string)
        };

    object ParseValue(string value, Type underlying)
    {
        if (underlying.IsEnum)
        {
            var resolved = schema.ResolveEnumValue(underlying, value);
            try
            {
                return Enum.Parse(underlying, resolved);
            }
            catch (ArgumentException)
            {
                // A value the client knows and the server does not — a stale client after the value was
                // renamed (without a [PreviousNames] entry) or removed. Report it as a rejected query
                // rather than letting it surface as a server fault.
                throw new ScryValidationException($"'{value}' is not a value of enum '{underlying.Name}'.");
            }
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

        if (underlying == typeof(byte[]))
        {
            return Convert.FromBase64String(value);
        }

        return Convert.ChangeType(value, underlying, culture);
    }
}
