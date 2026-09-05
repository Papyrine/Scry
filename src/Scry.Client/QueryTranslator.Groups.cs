// Grouping: the key the query grouped by, and the aggregates a grouped projection folds it with.
sealed partial class QueryTranslator
{
    // How to say "the key" for a query that grouped by one. A key that is a plain member says so by
    // its own path, which is what the server matches it back by; a computed key has no path, so it is
    // named by position instead.
    Node? groupKeyNode;

    // The same, per part of a composite key, by the name the key type gave it — what 'g.Key.Region' is
    // resolved through. Null while the query grouped by a single key.
    Dictionary<string, Node>? groupKeyParts;

    // A key built from several members groups on all of them at once. Each part keeps the name the key
    // type gave it, so a later read of 'g.Key.Region' can be resolved back to the member it grouped by;
    // the server matches the same paths to the positions it grouped at.
    GroupByOp TranslateGroupBy(LambdaExpression keyLambda)
    {
        var parameter = keyLambda.Parameters[0];

        if (keyLambda.Body is NewExpression or MemberInitExpression)
        {
            var keys = new List<Node>();
            var parts = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var (name, value) in NestedMembers(keyLambda.Body))
            {
                var key = TranslateExpr(value, parameter);
                keys.Add(key);

                // A member part is named back by its path; a computed one by its position.
                parts.Add(name, key is MemberNode member ? member : new GroupKeyNode(keys.Count - 1));
            }

            if (keys.Count == 0)
            {
                throw new NotSupportedException("A composite GroupBy key must have at least one member.");
            }

            groupKeyNode = null;
            groupKeyParts = parts;
            return new(keys);
        }

        var single = TranslateExpr(keyLambda.Body, parameter);
        groupKeyNode = single is MemberNode path ? path : new GroupKeyNode(0);
        groupKeyParts = null;
        return new([single]);
    }

    /// <summary>
    /// Translates an aggregate folding the group, including the composed forms: a <c>Where</c> before
    /// the fold filters the rows — <c>Count(predicate)</c> abbreviates it — and <c>Select</c> +
    /// <c>Distinct</c> folds only the distinct selected values. The grammar over the group is
    /// <c>g [.Where(pred)] [.Select(sel) [.Distinct()]] .Fold(…)</c>, written in that order because
    /// each stage reads what the previous one produced.
    /// </summary>
    AggregateNode TranslateAggregate(MethodCallExpression call)
    {
        Node? predicate = null;
        Node? selector = null;
        var distinct = false;

        // The fold's source, unwrapped from the outside in — so a Select is seen before the Where
        // written ahead of it, and a filter captured before any Select was seen must have been
        // written over the selected values, which the wire's predicate does not read.
        var source = call.Arguments[0];
        while (source is MethodCallExpression inner)
        {
            switch (inner.Method.Name)
            {
                case "Distinct" when inner.Arguments.Count == 1 && !distinct && selector is null:
                    distinct = true;
                    break;

                case "Select" when inner.Arguments.Count == 2 && selector is null && predicate is null:
                    var projected = Lambda(inner.Arguments[1]);
                    selector = TranslateExpr(projected.Body, projected.Parameters[0]);
                    break;

                case "Where" when inner.Arguments.Count == 2 && predicate is null:
                    var filter = Lambda(inner.Arguments[1]);
                    predicate = TranslateExpr(filter.Body, filter.Parameters[0]);
                    break;

                default:
                    throw new NotSupportedException(
                        $"'{inner.Method.Name}' cannot compose into an aggregate over a group this way — filter the rows, then select the values, then Distinct, then fold.");
            }

            source = inner.Arguments[0];
        }

        // string.Concat over the group's values is the text fold with nothing between them —
        // string.Join's empty-separator spelling, and like Join it folds the whole group.
        if (call is {Method.Name: "Concat", Arguments.Count: 1} &&
            call.Method.DeclaringType == typeof(string))
        {
            // The generic overload is what a non-string selector binds to.
            if (call.Method.IsGenericMethod)
            {
                throw new NotSupportedException("string.Concat over a group joins text — select a string member.");
            }

            if (predicate is not null ||
                distinct)
            {
                throw new NotSupportedException("string.Concat folds the whole group — filter the rows before grouping.");
            }

            if (selector is not MemberNode path)
            {
                throw new NotSupportedException(
                    "string.Concat over a group joins a text member the rows carry — string.Concat(_.Select(_ => _.Code)).");
            }

            return new(AggregateFn.Join, path, "");
        }

        if (call is {Method.Name: "Count", Arguments.Count: 1})
        {
            // Without a Distinct there is nothing for selected values to change about a count, so the
            // selector is dropped rather than carried as noise.
            return new(AggregateFn.Count, distinct ? selector : null)
            {
                Predicate = predicate,
                Distinct = distinct
            };
        }

        if (call is {Method.Name: "Count", Arguments.Count: 2})
        {
            if (predicate is not null)
            {
                throw new NotSupportedException("Count over a group cannot combine a Where with its own predicate.");
            }

            if (distinct || selector is not null)
            {
                throw new NotSupportedException("Count over selected values takes no predicate — filter the rows before selecting.");
            }

            var counted = Lambda(call.Arguments[1]);
            return new(AggregateFn.Count, Selector: null)
            {
                Predicate = TranslateExpr(counted.Body, counted.Parameters[0])
            };
        }

        var function = call.Method.Name switch
        {
            "Sum" => AggregateFn.Sum,
            "Average" => AggregateFn.Average,
            "Min" => AggregateFn.Min,
            "Max" => AggregateFn.Max,
            // Reached for any other call written in a grouped projection, where only the group key and
            // an aggregate are expressible — so the arity below is safe to assume.
            _ => throw new NotSupportedException(
                $"'{call.Method.Name}' is not an aggregate. A grouped projection may only use the group key, Count/Sum/Average/Min/Max, and string.Join or string.Concat over the group's values.")
        };

        if (call.Arguments.Count == 2)
        {
            if (selector is not null)
            {
                throw new NotSupportedException(
                    $"Aggregate '{call.Method.Name}' already reads selected values — fold them bare, without a second selector.");
            }

            var folded = Lambda(call.Arguments[1]);
            selector = TranslateExpr(folded.Body, folded.Parameters[0]);
        }

        if (selector is null)
        {
            throw new NotSupportedException($"Aggregate '{call.Method.Name}' requires a selector.");
        }

        return new(function, selector)
        {
            Predicate = predicate,
            Distinct = distinct
        };
    }

    /// <summary>
    /// Translates <c>string.Join(separator, g.Select(x =&gt; x.Member))</c> — the text aggregate,
    /// SQL's <c>STRING_AGG</c>. The separator is a constant; the values are the group's rows read
    /// through the selector. Returns null when the call is not string.Join at all, so the caller can
    /// keep trying.
    /// </summary>
    AggregateNode? TryJoinText(MethodCallExpression call, ParameterExpression root)
    {
        if (call.Method.Name != "Join" ||
            call.Method.DeclaringType != typeof(string) ||
            call.Arguments.Count != 2 ||
            !ReferencesParameter(call, root))
        {
            return null;
        }

        // A non-string selector binds the generic overload. So does a char separator, whatever the
        // selector reads — there is no non-generic char overload over a sequence — so it is the value
        // type the overload closed over that answers the question, not the genericity itself.
        if (call.Method.IsGenericMethod &&
            call.Method.GetGenericArguments()[0] != typeof(string))
        {
            throw new NotSupportedException("string.Join over a group joins text — select a string member.");
        }

        // The separator is a constant, written as a string or as the char spelling of one.
        if (ReferencesParameter(call.Arguments[0], root) ||
            SeparatorText(Evaluate(call.Arguments[0])) is not { } separator)
        {
            throw new NotSupportedException("string.Join over a group takes a constant separator.");
        }

        if (call.Arguments[1] is not MethodCallExpression {Method.Name: "Select", Arguments: [var source, var projection]} ||
            source != root)
        {
            throw new NotSupportedException(
                """
                string.Join over a group joins the values its selector reads — string.Join(", ", _.Select(_ => _.Name)).
                """);
        }

        // An aggregate selector is a member path, as every aggregate's is.
        var selector = Lambda(projection);
        if (TranslateExpr(selector.Body, selector.Parameters[0]) is not MemberNode path)
        {
            throw new NotSupportedException(
                "string.Join over a group joins a text member the rows carry, not a computed value.");
        }

        return new(AggregateFn.Join, path, separator);
    }

    // string.Join's separator is a string or, the shorter spelling, a char. Null for anything
    // else, which is a separator the call did not carry as a constant at all.
    static string? SeparatorText(object? constant) =>
        constant switch
        {
            string text => text,
            char character => character.ToString(),
            _ => null
        };

    static bool IsGrouping(Type type) =>
        type.IsGenericType &&
        type.GetGenericTypeDefinition() == typeof(IGrouping<,>);

    // A call applied to the group itself — g.Count(), g.Sum(x => …) — rather than to something read
    // out of it, such as g.Key.ToUpper(), which is an ordinary function over the key.
    static bool IsCallOver(MethodCallExpression call, ParameterExpression target) =>
        call.Object == target ||
        (call.Arguments.Count > 0 && call.Arguments[0] == target);

    // The same question through the Where/Select/Distinct chain a composed aggregate may put between
    // the fold and the group. A chain bottoming at anything else — g.Key, a closure — is not the
    // group being folded.
    // Followed down the receiver of each call, which for every LINQ operator is the first argument of
    // a static method. An instance call has no source to follow: g.ToString() reads the group as a
    // value, and is left to the translator's ordinary refusal rather than read as a fold.
    static bool IsChainOver(MethodCallExpression call, ParameterExpression target)
    {
        Expression? current = call;
        while (current is MethodCallExpression {Object: null, Arguments.Count: > 0} inner)
        {
            current = inner.Arguments[0];
        }

        return current == target;
    }
}
