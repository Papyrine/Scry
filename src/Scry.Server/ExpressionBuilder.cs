/// <summary>
/// Rebinds a validated query AST onto real CLR <see cref="Expression"/> trees over the server's
/// entity types. This is the only place CLR types are introduced — always from the schema, never
/// from the wire.
/// </summary>
/// <remarks>
/// <c>sources</c> resolves a named source, already policy-filtered, for a node that reads one — a
/// membership test against another source. Null where no such node can occur, which makes the
/// omission a rejection rather than an unfiltered read.
/// </remarks>
sealed class ExpressionBuilder(Schema schema, ScryOptions options, Func<string, IQueryable>? sources = null)
{
    /// <summary>Builds a predicate lambda <c>TElement =&gt; bool</c>.</summary>
    public LambdaExpression BuildPredicate(Node predicate, Type type)
    {
        var parameter = Expression.Parameter(type, "e");
        var body = Build(predicate, parameter, typeof(bool));
        return Expression.Lambda(body, parameter);
    }

    /// <summary>
    /// Builds the selector a flatten applies: <c>TElement =&gt; IEnumerable&lt;TChild&gt;</c>, reading the
    /// named collection navigation off the row.
    /// </summary>
    public (LambdaExpression Selector, Type Element) BuildCollectionSelector(IReadOnlyList<string> path, Type type)
    {
        var parameter = Expression.Parameter(type, "e");
        var collection = BuildMemberAccess(parameter, path);
        var element = Schema.CollectionElement(collection.Type) ??
                      throw new ScryValidationException($"'{string.Join('.', path)}' is not a collection.");

        // The lambda is typed to return IEnumerable<T> so the Queryable.SelectMany overload binds
        // regardless of how the navigation is declared. The delegate type carries that, rather than a
        // Convert around the body — a conversion node stops EF expanding the navigation at all.
        var selector = Expression.Lambda(
            typeof(Func<,>).MakeGenericType(type, typeof(IEnumerable<>).MakeGenericType(element)),
            collection,
            parameter);
        return (selector, element);
    }

    /// <summary>Builds a key selector lambda <c>TElement =&gt; TKey</c>.</summary>
    public LambdaExpression BuildKeySelector(Node key, Type type)
    {
        var parameter = Expression.Parameter(type, "e");
        var body = Build(key, parameter, null);
        return Expression.Lambda(body, parameter);
    }

    // The keys the current query grouped by, in order, once more than one — what a member read inside
    // a grouped projection or a HAVING predicate is resolved against. Null while a single key is in
    // scope, where every such read means the whole key and no matching is needed.
    IReadOnlyList<Node>? compositeKeys;

    /// <summary>
    /// Builds the key selector for a <c>GroupBy</c>. Several keys are projected into a
    /// <see cref="DistinctRow"/> carrying its member mappings, which is what lets the provider
    /// decompose the key into columns and group on them; a bare shaped row has no equality to group by.
    /// </summary>
    public LambdaExpression BuildGroupKeySelector(IReadOnlyList<Node> keys, Type type)
    {
        if (keys.Count == 1)
        {
            compositeKeys = null;
            return BuildKeySelector(keys[0], type);
        }

        var parameter = Expression.Parameter(type, "e");
        var values = keys.Select(_ => Build(_, parameter, null)).ToList();
        var row = DistinctRow.ByArity[values.Count - 1].MakeGenericType([..values.Select(_ => _.Type)]);
        var constructor = row.GetConstructors().Single();
        var members = constructor.GetParameters()
            .Select(MemberInfo (_) => row.GetProperty(_.Name!)!)
            .ToArray();

        compositeKeys = keys;
        return Expression.Lambda(Expression.New(constructor, values, members), parameter);
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

        // A group join pairs each outer row with the matching inner rows, so its result selector takes
        // the group rather than a row — and the only thing the projection may do with it is aggregate.
        var grouped = kind == JoinKind.Group;
        var inner = Expression.Parameter(
            grouped ? typeof(IEnumerable<>).MakeGenericType(innerType) : innerType,
            "i");
        var leaves = new List<Expression>(members.Count);
        var shape = new List<IReadOnlyList<string>>(members.Count);

        foreach (var member in members)
        {
            if (member.Aggregate is { } aggregate)
            {
                leaves.Add(BuildAggregate(aggregate, inner, innerType));
                shape.Add([member.Name]);
                continue;
            }

            Expression root = member.Side == JoinSide.Outer ? outer : inner;
            var leaf = BuildMemberAccess(root, member.Path);

            // A side the join can leave unmatched yields SQL NULL, so a non-nullable value read from
            // it is widened; without that the shaper faults materializing the null.
            var optional = member.Side == JoinSide.Inner
                ? kind == JoinKind.Left
                : kind == JoinKind.Right;

            if (optional &&
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

        // A composite key builds one DistinctRow per side — the typed row a composite GroupBy key
        // already uses — whose member-wise equality is what the provider decomposes into per-part
        // comparisons. Each pair of parts is reconciled exactly as a single key would be.
        if (outerKey is CompositeKeyNode outerParts &&
            innerKey is CompositeKeyNode innerParts)
        {
            var outerValues = new List<Expression>(outerParts.Parts.Count);
            var innerValues = new List<Expression>(innerParts.Parts.Count);
            for (var index = 0; index < outerParts.Parts.Count; index++)
            {
                var outerValue = Build(outerParts.Parts[index], outerParameter, null);
                var innerValue = Build(innerParts.Parts[index], innerParameter, null);
                Coerce(ref outerValue, ref innerValue);
                if (outerValue.Type != innerValue.Type)
                {
                    throw new ScryValidationException(
                        $"Join keys must have the same type, but part {index + 1} was '{outerValue.Type.Name}' and '{innerValue.Type.Name}'.");
                }

                outerValues.Add(outerValue);
                innerValues.Add(innerValue);
            }

            var row = DistinctRow.ByArity[outerValues.Count - 1].MakeGenericType([..outerValues.Select(_ => _.Type)]);
            var constructor = row.GetConstructors().Single();
            var members = constructor.GetParameters()
                .Select(MemberInfo (_) => row.GetProperty(_.Name!)!)
                .ToArray();

            return (
                Expression.Lambda(Expression.New(constructor, outerValues, members), outerParameter),
                Expression.Lambda(Expression.New(constructor, innerValues, members), innerParameter));
        }

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
            .Select(MemberInfo (_) => row.GetProperty(_.Name!)!)
            .ToArray();

        return (Expression.Lambda(Expression.New(constructor, leaves, members), parameter), shape);
    }

    /// <summary>
    /// Reads a <see cref="DistinctRow"/>'s values back out, in projection order. This runs per row,
    /// so the property reads are compiled once per closed row type — bounded by the
    /// <see cref="DistinctRow"/> arities the schema can produce — rather than reflected per value.
    /// </summary>
    public static object[] ReadDistinctRow(object row, int count) =>
        distinctReaders.GetOrAdd(row.GetType(), DistinctReader)(row);

    static readonly ConcurrentDictionary<Type, Func<object, object[]>> distinctReaders = new();

    static Func<object, object[]> DistinctReader(Type type)
    {
        var row = Expression.Parameter(typeof(object), "row");
        var typed = Expression.Convert(row, type);

        var arity = 0;
        while (type.GetProperty($"Value{arity + 1}") is not null)
        {
            arity++;
        }

        var values = new Expression[arity];
        for (var i = 0; i < arity; i++)
        {
            values[i] = Expression.Convert(Expression.Property(typed, $"Value{i + 1}"), typeof(object));
        }

        return Expression.Lambda<Func<object, object[]>>(Expression.NewArrayInit(typeof(object), values), row)
            .Compile();
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
        var leaves = new List<Expression>(projection.Members.Count);
        var shape = new List<IReadOnlyList<string>>(projection.Members.Count);

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
            GroupKeyNode key when IsGrouping(row.Type) => BuildGroupKeyAt(key.Index, row),
            MemberNode member when IsGrouping(row.Type) => BuildGroupKey(member, row),
            AggregateNode aggregate when IsGrouping(row.Type) =>
                BuildAggregate(aggregate, (ParameterExpression)row, row.Type.GetGenericArguments()[1]),
            MemberNode member => BuildMemberAccess(row, member.Path),
            ElementNode => BuildElement(row),
            ConstNode constant => BuildConstant(constant, expected),
            BinaryNode binary => BuildBinary(binary, row),
            UnaryNode unary => BuildUnary(unary, row),
            CallNode call => BuildCall(call, row),
            ConditionalNode conditional => BuildConditional(conditional, row),
            SubqueryNode subquery => BuildSubquery(subquery, row),
            CollateNode collate => BuildCollate(collate, row),
            InSourceNode inSource => BuildInSource(inSource, row),
            _ => throw new ScryValidationException($"Unsupported expression '{node.GetType().Name}'.")
        };

    /// <summary>
    /// Reads the row itself, which is what an element node names inside a subquery over a collection of
    /// values. No CLR type is introduced — the expression is the parameter the caller already built.
    /// </summary>
    /// <remarks>
    /// The validator has already refused an element node anywhere the row is not a value; the check is
    /// repeated because this is the one node whose meaning depends entirely on what it is read against,
    /// and reaching an entity here would silently name the whole row.
    /// </remarks>
    static Expression BuildElement(Expression row)
    {
        if (!Schema.IsScalar(row.Type))
        {
            throw new ScryValidationException("An element can only be read inside a subquery over a collection of values.");
        }

        return row;
    }

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

    /// <summary>
    /// Reads a string under a configured collation, which is what makes the comparisons wrapping it
    /// case-sensitive or not. The collation name comes from server options and is the one value here
    /// that reaches the SQL as text rather than as a parameter — which is exactly why a request names
    /// only the sensitivity it wants.
    /// </summary>
    Expression BuildCollate(CollateNode collate, Expression row)
    {
        var target = Build(collate.Target, row, typeof(string));
        var collation = collate.Match switch
        {
            StringMatch.CaseSensitive => options.CaseSensitiveCollation,
            StringMatch.CaseInsensitive => options.CaseInsensitiveCollation,
            _ => null
        };

        if (collation is null)
        {
            throw new ScryValidationException($"This server has no {collate.Match} collation configured.");
        }

        if (collateMethod is null)
        {
            throw new ScryValidationException(
                "Collation requires a relational provider; this model is not backed by one.");
        }

        return Expression.Call(
            collateMethod.MakeGenericMethod(target.Type),
            Expression.Constant(EF.Functions),
            target,
            Expression.Constant(collation));
    }

    // Resolved by name rather than referenced: a collation is a relational concept, and Scry.Server
    // itself depends only on EF Core proper, so a non-relational model simply cannot offer it.
    static readonly MethodInfo? collateMethod = Type
        .GetType("Microsoft.EntityFrameworkCore.RelationalDbFunctionsExtensions, Microsoft.EntityFrameworkCore.Relational")
        ?.GetMethod("Collate");

    /// <summary>
    /// Rebinds membership of a set drawn from another source onto <c>Contains</c> over that source's
    /// query, which EF translates to <c>IN (SELECT …)</c>. The source arrives already policy-filtered,
    /// so the set can only hold rows the caller could have queried directly.
    /// </summary>
    Expression BuildInSource(InSourceNode inSource, Expression row)
    {
        if (sources is null)
        {
            throw new ScryValidationException("A membership test against another source is not available here.");
        }

        var inner = sources(inSource.Root);
        var element = inner.ElementType;

        if (inSource.Predicate is { } predicate)
        {
            inner = inner.Provider.CreateQuery(
                Expression.Call(
                    typeof(Queryable),
                    "Where",
                    [element],
                    inner.Expression,
                    Expression.Quote(ElementLambda(predicate, element, typeof(bool)))));
        }

        var value = Build(inSource.Value, row, null);
        var selector = ElementLambda(inSource.Selector, element, null);
        var candidates = inner.Provider.CreateQuery(
            Expression.Call(
                typeof(Queryable),
                "Select",
                [element, selector.ReturnType],
                inner.Expression,
                Expression.Quote(selector)));

        // The tested value and the candidates must agree on type for Contains to bind.
        var candidateValues = candidates.Expression;
        if (selector.ReturnType != value.Type)
        {
            var target = Nullable.GetUnderlyingType(selector.ReturnType) == value.Type
                ? selector.ReturnType
                : value.Type;
            if (target != value.Type)
            {
                value = Expression.Convert(value, target);
            }
            else
            {
                throw new ScryValidationException(
                    $"A membership test compares '{value.Type.Name}' against '{selector.ReturnType.Name}'.");
            }
        }

        return Expression.Call(
            queryableContains.MakeGenericMethod(value.Type),
            candidateValues,
            value);
    }

    static readonly MethodInfo queryableContains = typeof(Queryable).GetMethods()
        .Single(_ =>
            _ is { Name: "Contains", IsGenericMethodDefinition: true } &&
            _.GetParameters().Length == 2);

    LambdaExpression ElementLambda(Node node, Type element, Type? expected)
    {
        var parameter = Expression.Parameter(element, "x");
        return Expression.Lambda(Build(node, parameter, expected), parameter);
    }

    // A member read against a group means the key. With one key that is the whole key; with several it
    // names one of them, matched back to the position it was grouped at.
    Expression BuildGroupKey(MemberNode member, Expression row)
    {
        if (compositeKeys is null)
        {
            return BuildGroupKeyAt(0, row);
        }

        for (var i = 0; i < compositeKeys.Count; i++)
        {
            if (compositeKeys[i] is MemberNode candidate &&
                candidate.Path.SequenceEqual(member.Path))
            {
                return BuildGroupKeyAt(i, row);
            }
        }

        throw new ScryValidationException(
            $"'{string.Join(".", member.Path)}' is not one of the query's group keys.");
    }

    // The same read by position rather than by path, which is how a computed key — one with no path to
    // match — names itself.
    Expression BuildGroupKeyAt(int index, Expression row)
    {
        var key = Expression.Property(row, "Key");
        if (compositeKeys is null)
        {
            if (index != 0)
            {
                throw new ScryValidationException($"Group key {index} was read, but the query grouped by one.");
            }

            return key;
        }

        if (index < 0 ||
            index >= compositeKeys.Count)
        {
            throw new ScryValidationException(
                $"Group key {index} was read, but the query grouped by {compositeKeys.Count}.");
        }

        return Expression.Property(key, $"Value{index + 1}");
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

        // Arithmetic promotes its operands to a common type; a comparison instead reads its constant at
        // the other side's type, which is what makes '_.Amount > 100' compare decimals. Inferring the
        // type that way for arithmetic too would compute in the member's type rather than the one C#
        // would have: '_.Quantity / 2d' over an integer member would answer 0.
        var arithmetic = binary.Op is
            BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide or BinaryOp.Modulo;

        Expression left;
        Expression right;
        if (arithmetic)
        {
            if (binary is {Left: ConstNode, Right: not ConstNode})
            {
                right = Build(binary.Right, row, null);
                left = Build(binary.Left, row, Widest(right.Type, binary.Left));
            }
            else
            {
                left = Build(binary.Left, row, null);
                right = Build(binary.Right, row, Widest(left.Type, binary.Right));
            }

            Promote(ref left, ref right);
        }
        else if (binary is {Left: ConstNode, Right: not ConstNode})
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
                KnownFunction.DateDayOfWeek => BuildDayOfWeek(target),
                KnownFunction.DateDate => TemporalProperty(target, "Date"),
                KnownFunction.DateAddYears => TemporalAdd(call, target, "AddYears", row),
                KnownFunction.DateAddMonths => TemporalAdd(call, target, "AddMonths", row),
                KnownFunction.DateAddDays => TemporalAdd(call, target, "AddDays", row),
                KnownFunction.DateAddHours => TemporalAdd(call, target, "AddHours", row),
                KnownFunction.DateAddMinutes => TemporalAdd(call, target, "AddMinutes", row),
                KnownFunction.DateAddSeconds => TemporalAdd(call, target, "AddSeconds", row),
                KnownFunction.DateAddMilliseconds => TemporalAdd(call, target, "AddMilliseconds", row),

                KnownFunction.StringConcat => BuildConcat(target, Build(call.Arguments[0], row, null)),
                KnownFunction.StringFrom => BuildStringFrom(target),
                KnownFunction.EnumHasFlag => BuildHasFlag(call, target, row),

                KnownFunction.Int32From => BuildFromText(call.Function, target, convertToInt32),
                KnownFunction.Int64From => BuildFromText(call.Function, target, convertToInt64),
                KnownFunction.DecimalFrom => BuildFromText(call.Function, target, convertToDecimal),
                KnownFunction.DoubleFrom => BuildFromText(call.Function, target, convertToDouble),
                KnownFunction.BooleanFrom => BuildFromText(call.Function, target, convertToBoolean),
                KnownFunction.ByteFrom => BuildFromText(call.Function, target, convertToByte),
                KnownFunction.Int16From => BuildFromText(call.Function, target, convertToInt16),
                KnownFunction.SingleFrom => BuildFromText(call.Function, target, singleParse),

                KnownFunction.MathAbs => MathCall("Abs", target),
                KnownFunction.MathCeiling => MathCall("Ceiling", target),
                KnownFunction.MathFloor => MathCall("Floor", target),
                KnownFunction.MathRound => BuildRound(call, target, row),
                KnownFunction.MathTruncate => MathCall("Truncate", target),

                // These are defined over double alone, so a decimal or integer member is widened to
                // reach them — which is also the type the provider computes them in.
                KnownFunction.MathSign => BuildSign(target),
                KnownFunction.MathSqrt => Double1("Sqrt", target),
                KnownFunction.MathExp => Double1("Exp", target),
                KnownFunction.MathLog10 => Double1("Log10", target),
                KnownFunction.MathSin => Double1("Sin", target),
                KnownFunction.MathCos => Double1("Cos", target),
                KnownFunction.MathTan => Double1("Tan", target),
                KnownFunction.MathAsin => Double1("Asin", target),
                KnownFunction.MathAcos => Double1("Acos", target),
                KnownFunction.MathAtan => Double1("Atan", target),

                KnownFunction.MathPow => Double2("Pow", target, call, row),
                KnownFunction.MathAtan2 => Double2("Atan2", target, call, row),

                KnownFunction.MathMax => BuildMinMax(call, target, row, max: true),
                KnownFunction.MathMin => BuildMinMax(call, target, row, max: false),
                KnownFunction.CompareTo => BuildCompareTo(call, target, row),

                // With no argument this is the natural logarithm; with one it is the logarithm to that
                // base, which is a second double operand exactly like Pow's exponent.
                KnownFunction.MathLog => call.Arguments.Count == 0
                    ? Double1("Log", target)
                    : Double2("Log", target, call, row),

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
    /// <summary>
    /// Builds the day of the week, numbered as <see cref="DayOfWeek"/> is — 0 for Sunday.
    /// </summary>
    /// <remarks>
    /// SQL Server has no deterministic day-of-week function: <c>DATEPART(weekday, …)</c> reads
    /// <c>@@DATEFIRST</c>, a session setting, so the same row answers differently on two connections.
    /// That is why EF refuses to translate <see cref="DateTime.DayOfWeek"/> at all rather than
    /// translating it wrongly. Counting whole days from a fixed Monday and taking the remainder depends
    /// on nothing but the date, translates in full, and numbers the days exactly as .NET does. The wire
    /// carries only the intent; the server owns the SQL, the same way it owns a collation.
    /// </remarks>
    static Expression BuildDayOfWeek(Expression target)
    {
        var value = NonNullable(target);

        if (DateDiffDay(value.Type) is not { } dateDiffDay)
        {
            throw new ScryValidationException(
                "DayOfWeek is only supported on SQL Server, whose provider supplies the deterministic date arithmetic it is built from. No other provider has been verified to answer it the same way, so it is refused here rather than translated into something that reads a session setting.");
        }

        var epoch = Expression.Constant(Epoch(value.Type), value.Type);
        var days = Expression.Call(dateDiffDay, Expression.Constant(EF.Functions, typeof(DbFunctions)), epoch, value);

        // The epoch is a Monday, so +1 lands Sunday on zero. The second remainder is for dates before
        // the epoch, where the day count — and so the first remainder — is negative.
        var shifted = Expression.Modulo(Expression.Add(days, Expression.Constant(1)), Expression.Constant(7));
        return Expression.Modulo(Expression.Add(shifted, Expression.Constant(7)), Expression.Constant(7));
    }

    // Boxed per branch on purpose: DateTime converts implicitly to DateTimeOffset, so an unboxed
    // conditional would silently hand back the wrong type for a DateTime member.
    static object Epoch(Type type)
    {
        if (type == typeof(Date))
        {
            return new Date(1900, 1, 1);
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(new DateTime(1900, 1, 1), TimeSpan.Zero);
        }

        return new DateTime(1900, 1, 1);
    }

    // Resolved by name rather than referenced: Scry.Server depends on EF Core alone, not on any one
    // provider's package. Null when the SQL Server provider is not part of the application at all.
    static readonly Type? sqlServerFunctions = Type.GetType(
        "Microsoft.EntityFrameworkCore.SqlServerDbFunctionsExtensions, Microsoft.EntityFrameworkCore.SqlServer");

    static readonly ConcurrentDictionary<Type, MethodInfo?> dateDiffDays = new();

    static MethodInfo? DateDiffDay(Type type) =>
        dateDiffDays.GetOrAdd(
            type,
            key => sqlServerFunctions?.GetMethod("DateDiffDay", [typeof(DbFunctions), key, key]));

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

    /// <summary>
    /// Builds the sign of a value as -1, 0, or 1, from comparisons rather than from SQL's own
    /// <c>SIGN</c>.
    /// </summary>
    /// <remarks>
    /// The provider does translate <see cref="Math.Sign(decimal)"/>, but SQL's <c>SIGN</c> returns its
    /// argument's type while the CLR method returns an <see cref="int"/>, so the result cannot be read
    /// back: the query succeeds in a predicate, where nothing is materialized, and faults in a
    /// projection. Two comparisons and a conditional say the same thing, are translated by any
    /// relational provider, and yield an int because that is what they are built from.
    /// </remarks>
    /// <summary>
    /// Builds <c>target.HasFlag(flag)</c> over a [Flags] enum member. The CLR call is handed to the
    /// provider as written — EF owns its translation, <c>(x &amp; flag) = flag</c> in SQL — and runs
    /// as itself over an in-memory source. The flag constant is parsed at the member's own enum type,
    /// so a name outside it is a rejected query.
    /// </summary>
    Expression BuildHasFlag(CallNode call, Expression target, Expression row)
    {
        var value = NonNullable(target);
        if (!value.Type.IsEnum)
        {
            throw new ScryValidationException($"HasFlag is not supported over '{value.Type.Name}'.");
        }

        var flag = Build(call.Arguments[0], row, value.Type);
        return Expression.Call(value, enumHasFlag, Expression.Convert(flag, typeof(Enum)));
    }

    static Expression BuildSign(Expression target)
    {
        var value = NonNullable(target);
        if (Rank(value.Type) is null)
        {
            throw new ScryValidationException($"Sign is not supported over '{value.Type.Name}'.");
        }

        var zero = Expression.Constant(Activator.CreateInstance(value.Type), value.Type);
        var sign = Expression.Condition(
            Expression.GreaterThan(value, zero),
            Expression.Constant(1),
            Expression.Condition(
                Expression.LessThan(value, zero),
                Expression.Constant(-1),
                Expression.Constant(0)));

        if (Nullable.GetUnderlyingType(target.Type) is null)
        {
            return sign;
        }

        // A comparison against null is neither greater nor less, so an unguarded chain would answer
        // zero — the sign of a value that is not there. Null in, null out instead.
        return Expression.Condition(
            Expression.Equal(target, Expression.Constant(null, target.Type)),
            Expression.Constant(null, typeof(int?)),
            Expression.Convert(sign, typeof(int?)));
    }

    /// <summary>
    /// The greater or lesser of the target and the argument (<c>Math.Max</c> / <c>Math.Min</c>).
    /// </summary>
    /// <remarks>
    /// Composed from a comparison rather than handed to the provider: SQL's GREATEST and LEAST exist
    /// only from SQL Server 2022, and a conditional says the same thing anywhere EF translates at
    /// all. A null operand keeps the answer null — GREATEST would skip it and answer with the other
    /// operand, which is the greater of one value rather than of two.
    /// </remarks>
    Expression BuildMinMax(CallNode call, Expression target, Expression row, bool max)
    {
        var left = target;
        var right = Build(call.Arguments[0], row, Nullable.GetUnderlyingType(target.Type) ?? target.Type);
        Promote(ref left, ref right);

        // Enums are excluded by hand: their type code reports the underlying number's, and the
        // comparison below would fault over them rather than reject.
        var leftValue = Nullable.GetUnderlyingType(left.Type) ?? left.Type;
        var rightValue = Nullable.GetUnderlyingType(right.Type) ?? right.Type;
        if (leftValue.IsEnum ||
            rightValue.IsEnum ||
            Rank(leftValue) is null ||
            Rank(rightValue) is null)
        {
            throw new ScryValidationException(
                $"Math.{(max ? "Max" : "Min")} is not supported over '{leftValue.Name}'.");
        }

        // Promote unifies differing value types but leaves matching ones alone, so two operands can
        // still disagree only in optionality — and the comparison and the answer both need one shape,
        // which has to be the optional one.
        if (left.Type != right.Type)
        {
            var lifted = Nullable.GetUnderlyingType(left.Type) is null ? right.Type : left.Type;
            left = ConvertTo(left, lifted);
            right = ConvertTo(right, lifted);
        }

        var pick = Expression.Condition(
            max ? Expression.GreaterThanOrEqual(left, right) : Expression.LessThanOrEqual(left, right),
            left,
            right);

        if (Nullable.GetUnderlyingType(left.Type) is null)
        {
            return pick;
        }

        // A lifted comparison against null is simply false, so an unguarded conditional would answer
        // the other operand — the greater of one value, not of two. Null in, null out instead.
        return Expression.Condition(
            Expression.OrElse(
                Expression.Equal(left, Expression.Constant(null, left.Type)),
                Expression.Equal(right, Expression.Constant(null, right.Type))),
            Expression.Constant(null, left.Type),
            pick);
    }

    /// <summary>
    /// Three-way comparison: -1, 0, or 1. Emitted as the CLR <c>CompareTo</c> call, whose translation
    /// EF owns — a CASE over the two operands, on any relational provider — and which runs as itself
    /// over an in-memory source. Text compares under the server's collation, exactly as ordering does.
    /// </summary>
    /// <remarks>
    /// A null operand keeps the answer null: a comparison against a value that is not there has no
    /// direction. EF's CASE says the same by falling through, but the guard also spares the in-memory
    /// path a call on a null receiver.
    /// </remarks>
    Expression BuildCompareTo(CallNode call, Expression target, Expression row)
    {
        var argument = Build(call.Arguments[0], row, Nullable.GetUnderlyingType(target.Type) ?? target.Type);
        var left = NonNullable(target);
        var right = NonNullable(argument);
        Promote(ref left, ref right);

        if (!ThreeWayComparable(left.Type) ||
            left.Type != right.Type)
        {
            throw new ScryValidationException($"CompareTo is not supported over '{left.Type.Name}'.");
        }

        var method = left.Type.GetMethod("CompareTo", [left.Type])!;
        Expression compared = Expression.Call(left, method, right);

        // The operands that can be null: an optional member, or text, whose null no static type
        // records.
        var guards = new List<Expression>();
        if (Nullable.GetUnderlyingType(target.Type) is not null ||
            target.Type == typeof(string))
        {
            guards.Add(Expression.Equal(target, Expression.Constant(null, target.Type)));
        }

        if (Nullable.GetUnderlyingType(argument.Type) is not null ||
            argument.Type == typeof(string))
        {
            guards.Add(Expression.Equal(argument, Expression.Constant(null, argument.Type)));
        }

        if (guards.Count == 0)
        {
            return compared;
        }

        return Expression.Condition(
            guards.Aggregate(Expression.OrElse),
            Expression.Constant(null, typeof(int?)),
            Expression.Convert(compared, typeof(int?)));
    }

    // The types the three-way comparison is defined over: numbers, text, and dates. Enums are
    // excluded by hand — their type code reports the underlying number's.
    static bool ThreeWayComparable(Type type) =>
        type == typeof(string) ||
        type == typeof(DateTime) ||
        type == typeof(Date) ||
        type == typeof(Time) ||
        type == typeof(DateTimeOffset) ||
        (!type.IsEnum && Rank(type) is not null);

    // A Math method defined over double alone: the target is widened to reach it.
    static Expression Double1(string name, Expression target) =>
        Expression.Call(DoubleMethod(name, 1), ConvertTo(NonNullable(target), typeof(double)));

    // The same, for the two-operand forms — the second operand is the call's one argument.
    Expression Double2(string name, Expression target, CallNode call, Expression row) =>
        Expression.Call(
            DoubleMethod(name, 2),
            ConvertTo(NonNullable(target), typeof(double)),
            ConvertTo(NonNullable(Build(call.Arguments[0], row, typeof(double))), typeof(double)));

    static readonly ConcurrentDictionary<(string Name, int Arity), MethodInfo> doubleMethods = new();

    static MethodInfo DoubleMethod(string name, int arity) =>
        doubleMethods.GetOrAdd(
            (name, arity),
            key => typeof(Math).GetMethod(key.Name, [..Enumerable.Repeat(typeof(double), key.Arity)])!);

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
    /// decide it. The array reaches the provider the same way a scalar constant does — as a bound
    /// collection parameter, not statement text — so one cached plan serves every list a client
    /// sends, whatever its values.
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
            Parameterization.Parameterize(values, typeof(IEnumerable<>).MakeGenericType(elementType)),
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

    /// <summary>
    /// Reads a value as text. Restricted to the types a provider converts: a value whose text form the
    /// database cannot produce is refused here rather than faulting once the query runs.
    /// </summary>
    /// <remarks>
    /// An enum is the notable exclusion. Its text form is a member name, which lives in the model
    /// rather than in the database — the column holds the underlying value — so converting one in SQL
    /// would yield a number where the client expects a name.
    /// </remarks>
    /// <summary>
    /// Reads text as a value — the inverse of <see cref="BuildStringFrom"/>. Only a string target is
    /// accepted: a numeric member is already a value, and SQL's numeric-to-numeric conversions
    /// truncate where the CLR's round, so carrying those would answer differently per source. Emitted
    /// as the Convert call EF translates to CONVERT; text that does not parse faults at execution,
    /// exactly as it would in memory.
    /// </summary>
    static Expression BuildFromText(KnownFunction function, Expression target, MethodInfo method)
    {
        var value = NonNullable(target);
        if (value.Type != typeof(string))
        {
            throw new ScryValidationException($"'{function}' reads text as a value, and '{value.Type.Name}' is already one.");
        }

        return Expression.Call(method, value);
    }

    static Expression BuildStringFrom(Expression target)
    {
        var value = NonNullable(target);
        if (value.Type == typeof(string))
        {
            return value;
        }

        if (value.Type.IsEnum)
        {
            throw new ScryValidationException(
                "ToString is not supported over an enum: its text is a member name the database does not hold, so the conversion would yield the underlying number instead.");
        }

        if (!convertibleToText.Contains(value.Type))
        {
            throw new ScryValidationException($"ToString is not supported over '{value.Type.Name}'.");
        }

        return Expression.Call(value, value.Type.GetMethod("ToString", Type.EmptyTypes)!);
    }

    // The scalar shapes a relational provider can render as text. Deliberately a list rather than
    // "anything with a ToString": every CLR type has one, and almost none of them mean anything in SQL.
    static readonly HashSet<Type> convertibleToText =
    [
        typeof(bool),
        typeof(char),
        typeof(sbyte),
        typeof(byte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(DateTime),
        typeof(Date),
        typeof(Time),
        typeof(DateTimeOffset),
        typeof(TimeSpan),
        typeof(Guid),
        typeof(byte[])
    ];

    /// <summary>
    /// Joins two operands into a string, reproducing the shape C# compiles <c>+</c> to.
    /// </summary>
    /// <remarks>
    /// A string operand is left alone and only a non-string one is boxed, which is what tells the
    /// provider which side is already text. Converting both instead loses that: the operands become
    /// indistinguishable and the provider reads the whole thing as arithmetic, casting the string to a
    /// number and failing at execution.
    /// </remarks>
    static Expression BuildConcat(Expression left, Expression right)
    {
        if (left.Type == typeof(string) &&
            right.Type == typeof(string))
        {
            return Expression.Call(stringConcat, left, right);
        }

        return Expression.Add(ConcatOperand(left), ConcatOperand(right), stringConcatObjects);
    }

    static Expression ConcatOperand(Expression expression) =>
        expression.Type == typeof(string) ? expression : Expression.Convert(expression, typeof(object));

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
        return Parameterization.Parameterize(parsed, underlying);
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

        // The text aggregate. SQL leaves STRING_AGG's concatenation order unspecified, so the joined
        // values are ordered by themselves — WITHIN GROUP on SQL Server, the same OrderBy in memory —
        // and null values are filtered first, which is what STRING_AGG does on its own and string.Join
        // does not. The answer then reads identically from either source.
        if (aggregate.Function == AggregateFn.Join)
        {
            if (returnType != typeof(string))
            {
                throw new ScryValidationException($"Join aggregates text; '{string.Join('.', member.Path)}' is not text.");
            }

            if (aggregate.Separator is not { } separator)
            {
                throw new ScryValidationException("Join requires a separator.");
            }

            var notNull = Expression.Lambda(
                Expression.NotEqual(selectorBody, Expression.Constant(null, typeof(string))),
                selectorParameter);
            var present = Expression.Call(enumerableWhere.MakeGenericMethod(element), group, notNull);
            var values = Expression.Call(enumerableSelect.MakeGenericMethod(element, typeof(string)), present, selector);

            var value = Expression.Parameter(typeof(string), "v");
            var ordered = Expression.Call(
                enumerableOrderBy.MakeGenericMethod(typeof(string), typeof(string)),
                values,
                Expression.Lambda(value, value));

            return Expression.Call(stringJoinValues, Expression.Constant(separator, typeof(string)), ordered);
        }

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

    /// <summary>
    /// The type to read a constant at when it sits opposite <paramref name="other"/> in an arithmetic
    /// expression: its own, when that is wider, and otherwise the type it is being combined with.
    /// </summary>
    /// <remarks>
    /// Reading it at the other operand's type is what makes a bare literal take the member's type
    /// rather than its own, and it is the only way a value whose CLR type the wire has no tag for — an
    /// unsigned or narrow integer, which is sent as a string — can be parsed at all. But it must not
    /// narrow a constant that was written wider than the member, which is what silently turns a
    /// floating-point expression into an integer one.
    /// </remarks>
    static Type Widest(Type other, Node node)
    {
        if (node is not ConstNode constant)
        {
            return other;
        }

        var declared = TagToType(constant.Tag);
        var value = Nullable.GetUnderlyingType(other) ?? other;

        return Rank(declared) is { } declaredRank && Rank(value) is { } valueRank && declaredRank > valueRank
            ? declared
            : other;
    }

    /// <summary>
    /// Applies C#'s numeric promotion to the operands of an arithmetic expression: both are converted
    /// to the widest of their types before the operation.
    /// </summary>
    /// <remarks>
    /// The client's own cast is not on the wire — translation drops conversions, since nearly all of
    /// them are nullable lifting or enum boxing that the server reproduces anyway — so the promotion is
    /// reapplied here from the operand types themselves. Without it an expression written to evaluate
    /// in floating point would be computed in an integer member's type and silently answer something
    /// else. Nullability is preserved, so a null operand still propagates rather than becoming a zero.
    /// </remarks>
    static void Promote(ref Expression left, ref Expression right)
    {
        var leftValue = Nullable.GetUnderlyingType(left.Type) ?? left.Type;
        var rightValue = Nullable.GetUnderlyingType(right.Type) ?? right.Type;

        if (leftValue == rightValue ||
            Rank(leftValue) is not { } leftRank ||
            Rank(rightValue) is not { } rightRank)
        {
            return;
        }

        var target = leftRank >= rightRank ? leftValue : rightValue;

        // A nullable operand makes the whole expression nullable, exactly as it does in C#.
        if (Nullable.GetUnderlyingType(left.Type) is not null ||
            Nullable.GetUnderlyingType(right.Type) is not null)
        {
            target = typeof(Nullable<>).MakeGenericType(target);
        }

        left = ConvertTo(left, target);
        right = ConvertTo(right, target);
    }

    // Only pairs C# itself would have promoted can reach here, so the ordering needs no rule for the
    // pairs C# refuses to convert between — decimal and double have no implicit conversion either way,
    // so an expression mixing them could not have been written.
    static int? Rank(Type type) =>
        Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or TypeCode.SByte => 1,
            TypeCode.Int16 or TypeCode.UInt16 => 2,
            TypeCode.Int32 or TypeCode.UInt32 => 3,
            TypeCode.Int64 or TypeCode.UInt64 => 4,
            TypeCode.Decimal => 5,
            TypeCode.Single => 6,
            TypeCode.Double => 7,
            _ => null
        };

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
    static readonly MethodInfo stringConcatObjects = StringMethod("Concat", typeof(object), typeof(object));

    static readonly MethodInfo enumHasFlag = typeof(Enum).GetMethod("HasFlag")!;

    static readonly MethodInfo enumerableSelect = typeof(Enumerable).GetMethods()
        .Single(_ => _.Name == "Select" && _.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);

    static readonly MethodInfo enumerableOrderBy = typeof(Enumerable).GetMethods()
        .Single(_ => _.Name == "OrderBy" && _.GetParameters().Length == 2);

    static readonly MethodInfo stringJoinValues = typeof(string).GetMethod("Join", [typeof(string), typeof(IEnumerable<string>)])!;

    static readonly MethodInfo convertToInt32 = typeof(Convert).GetMethod("ToInt32", [typeof(string)])!;
    static readonly MethodInfo convertToInt64 = typeof(Convert).GetMethod("ToInt64", [typeof(string)])!;
    static readonly MethodInfo convertToDecimal = typeof(Convert).GetMethod("ToDecimal", [typeof(string)])!;
    static readonly MethodInfo convertToDouble = typeof(Convert).GetMethod("ToDouble", [typeof(string)])!;
    static readonly MethodInfo convertToBoolean = typeof(Convert).GetMethod("ToBoolean", [typeof(string)])!;
    static readonly MethodInfo convertToByte = typeof(Convert).GetMethod("ToByte", [typeof(string)])!;
    static readonly MethodInfo convertToInt16 = typeof(Convert).GetMethod("ToInt16", [typeof(string)])!;

    // float.Parse rather than Convert.ToSingle: the provider translates Parse for every numeric type
    // but carries no ToSingle conversion.
    static readonly MethodInfo singleParse = typeof(float).GetMethod("Parse", [typeof(string)])!;


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

    /// <summary>
    /// Parses a wire constant into <paramref name="underlying"/> — the member's own type, resolved
    /// from the schema, never from the wire's tag.
    /// </summary>
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

        try
        {
            return ParseScalar(value, underlying);
        }
        // A value that does not parse as the member's type is a malformed request — most often a
        // client generated before the member's representation changed server-side — so it is reported
        // as a rejected query rather than surfacing as a server fault.
        catch (Exception exception) when (
            exception is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new ScryValidationException($"'{value}' is not a valid {underlying.Name} value.");
        }
    }

    static object ParseScalar(string value, Type underlying)
    {
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
