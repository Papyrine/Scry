// The questions asked about a set of rows other than the one being read: a collection the row holds,
// another source, or a client-side set.
sealed partial class QueryTranslator
{
    /// <summary>
    /// Translates a question asked about a collection navigation — <c>_.Orders.Any(o =&gt; …)</c>,
    /// <c>_.Orders.Count()</c>, <c>_.Orders.Sum(o =&gt; o.Total)</c> — into a subquery node, including
    /// the <c>Where(…).Count()</c> form, whose filter folds into the subquery's own predicate. Returns
    /// null when the call is not one of these, so the caller can go on trying other forms.
    /// </summary>
    SubqueryNode? TrySubquery(MethodCallExpression call, ParameterExpression root)
    {
        // Contains over a collection the row holds asks whether any element equals the value — the
        // same question Any answers, and the only way to ask it of a collection of values, whose
        // elements have no member to compare.
        if (call.Method.Name == "Contains" &&
            TryContainsOverCollection(call, root) is { } contains)
        {
            return contains;
        }

        var function = call.Method.Name switch
        {
            "Any" => SubqueryFn.Any,
            "All" => SubqueryFn.All,
            "Count" => SubqueryFn.Count,
            "Sum" => SubqueryFn.Sum,
            "Average" => SubqueryFn.Average,
            "Min" => SubqueryFn.Min,
            "Max" => SubqueryFn.Max,
            _ => (SubqueryFn?)null
        };

        if (function is null ||
            call.Arguments.Count == 0)
        {
            return null;
        }

        // A preceding Where over the same collection contributes the subquery's predicate.
        var source = call.Arguments[0];
        Expression? filter = null;
        if (source is MethodCallExpression {Method.Name: "Where", Arguments.Count: 2} where)
        {
            source = where.Arguments[0];
            filter = where.Arguments[1];
        }

        if (!IsRootedCollection(source, root))
        {
            return null;
        }

        var path = MemberPath((MemberExpression)source);
        var argument = call.Arguments.Count > 1 ? Lambda(call.Arguments[1]) : null;
        var predicate = filter is null ? null : Lambda(filter);

        // Any/All/Count take a predicate; the rest take a value selector.
        Node? selector = null;
        if (argument is not null)
        {
            if (function is
                SubqueryFn.Any or
                SubqueryFn.All or
                SubqueryFn.Count)
            {
                if (predicate is not null)
                {
                    throw new NotSupportedException(
                        $"'{call.Method.Name}' over a collection cannot combine a Where with its own predicate.");
                }

                predicate = argument;
            }
            else
            {
                selector = TranslateExpr(argument.Body, argument.Parameters[0]);
            }
        }
        else if (function is
                 SubqueryFn.Sum or
                 SubqueryFn.Average or
                 SubqueryFn.Min or
                 SubqueryFn.Max)
        {
            // Sum() and friends without a selector fold the elements themselves, which only a
            // collection of values can be asked for — there is nothing else there to read.
            selector = new ElementNode();
        }

        Node? translatedPredicate;
        if (predicate is null)
        {
            translatedPredicate = null;
        }
        else
        {
            translatedPredicate = TranslateExpr(predicate.Body, predicate.Parameters[0]);
        }

        return new(
            path,
            function.Value,
            translatedPredicate,
            selector);
    }

    /// <summary>
    /// Translates a projection member that returns a collection navigation's elements —
    /// <c>_.Lines.Select(l =&gt; l.Sku).ToList()</c>, <c>_.Lines.Where(…).Select(l =&gt; new { l.Sku,
    /// l.Price }).ToArray()</c>, or <c>_.Tags.ToList()</c> over a collection of values — into a
    /// <see cref="CollectionValue"/>. Returns null when the expression is not one of these forms, so the
    /// caller can go on reading it as a value.
    /// </summary>
    /// <remarks>
    /// Deliberately one level: each element becomes a value or a flat object of values, and a member
    /// of that object that constructs another object, or asks a question of another collection, is
    /// refused here rather than carried to the server to be refused there.
    /// </remarks>
    CollectionValue? TryCollection(Expression expression, ParameterExpression root)
    {
        var current = expression;

        // ToList and ToArray only materialize what Select produced, so the wire carries neither.
        if (current is MethodCallExpression {Object: null, Method.Name: "ToList" or "ToArray", Arguments.Count: 1} materialize)
        {
            current = materialize.Arguments[0];
        }

        LambdaExpression? selector = null;
        if (current is MethodCallExpression {Object: null, Method.Name: "Select", Arguments.Count: 2} select)
        {
            selector = Lambda(select.Arguments[1]);
            current = select.Arguments[0];
        }

        Expression? filter = null;
        if (current is MethodCallExpression {Object: null, Method.Name: "Where", Arguments.Count: 2} where)
        {
            filter = where.Arguments[1];
            current = where.Arguments[0];
        }

        // Nothing of the three forms was read, or what they read is not a collection the row holds.
        if (ReferenceEquals(current, expression) ||
            !IsRootedCollection(current, root))
        {
            return null;
        }

        var path = MemberPath((MemberExpression) current);
        var element = CollectionElement(current.Type);

        Node? predicate = null;
        if (filter is not null)
        {
            var lambda = Lambda(filter);
            predicate = Correlated(TranslateExpr(lambda.Body, lambda.Parameters[0]), path);
        }

        if (selector is null)
        {
            if (!IsValue(element))
            {
                throw new NotSupportedException(
                    $"'{string.Join('.', path)}' is a collection of rows, so returning it needs a Select saying what to return of each element — a value, or an object of values.");
            }

            // The values themselves, which is all a collection of values has to return.
            return new(path, predicate, new ElementNode());
        }

        if (selector.Parameters.Count != 1)
        {
            throw new NotSupportedException("Select with an index is not supported over a collection the row holds.");
        }

        var parameter = selector.Parameters[0];
        if (selector.Body is NewExpression or MemberInitExpression)
        {
            var members = new List<ProjectionMember>();
            foreach (var (name, value) in NestedMembers(selector.Body))
            {
                if (value is NewExpression or MemberInitExpression)
                {
                    throw new NotSupportedException(
                        $"'{name}' nests an object inside a projected collection. An element of a projected collection is a value or a flat object of values.");
                }

                if (members.Any(_ => _.Name == name))
                {
                    throw ProjectedTwice(name);
                }

                members.Add(new(name, new NodeValue(Correlated(TranslateExpr(value, parameter), path))));
            }

            if (members.Count == 0)
            {
                throw new NotSupportedException("An element of a projected collection must have at least one member.");
            }

            return new(path, predicate, Projection: new(members));
        }

        return new(path, predicate, Correlated(TranslateExpr(selector.Body, parameter), path));
    }

    // What an element of a projected collection may be made of: the element's own values, composed
    // with the operators and functions a predicate is. A further collection — a question asked of
    // another collection, a membership test against another source — would multiply the request's
    // cost per element, and is refused here as the server refuses it.
    static Node Correlated(Node node, IReadOnlyList<string> path)
    {
        if (Contains(node, _ => _ is SubqueryNode or InSourceNode))
        {
            throw new NotSupportedException(
                $"An element of '{string.Join('.', path)}' may only carry values read off the element — not a question asked of another collection or source.");
        }

        return node;
    }

    static bool Contains(Node node, Func<Node, bool> test)
    {
        if (test(node))
        {
            return true;
        }

        return node switch
        {
            BinaryNode binary => Contains(binary.Left, test) || Contains(binary.Right, test),
            UnaryNode unary => Contains(unary.Operand, test),
            ConditionalNode conditional => Contains(conditional.Test, test) ||
                                           Contains(conditional.IfTrue, test) ||
                                           Contains(conditional.IfFalse, test),
            CallNode call => Contains(call.Target, test) || call.Arguments.Any(_ => Contains(_, test)),
            CollateNode collate => Contains(collate.Target, test),
            _ => false
        };
    }

    // The element type of a collection navigation as the generated model types it: IReadOnlyList<T>,
    // or any other IEnumerable<T>.
    static Type CollectionElement(Type collection)
    {
        if (collection.IsArray)
        {
            return collection.GetElementType()!;
        }

        var enumerable = collection.IsGenericType && collection.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? collection
            : collection.GetInterfaces().First(_ => _.IsGenericType && _.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable.GetGenericArguments()[0];
    }

    /// <summary>
    /// Translates <c>_.Tags.Contains("urgent")</c> — membership of a collection belonging to the row —
    /// into an <c>Any</c> over its elements. Both the instance form (<c>List&lt;T&gt;.Contains</c>) and
    /// the static form (<c>Enumerable.Contains</c>, which is what the generated model's
    /// <c>IReadOnlyList&lt;T&gt;</c> binds to) arrive here. Returns null for the closure-set form,
    /// <c>ids.Contains(_.Id)</c>, which is a different question and becomes a SQL <c>IN</c>.
    /// </summary>
    SubqueryNode? TryContainsOverCollection(MethodCallExpression call, ParameterExpression root)
    {
        Expression source;
        Expression argument;
        if (call.Object is null)
        {
            if (call.Arguments.Count != 2)
            {
                return null;
            }

            source = call.Arguments[0];
            argument = call.Arguments[1];
        }
        else
        {
            if (call.Arguments.Count != 1)
            {
                return null;
            }

            source = call.Object;
            argument = call.Arguments[0];
        }

        if (!IsRootedCollection(source, root))
        {
            return null;
        }

        var value = TranslateExpr(argument, root);
        if (value is not ConstNode)
        {
            throw new NotSupportedException(
                """
                Contains over a collection the row holds takes a constant.
                The test is evaluated against the collection's elements, and the row that owns them is not in scope there.
                """);
        }

        return new(
            MemberPath((MemberExpression) source),
            SubqueryFn.Any,
            new BinaryNode(BinaryOp.Equal, new ElementNode(), value));
    }

    /// <summary>
    /// Translates membership of a set drawn from another Scry source — the candidates come from a
    /// captured query rather than from closure state, so they are named rather than evaluated.
    /// </summary>
    InSourceNode InSource(IQueryable queryable, QueryProvider provider, Expression value, ParameterExpression root)
    {
        Beside(provider, "The set of 'Contains'");

        // Walked directly rather than run through the operator translator: the Select here names a
        // bare value to compare against, where an ordinary projection must construct an object.
        Node? predicate = null;
        Node? selector = null;
        var current = queryable.Expression;
        while (current is MethodCallExpression inner)
        {
            switch (inner.Method.Name)
            {
                case "Select" when selector is null && inner.Arguments.Count == 2:
                    var projection = Lambda(inner.Arguments[1]);
                    selector = TranslateExpr(projection.Body, projection.Parameters[0]);
                    break;

                case "Where" when inner.Arguments.Count == 2:
                    var filter = Lambda(inner.Arguments[1]);
                    var clause = TranslateExpr(filter.Body, filter.Parameters[0]);
                    predicate = predicate is null
                        ? clause
                        : new BinaryNode(BinaryOp.AndAlso, clause, predicate);
                    break;

                default:
                    throw new NotSupportedException(
                        "The source of a membership test may only carry a Where and a Select of one value.");
            }

            current = inner.Arguments[0];
        }

        if (selector is null)
        {
            throw new NotSupportedException(
                "A membership test against another source must Select the single value to compare against.");
        }

        return new(TranslateExpr(value, root), provider.Root, selector, predicate);
    }

    // The receiver holds the candidate set and must be closure state; the tested value must read from
    // the row. Both the instance form (List.Contains) and the static form (Enumerable.Contains) map here.
    static bool IsSetContains(
        MethodCallExpression call,
        ParameterExpression root,
        [NotNullWhen(true)] out Expression? set,
        [NotNullWhen(true)] out Expression? value)
    {
        set = null;
        value = null;

        if (call.Method.Name != "Contains")
        {
            return false;
        }

        if (call is {Object: { } instance, Arguments.Count: 1})
        {
            (set, value) = (instance, call.Arguments[0]);
        }
        else if (call is {Object: null, Arguments.Count: 2})
        {
            (set, value) = (call.Arguments[0], call.Arguments[1]);
        }
        else
        {
            return false;
        }

        set = UnwrapSet(set);

        return !ReferencesParameter(set, root) &&
               ReferencesParameter(value, root);
    }

    /// <summary>
    /// Reads through to the collection a set expression really denotes. An array receiver binds to
    /// <c>MemoryExtensions.Contains</c>, so what arrives is a <c>ReadOnlySpan</c> produced by a
    /// conversion or an <c>AsSpan</c> call — a ref struct, which cannot be returned from the compiled
    /// lambda the values are read with. The array behind it can.
    /// </summary>
    static Expression UnwrapSet(Expression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} convert:
                    expression = convert.Operand;
                    continue;
                case MethodCallExpression {Object: null, Arguments.Count: 1, Type.IsByRefLike: true} call:
                    expression = call.Arguments[0];
                    continue;
                case MethodCallExpression {Object: { } instance, Arguments.Count: 0, Type.IsByRefLike: true}:
                    expression = instance;
                    continue;
                default:
                    return expression;
            }
        }
    }

    static IEnumerable<Node> SetConstants(object? set)
    {
        if (set is IEnumerable values)
        {
            return values.Cast<object?>().Select(ConstantOf);
        }

        throw new NotSupportedException("The Contains set must be a collection of values.");
    }

    // A member path rooted at the query parameter whose value is a collection — the shape the
    // generated model gives a collection navigation.
    static bool IsRootedCollection(Expression expression, ParameterExpression root) =>
        expression is MemberExpression member &&
        IsRooted(member, root) &&
        member.Type != typeof(string) &&
        member.Type.GetInterfaces()
            .Any(_ => _.IsGenericType && _.GetGenericTypeDefinition() == typeof(IEnumerable<>));
}
