// The projection a query ends in, and the nested objects it may construct along the way.
sealed partial class QueryTranslator
{
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
        var names = ProjectionNames(construction);
        var arguments = construction.Arguments;
        var members = new List<ProjectionMember>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (TryAttachment(arguments[i], parameter, grouped, [names[i]], prefix: []))
            {
                continue;
            }

            members.Add(new(names[i], ProjectionValue(arguments[i], parameter, grouped, [names[i]])));
        }

        return Built(members);
    }

    Projection FromMemberInit(MemberInitExpression init, ParameterExpression parameter, bool grouped)
    {
        var members = new List<ProjectionMember>(init.Bindings.Count);
        foreach (var binding in init.Bindings)
        {
            if (binding is not MemberAssignment assignment)
            {
                throw new NotSupportedException("Only simple member assignments are supported in a projection.");
            }

            if (TryAttachment(assignment.Expression, parameter, grouped, [assignment.Member.Name], prefix: []))
            {
                continue;
            }

            members.Add(new(assignment.Member.Name, ProjectionValue(assignment.Expression, parameter, grouped, [assignment.Member.Name])));
        }

        return Built(members);
    }

    // An attachment leaves the wire projection entirely, so a projection of nothing else would reach
    // the server empty. Reported here rather than as the server's own "empty projection", which would
    // read as a wire fault rather than the missing keys it really is.
    static Projection Built(List<ProjectionMember> members)
    {
        if (members.Count == 0)
        {
            throw new NotSupportedException(
                "A projection of nothing but attachments has no members left to send. Project the row's key beside the attachment — that is what the fetch is keyed by.");
        }

        return new(members);
    }

    ProjectionValue ProjectionValue(Expression expression, ParameterExpression parameter, bool grouped, IReadOnlyList<string>? target = null)
    {
        target ??= [];
        // Over a group the row being read is the grouping itself, which TranslateExpr already knows how
        // to read: its Key is the group key and a call taking it is an aggregate. That leaves the two
        // free to compose — _.Sum(x => x.Amount) / _.Count(), or _.Key.ToUpper().
        if (grouped)
        {
            return new NodeValue(TranslateExpr(expression, parameter));
        }

        // A member whose value is itself a constructed object is a nested projection into a navigation
        // (e.g. Department = new DepartmentInfo(_.Department.Name)), producing a nested result object.
        // One that reads nothing from the row is not a projection at all — it is a constructed constant
        // such as new DateTime(2026, 1, 1), and falls through to be evaluated as one.
        if (expression is NewExpression or MemberInitExpression &&
            ReferencesParameter(expression, parameter))
        {
            return TranslateNested(expression, parameter, target);
        }

        return new NodeValue(TranslateExpr(expression, parameter));
    }

    NestedValue TranslateNested(Expression expression, ParameterExpression parameter, IReadOnlyList<string> target)
    {
        var members = new List<(string Name, Node Value)>();
        foreach (var (name, value) in NestedMembers(expression))
        {
            // An attachment nested inside a projected object reads the same full path it would at the
            // top level, so only where its handle lands differs.
            if (TryAttachment(value, parameter, grouped: false, [..target, name], prefix: []))
            {
                continue;
            }

            members.Add((name, TranslateExpr(value, parameter)));
        }

        if (members.Count == 0)
        {
            throw new NotSupportedException("A nested projection must have at least one member.");
        }

        // The navigation a nested object descends into is inferred from the member paths it reads —
        // which may sit anywhere inside an expression, not only at its root, so they are collected from
        // the whole tree and then stripped back off it.
        var paths = new List<IReadOnlyList<string>>();
        foreach (var (_, value) in members)
        {
            CollectPaths(value, paths);
        }

        var prefix = CommonNavigationPrefix(paths);
        if (prefix.Count == 0)
        {
            throw new NotSupportedException(
                "A nested projection must read from a single navigation property (every member sharing, e.g., _.Department).");
        }

        var projected = members
            .Select(_ => new ProjectionMember(_.Name, new NodeValue(StripPrefix(_.Value, prefix.Count))))
            .ToList();

        return new(prefix, new(projected));
    }

    static IEnumerable<(string Name, Expression Value)> NestedMembers(Expression expression)
    {
        switch (expression)
        {
            case NewExpression construction:
                var names = ProjectionNames(construction);
                for (var i = 0; i < construction.Arguments.Count; i++)
                {
                    yield return (names[i], construction.Arguments[i]);
                }

                break;

            case MemberInitExpression init:
                foreach (var binding in init.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                    {
                        throw new NotSupportedException("Only simple member assignments are supported in a projection.");
                    }

                    yield return (assignment.Member.Name, assignment.Expression);
                }

                break;

            default:
                throw new NotSupportedException("A nested projection must construct an object.");
        }
    }

    static string[] ProjectionNames(NewExpression construction)
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

    static string Capitalize(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
