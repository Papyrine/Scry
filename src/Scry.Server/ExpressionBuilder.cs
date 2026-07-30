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
    /// Builds a join's result selector <c>(TOuter, TInner) =&gt; object[]</c>, plus the shape describing
    /// its slots. The join projects straight to the shaped row, so the joined pair never becomes an
    /// element type of its own — which is what keeps every later operator single-rooted.
    /// </summary>
    /// <remarks>
    /// Under a left join the inner row can be absent, so a non-nullable value read from that side is
    /// widened to its nullable form; without that the shaper would fault materializing a SQL NULL.
    /// </remarks>
    public (LambdaExpression Selector, IReadOnlyList<IReadOnlyList<string>> Shape) BuildJoinProjection(
        IReadOnlyList<JoinMember> members,
        Type outerType,
        Type innerType,
        JoinKind kind)
    {
        var outer = Expression.Parameter(outerType, "o");
        var inner = Expression.Parameter(innerType, "i");
        var leaves = new List<Expression>();
        var shape = new List<IReadOnlyList<string>>();

        foreach (var member in members)
        {
            var root = member.Side == JoinSide.Outer ? (Expression)outer : inner;
            var leaf = BuildMemberAccess(root, member.Path);

            if (kind == JoinKind.Left &&
                member.Side == JoinSide.Inner &&
                leaf.Type.IsValueType &&
                Nullable.GetUnderlyingType(leaf.Type) is null)
            {
                leaf = Expression.Convert(leaf, typeof(Nullable<>).MakeGenericType(leaf.Type));
            }

            leaves.Add(leaf);
            shape.Add([member.Name]);
        }

        return (Expression.Lambda(ToObjectArray(leaves), outer, inner), shape);
    }

    /// <summary>
    /// Builds a join key selector. Both sides must produce the same key type for the provider to join
    /// on, so a nullable difference between them is reconciled here rather than faulting.
    /// </summary>
    public (LambdaExpression Outer, LambdaExpression Inner) BuildJoinKeys(
        Node outerKey,
        Type outerType,
        Node innerKey,
        Type innerType)
    {
        var outerParameter = Expression.Parameter(outerType, "o");
        var innerParameter = Expression.Parameter(innerType, "i");
        var outerBody = Build(outerKey, outerParameter, null);
        var innerBody = Build(innerKey, innerParameter, null);

        Coerce(ref outerBody, ref innerBody);
        if (outerBody.Type != innerBody.Type)
        {
            throw new ScryValidationException(
                $"Join keys must have the same type, but were '{outerBody.Type.Name}' and '{innerBody.Type.Name}'.");
        }

        return (
            Expression.Lambda(outerBody, outerParameter),
            Expression.Lambda(innerBody, innerParameter));
    }

    /// <summary>
    /// Builds a selector projecting into a <see cref="DistinctRow"/> — one typed property per leaf —
    /// plus the shape those leaves fold back into. This is what lets a deduplicated projection be
    /// ordered, paged or counted: the shaped <c>object[]</c> row has no equality or ordering of its own.
    /// Returns null when the projection has more leaves than there are row arities.
    /// </summary>
    /// <remarks>
    /// The member mappings passed to <see cref="Expression.New(ConstructorInfo,IEnumerable{Expression},IEnumerable{MemberInfo})"/>
    /// are the point of the whole thing. Without them a provider sees an opaque constructor call and
    /// cannot decompose the projection into columns, so it can neither push it into a subquery to count
    /// it nor order by one of its members — which is exactly how a plain record behaves, and why one
    /// is not enough on its own.
    /// </remarks>
    public (LambdaExpression Selector, IReadOnlyList<IReadOnlyList<string>> Shape)? BuildDistinctRow(
        Projection projection,
        Type type)
    {
        var parameter = Expression.Parameter(type, "e");
        var leaves = new List<Expression>();
        var shape = new List<IReadOnlyList<string>>();
        Flatten(projection, parameter, [], leaves, shape);

        if (leaves.Count == 0 ||
            leaves.Count > DistinctRow.ByArity.Length)
        {
            return null;
        }

        var row = DistinctRow.ByArity[leaves.Count - 1].MakeGenericType([..leaves.Select(_ => _.Type)]);
        var constructor = row.GetConstructors().Single();
        var members = constructor.GetParameters()
            .Select(_ => (MemberInfo)row.GetProperty(_.Name!)!)
            .ToArray();

        return (Expression.Lambda(Expression.New(constructor, leaves, members), parameter), shape);
    }

    /// <summary>Reads a <see cref="DistinctRow"/>'s values back out, in projection order.</summary>
    public static object[] ReadDistinctRow(object row, int count)
    {
        var type = row.GetType();
        var values = new object[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = type.GetProperty($"Value{i + 1}")!.GetValue(row)!;
        }

        return values;
    }

    /// <summary>Builds a key selector over a <see cref="DistinctRow"/> for the leaf at <paramref name="index"/>.</summary>
    public static LambdaExpression BuildDistinctRowKey(Type row, int index)
    {
        var parameter = Expression.Parameter(row, "r");
        return Expression.Lambda(Expression.Property(parameter, $"Value{index + 1}"), parameter);
    }

    /// <summary>
    /// Builds a selector for a projection of exactly one member, typed as that member rather than
    /// boxed into the <c>object[]</c> a shaped row uses. The validator guarantees the shape; this is
    /// what lets a deduplicated sequence be folded to a scalar.
    /// </summary>
    public LambdaExpression BuildSingleValueSelector(Projection projection, Type type)
    {
        var parameter = Expression.Parameter(type, "e");
        var leaves = new List<Expression>();
        Flatten(projection, parameter, [], leaves, []);
        if (leaves.Count != 1)
        {
            throw new ScryValidationException("Expected a projection of exactly one member.");
        }

        return Expression.Lambda(leaves[0], parameter);
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
            if (member.Value is not NodeValue value)
            {
                throw new ScryValidationException("Unsupported grouped projection member.");
            }

            // Reading the group is what Build already does when the row is a grouping.
            leaves.Add(Box(Build(value.Node, parameter, null)));
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
                // A leaf is any validated scalar expression, not only a member path — the same
                // vocabulary a predicate is built from, rooted at whatever part of the row this
                // projection level describes.
                case NodeValue value:
                    leaves.Add(Build(value.Node, root, null));
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

    /// <summary>
    /// Builds the selector for a whole-sequence aggregate, typed so the aggregate is one the provider
    /// can actually run: <c>Min</c>/<c>Max</c> select a nullable so an empty sequence yields null
    /// instead of faulting, and <c>Sum</c>/<c>Average</c> widen the member to the nearest numeric type
    /// with a <see cref="Queryable"/> overload (the wire says which member to fold, never which CLR
    /// type to fold it as).
    /// </summary>
    public LambdaExpression BuildAggregateSelector(Node selector, Type type, AggregateFn function)
    {
        var parameter = Expression.Parameter(type, "e");
        var body = Build(selector, parameter, null);

        if (function is AggregateFn.Min or AggregateFn.Max)
        {
            if (body.Type.IsValueType &&
                Nullable.GetUnderlyingType(body.Type) is null)
            {
                body = Expression.Convert(body, typeof(Nullable<>).MakeGenericType(body.Type));
            }
        }
        else
        {
            var promoted = PromoteNumeric(body.Type) ??
                           throw new ScryValidationException(
                               $"'{function}' is not supported over '{body.Type.Name}'.");
            if (promoted != body.Type)
            {
                body = Expression.Convert(body, promoted);
            }
        }

        return Expression.Lambda(body, parameter);
    }

    // The numeric types Queryable.Sum/Average have overloads for. Narrower members widen to the
    // smallest of them that holds every value; anything not numeric has no aggregate at all.
    static Type? PromoteNumeric(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var value = underlying ?? type;

        var promoted = Type.GetTypeCode(value) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 => typeof(int),
            TypeCode.UInt32 or TypeCode.Int64 => typeof(long),
            TypeCode.UInt64 or TypeCode.Decimal => typeof(decimal),
            TypeCode.Single => typeof(float),
            TypeCode.Double => typeof(double),
            _ => null
        };

        if (promoted is null)
        {
            return null;
        }

        if (underlying is null)
        {
            return promoted;
        }

        return typeof(Nullable<>).MakeGenericType(promoted);
    }

    /// <summary>
    /// Builds one expression against the row it reads. When that row is an
    /// <see cref="IGrouping{TKey,TElement}"/> — a <c>HAVING</c> predicate or a grouped projection — the
    /// same two nodes mean something else: a member path is the key the query grouped by, and an
    /// aggregate folds the group's rows. Both are read off the row's own type, so nothing has to be
    /// threaded through the recursion.
    /// </summary>
    Expression Build(Node node, Expression row, Type? expected) =>
        node switch
        {
            MemberNode when IsGrouping(row.Type) => Expression.Property(row, "Key"),
            AggregateNode aggregate when IsGrouping(row.Type) =>
                BuildAggregate(aggregate, (ParameterExpression)row, row.Type.GetGenericArguments()[1]),
            MemberNode member => BuildMemberAccess(row, member.Path),
            ConstNode constant => BuildConstant(constant, expected),
            BinaryNode binary => BuildBinary(binary, row),
            UnaryNode unary => BuildUnary(unary, row),
            CallNode call => BuildCall(call, row),
            ConditionalNode conditional => BuildConditional(conditional, row),
            SubqueryNode subquery => BuildSubquery(subquery, row),
            _ => throw new ScryValidationException($"Unsupported expression '{node.GetType().Name}'.")
        };

    /// <summary>
    /// Rebinds a question about a collection navigation onto the <see cref="Enumerable"/> call EF
    /// translates into a correlated subquery. The inner predicate and selector are lambdas over the
    /// collection's element, so they are built against a parameter of that type rather than the row.
    /// </summary>
    Expression BuildSubquery(SubqueryNode subquery, Expression row)
    {
        var collection = BuildMemberAccess(row, subquery.Path);
        var element = Schema.CollectionElement(collection.Type) ??
                      throw new ScryValidationException($"'{string.Join('.', subquery.Path)}' is not a collection.");

        // All takes its predicate directly; for everything else a predicate narrows the collection
        // first, which is what lets Count and the aggregates carry one at all.
        var source = collection;
        if (subquery.Predicate is { } filter &&
            subquery.Function != SubqueryFn.All)
        {
            source = Expression.Call(
                enumerableWhere.MakeGenericMethod(element),
                source,
                ElementLambda(filter, element, typeof(bool)));
        }

        switch (subquery.Function)
        {
            case SubqueryFn.Any:
                return Expression.Call(enumerableAny.MakeGenericMethod(element), source);

            case SubqueryFn.All:
                return Expression.Call(
                    enumerableAll.MakeGenericMethod(element),
                    source,
                    ElementLambda(subquery.Predicate!, element, typeof(bool)));

            case SubqueryFn.Count:
                return Expression.Call(enumerableCount.MakeGenericMethod(element), source);
        }

        var parameter = Expression.Parameter(element, "x");
        var body = Build(subquery.Selector!, parameter, null);

        // Min/Max over an empty collection is SQL NULL, so the selected value is made nullable rather
        // than faulting when a row has no elements.
        if (subquery.Function is SubqueryFn.Min or SubqueryFn.Max &&
            body.Type.IsValueType &&
            Nullable.GetUnderlyingType(body.Type) is null)
        {
            body = Expression.Convert(body, typeof(Nullable<>).MakeGenericType(body.Type));
        }

        var selector = Expression.Lambda(body, parameter);
        return subquery.Function switch
        {
            SubqueryFn.Sum => Expression.Call(SumOrAverage("Sum", element, body.Type), source, selector),
            SubqueryFn.Average => Expression.Call(SumOrAverage("Average", element, body.Type), source, selector),
            SubqueryFn.Min => Expression.Call(MinOrMax("Min", element, body.Type), source, selector),
            SubqueryFn.Max => Expression.Call(MinOrMax("Max", element, body.Type), source, selector),
            _ => throw new ScryValidationException($"Unsupported subquery function '{subquery.Function}'.")
        };
    }

    LambdaExpression ElementLambda(Node node, Type element, Type? expected)
    {
        var parameter = Expression.Parameter(element, "x");
        return Expression.Lambda(Build(node, parameter, expected), parameter);
    }

    static bool IsGrouping(Type type) =>
        type.IsGenericType &&
        type.GetGenericTypeDefinition() == typeof(IGrouping<,>);

    /// <summary>
    /// Builds a <c>HAVING</c> predicate <c>IGrouping&lt;TKey,TElement&gt; =&gt; bool</c>, filtering the
    /// groups a <c>GroupBy</c> produced rather than the rows that fed it.
    /// </summary>
    public LambdaExpression BuildGroupPredicate(Node predicate, Type element, Type key)
    {
        var parameter = Expression.Parameter(typeof(IGrouping<,>).MakeGenericType(key, element), "g");
        return Expression.Lambda(Build(predicate, parameter, typeof(bool)), parameter);
    }

    Expression BuildConditional(ConditionalNode conditional, Expression row)
    {
        var test = Build(conditional.Test, row, typeof(bool));
        var ifTrue = Build(conditional.IfTrue, row, null);
        var ifFalse = Build(conditional.IfFalse, row, ifTrue.Type);
        Coerce(ref ifTrue, ref ifFalse);

        if (ifTrue.Type != ifFalse.Type)
        {
            throw new ScryValidationException(
                $"The branches of a conditional must have the same type, but were '{ifTrue.Type.Name}' and '{ifFalse.Type.Name}'.");
        }

        return Expression.Condition(test, ifTrue, ifFalse);
    }

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
                throw new ScryValidationException($"Property '{segment}' is not allow-listed on '{ownerType.Name}'.");
            }

            if (underlying is not null)
            {
                expression = Expression.Property(expression, "Value");
            }

            expression = Expression.Property(expression, member.Property);
        }

        return expression;
    }

    Expression BuildBinary(BinaryNode binary, Expression row)
    {
        if (binary.Op == BinaryOp.Coalesce)
        {
            var fallbackOf = Build(binary.Left, row, null);
            if (fallbackOf.Type.IsValueType &&
                Nullable.GetUnderlyingType(fallbackOf.Type) is null)
            {
                throw new ScryValidationException(
                    $"Coalesce requires a nullable left operand, but '{fallbackOf.Type.Name}' cannot be null.");
            }

            var fallback = Build(
                binary.Right,
                row,
                Nullable.GetUnderlyingType(fallbackOf.Type) ?? fallbackOf.Type);
            return Expression.Coalesce(fallbackOf, fallback);
        }

        if (binary.Op is BinaryOp.AndAlso or BinaryOp.OrElse)
        {
            var leftBool = Build(binary.Left, row, typeof(bool));
            var rightBool = Build(binary.Right, row, typeof(bool));
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
            right = Build(binary.Right, row, null);
            left = Build(binary.Left, row, right.Type);
        }
        else
        {
            left = Build(binary.Left, row, null);
            right = Build(binary.Right, row, left.Type);
        }

        Coerce(ref left, ref right);
        PromoteNumeric(ref left, ref right);

        // C# compiles string concatenation to an Add carrying string.Concat as its method. The wire
        // records the operator, never the method, so the concatenation is reconstructed here from the
        // operand types — an Add of two strings can mean nothing else.
        if (binary.Op == BinaryOp.Add &&
            left.Type == typeof(string) &&
            right.Type == typeof(string))
        {
            return Expression.Call(stringConcat, left, right);
        }

        try
        {
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
                BinaryOp.Modulo => Expression.Modulo(left, right),
                _ => throw new ScryValidationException($"Unsupported binary operator '{binary.Op}'.")
            };
        }
        // An operator the two operand types have no definition for — a request pairing, say, a date
        // with a number. Rejected rather than surfacing as a fault.
        catch (InvalidOperationException exception)
        {
            throw new ScryValidationException($"'{binary.Op}' is not defined for '{left.Type.Name}' and '{right.Type.Name}': {exception.Message}");
        }
    }

    UnaryExpression BuildUnary(UnaryNode unary, Expression row)
    {
        var operand = Build(unary.Operand, row, unary.Op == UnaryOp.Not ? typeof(bool) : null);
        return unary.Op switch
        {
            UnaryOp.Not => Expression.Not(operand),
            UnaryOp.Negate => Expression.Negate(operand),
            _ => throw new ScryValidationException($"Unsupported unary operator '{unary.Op}'.")
        };
    }

    /// <summary>
    /// Rebinds a function call. The wire names a function, never a method: which CLR member that
    /// resolves to is decided here from the target's real type. A function the target type does not
    /// support (a date part on a number, say) is a rejected query rather than a fault, which is what
    /// the type mismatches surfacing as <see cref="ArgumentException"/> below are translated into.
    /// </summary>
    Expression BuildCall(CallNode call, Expression row)
    {
        var target = Build(call.Target, row, null);

        try
        {
            return call.Function switch
            {
                KnownFunction.StringContains => Expression.Call(target, stringContains, StringArgument(call, 0, row)),
                KnownFunction.StringStartsWith => Expression.Call(target, stringStartsWith, StringArgument(call, 0, row)),
                KnownFunction.StringEndsWith => Expression.Call(target, stringEndsWith, StringArgument(call, 0, row)),
                KnownFunction.StringToLower => Expression.Call(target, stringToLower),
                KnownFunction.StringToUpper => Expression.Call(target, stringToUpper),
                KnownFunction.StringIsNullOrEmpty => Expression.Call(stringIsNullOrEmpty, target),
                KnownFunction.StringIsNullOrWhiteSpace => Expression.Call(stringIsNullOrWhiteSpace, target),
                KnownFunction.StringLength => Expression.Property(target, "Length"),
                KnownFunction.StringTrim => Expression.Call(target, stringTrim),
                KnownFunction.StringTrimStart => Expression.Call(target, stringTrimStart),
                KnownFunction.StringTrimEnd => Expression.Call(target, stringTrimEnd),
                KnownFunction.StringSubstring => BuildSubstring(call, target, row),
                KnownFunction.StringIndexOf => Expression.Call(target, stringIndexOf, StringArgument(call, 0, row)),
                KnownFunction.StringReplace => Expression.Call(target, stringReplace, StringArgument(call, 0, row), StringArgument(call, 1, row)),

                KnownFunction.DateYear => TemporalProperty(target, "Year"),
                KnownFunction.DateMonth => TemporalProperty(target, "Month"),
                KnownFunction.DateDay => TemporalProperty(target, "Day"),
                KnownFunction.DateHour => TemporalProperty(target, "Hour"),
                KnownFunction.DateMinute => TemporalProperty(target, "Minute"),
                KnownFunction.DateSecond => TemporalProperty(target, "Second"),
                KnownFunction.DateMillisecond => TemporalProperty(target, "Millisecond"),
                KnownFunction.DateDayOfYear => TemporalProperty(target, "DayOfYear"),
                KnownFunction.DateDate => TemporalProperty(target, "Date"),
                KnownFunction.DateAddYears => TemporalAdd(call, target, "AddYears", row),
                KnownFunction.DateAddMonths => TemporalAdd(call, target, "AddMonths", row),
                KnownFunction.DateAddDays => TemporalAdd(call, target, "AddDays", row),
                KnownFunction.DateAddHours => TemporalAdd(call, target, "AddHours", row),
                KnownFunction.DateAddMinutes => TemporalAdd(call, target, "AddMinutes", row),
                KnownFunction.DateAddSeconds => TemporalAdd(call, target, "AddSeconds", row),

                KnownFunction.MathAbs => MathCall("Abs", target),
                KnownFunction.MathCeiling => MathCall("Ceiling", target),
                KnownFunction.MathFloor => MathCall("Floor", target),
                KnownFunction.MathRound => BuildRound(call, target, row),
                KnownFunction.MathTruncate => MathCall("Truncate", target),

                // Sqrt and Pow are defined over double alone, so a decimal or integer member is widened
                // to reach them — which is also the type the provider computes them in.
                KnownFunction.MathSqrt => Expression.Call(mathSqrt, ConvertTo(NonNullable(target), typeof(double))),
                KnownFunction.MathPow => Expression.Call(
                    mathPow,
                    ConvertTo(NonNullable(target), typeof(double)),
                    ConvertTo(NonNullable(Build(call.Arguments[0], row, typeof(double))), typeof(double))),

                KnownFunction.In => BuildIn(call, target),

                _ => throw new ScryValidationException($"Unsupported function '{call.Function}'.")
            };
        }
        catch (ArgumentException exception)
        {
            throw new ScryValidationException(
                $"Function '{call.Function}' cannot be applied to '{target.Type.Name}': {exception.Message}");
        }
    }

    Expression StringArgument(CallNode call, int index, Expression row) =>
        Build(call.Arguments[index], row, typeof(string));

    // A date part reads off the value, so an optional member is unwrapped first. Under EF that unwrap
    // is part of the translated SQL expression and a null row simply yields null.
    static Expression TemporalProperty(Expression target, string name)
    {
        var value = Nullable.GetUnderlyingType(target.Type) is null
            ? target
            : Expression.Property(target, "Value");

        var property = value.Type.GetProperty(name) ??
                       throw new ScryValidationException($"'{value.Type.Name}' has no '{name}'.");
        return Expression.Property(value, property);
    }

    // AddDays and friends take an int on DateOnly and a double on DateTime, so the argument is built
    // against whatever the resolved overload actually declares.
    Expression TemporalAdd(CallNode call, Expression target, string name, Expression row)
    {
        var value = Nullable.GetUnderlyingType(target.Type) is null
            ? target
            : Expression.Property(target, "Value");

        var method = value.Type.GetMethods()
                         .FirstOrDefault(_ => _.Name == name && _.GetParameters().Length == 1) ??
                     throw new ScryValidationException($"'{value.Type.Name}' has no '{name}'.");

        var argument = Build(call.Arguments[0], row, method.GetParameters()[0].ParameterType);
        return Expression.Call(value, method, ConvertTo(argument, method.GetParameters()[0].ParameterType));
    }

    /// <summary>
    /// Reads the value out of an optional member. A function is defined over the value, not over its
    /// nullability; under EF the unwrap is part of the translated expression, so a null row simply
    /// yields null rather than faulting.
    /// </summary>
    static Expression NonNullable(Expression target) =>
        Nullable.GetUnderlyingType(target.Type) is null
            ? target
            : Expression.Property(target, "Value");

    static Expression MathCall(string name, Expression target)
    {
        var value = NonNullable(target);

        var method = typeof(Math).GetMethod(name, [value.Type]) ??
                     throw new ScryValidationException($"Math.{name} is not defined for '{value.Type.Name}'.");
        return Expression.Call(method, value);
    }

    Expression BuildRound(CallNode call, Expression target, Expression row)
    {
        if (call.Arguments.Count == 0)
        {
            return MathCall("Round", target);
        }

        var value = Nullable.GetUnderlyingType(target.Type) is null
            ? target
            : Expression.Property(target, "Value");
        var digits = ConvertTo(Build(call.Arguments[0], row, typeof(int)), typeof(int));

        var method = typeof(Math).GetMethod("Round", [value.Type, typeof(int)]) ??
                     throw new ScryValidationException($"Math.Round is not defined for '{value.Type.Name}'.");
        return Expression.Call(method, value, digits);
    }

    Expression BuildSubstring(CallNode call, Expression target, Expression row)
    {
        var start = ConvertTo(Build(call.Arguments[0], row, typeof(int)), typeof(int));
        if (call.Arguments.Count == 1)
        {
            return Expression.Call(target, stringSubstring, start);
        }

        var length = ConvertTo(Build(call.Arguments[1], row, typeof(int)), typeof(int));
        return Expression.Call(target, stringSubstringWithLength, start, length);
    }

    /// <summary>
    /// Rebinds set membership onto <c>Enumerable.Contains</c> over a typed array of the client's
    /// values, which EF translates to a SQL <c>IN</c>. The array's element type comes from the member
    /// being tested, so every value is parsed into the server's own type — the wire's type tags never
    /// decide it.
    /// </summary>
    Expression BuildIn(CallNode call, Expression target)
    {
        var elementType = target.Type;
        var values = Array.CreateInstance(elementType, call.Arguments.Count);
        var underlying = Nullable.GetUnderlyingType(elementType) ?? elementType;

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var constant = (ConstNode)call.Arguments[i];
            if (constant.Value is not null &&
                constant.Tag != ClrTypeTag.Null)
            {
                values.SetValue(ParseValue(constant.Value, underlying), i);
            }
        }

        return Expression.Call(
            enumerableContains.MakeGenericMethod(elementType),
            Expression.Constant(values, typeof(IEnumerable<>).MakeGenericType(elementType)),
            target);
    }

    /// <summary>
    /// Widens two numeric operands to a common type. C# inserts this conversion itself — <c>decimal /
    /// int</c> compiles because the <c>int</c> is promoted — but the wire records the operator and
    /// never the conversion, so it has to be reconstructed from the operand types. Operands that are
    /// not both numeric are left alone: enums, strings and dates have their own comparison rules.
    /// </summary>
    static void PromoteNumeric(ref Expression left, ref Expression right)
    {
        if (left.Type == right.Type)
        {
            return;
        }

        var leftValue = Nullable.GetUnderlyingType(left.Type) ?? left.Type;
        var rightValue = Nullable.GetUnderlyingType(right.Type) ?? right.Type;
        if (leftValue == rightValue ||
            NumericTarget(leftValue, rightValue) is not { } target)
        {
            return;
        }

        // An operand that can be null keeps the result nullable, exactly as it would in C#.
        if (Nullable.GetUnderlyingType(left.Type) is not null ||
            Nullable.GetUnderlyingType(right.Type) is not null)
        {
            target = typeof(Nullable<>).MakeGenericType(target);
        }

        left = ConvertTo(left, target);
        right = ConvertTo(right, target);
    }

    // Widest wins, in the order C# itself prefers.
    static Type? NumericTarget(Type left, Type right)
    {
        if (!IsNumeric(left) ||
            !IsNumeric(right))
        {
            return null;
        }

        foreach (var candidate in numericWidths)
        {
            if (left == candidate ||
                right == candidate)
            {
                return candidate;
            }
        }

        return typeof(int);
    }

    static readonly Type[] numericWidths =
    [
        typeof(decimal), typeof(double), typeof(float), typeof(ulong), typeof(long), typeof(uint), typeof(int)
    ];

    static bool IsNumeric(Type type) =>
        !type.IsEnum &&
        Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or
            TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    static Expression ConvertTo(Expression expression, Type target) =>
        expression.Type == target ? expression : Expression.Convert(expression, target);

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
    static readonly MethodInfo stringIsNullOrWhiteSpace = StringMethod("IsNullOrWhiteSpace", typeof(string));
    static readonly MethodInfo stringTrim = StringMethod("Trim");
    static readonly MethodInfo stringTrimStart = StringMethod("TrimStart");
    static readonly MethodInfo stringTrimEnd = StringMethod("TrimEnd");
    static readonly MethodInfo stringSubstring = StringMethod("Substring", typeof(int));
    static readonly MethodInfo stringSubstringWithLength = StringMethod("Substring", typeof(int), typeof(int));
    static readonly MethodInfo stringIndexOf = StringMethod("IndexOf", typeof(string));
    static readonly MethodInfo stringReplace = StringMethod("Replace", typeof(string), typeof(string));
    static readonly MethodInfo stringConcat = StringMethod("Concat", typeof(string), typeof(string));

    static readonly MethodInfo mathSqrt = typeof(Math).GetMethod("Sqrt", [typeof(double)])!;
    static readonly MethodInfo mathPow = typeof(Math).GetMethod("Pow", [typeof(double), typeof(double)])!;

    // The generic Contains<TSource>(source, value) definition, closed per member type by BuildIn.
    static readonly MethodInfo enumerableContains = typeof(Enumerable).GetMethods()
        .Single(_ =>
            _ is { Name: "Contains", IsGenericMethodDefinition: true } &&
            _.GetParameters().Length == 2);

    // The collection-subquery methods, closed per element type. Enumerable rather than Queryable: a
    // navigation collection is an IEnumerable in the expression tree, which is the shape EF translates
    // into a correlated subquery.
    static readonly MethodInfo enumerableAny = EnumerableMethod("Any", 1);
    static readonly MethodInfo enumerableAll = EnumerableMethod("All", 2);

    // Where has an indexed overload too; the wanted one takes Func<TSource, bool> — two generic
    // arguments — rather than Func<TSource, int, bool>.
    static readonly MethodInfo enumerableWhere = typeof(Enumerable).GetMethods()
        .Single(_ =>
            _ is { Name: "Where", IsGenericMethodDefinition: true } &&
            _.GetParameters().Length == 2 &&
            _.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);

    static MethodInfo EnumerableMethod(string name, int parameters) =>
        typeof(Enumerable).GetMethods()
            .Single(_ =>
                _.Name == name &&
                _.IsGenericMethodDefinition &&
                _.GetGenericArguments().Length == 1 &&
                _.GetParameters().Length == parameters &&
                _.GetParameters()[0].ParameterType.IsGenericType &&
                _.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

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

        // C# promotes char to int to compare it, so the literal reaches the wire as a code point about
        // as often as it does as the character itself. Both spell the same value.
        if (underlying == typeof(char))
        {
            if (value.Length == 1)
            {
                return value[0];
            }

            if (int.TryParse(value, NumberStyles.Integer, culture, out var code) &&
                code is >= char.MinValue and <= char.MaxValue)
            {
                return (char)code;
            }

            throw new ScryValidationException($"'{value}' is not a character.");
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
