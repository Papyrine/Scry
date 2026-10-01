namespace Scry;

// Writing a BuilderQuery back out as a snippet: composed compactly and printed through QueryPrinter,
// so what the builder writes and what the format button produces are the same shape by construction.
public static partial class QueryBuilder
{
    /// <summary>
    /// The snippet a query reads as: the variable declarations ahead of it as they were written, then
    /// the query in the format button's layout.
    /// </summary>
    public static string Write(SchemaIndex index, BuilderQuery query)
    {
        var culture = CultureInfo.InvariantCulture;
        var model = Model(index, query);
        var parameter = query.Parameter;
        var builder = new StringBuilder("Query.");
        builder.Append(CSharpIdentifier.Escape(query.Source));

        if (query.Filters.Count > 0)
        {
            // A condition written with || or ?: binds looser than the && joining it to the others, so
            // it goes in parentheses to keep meaning what it meant on its own.
            var several = query.Filters.Count > 1;
            var conditions = query.Filters.Select(_ => _ switch
            {
                ComparisonFilter member => Condition(index, model, parameter, member),
                CodeFilter code when several && BindsLooserThanAnd(code.Code) => $"({code.Code})",
                CodeFilter code => code.Code,
                _ => throw new InvalidOperationException($"No filter of kind {_.GetType().Name}.")
            });
            builder.Append($".Where({parameter} => {string.Join(" && ", conditions)})");
        }

        for (var position = 0; position < query.Orders.Count; position++)
        {
            var order = query.Orders[position];
            var method = (position == 0 ? "OrderBy" : "ThenBy") + (order.Descending ? "Descending" : "");
            var key = order.Code ?? Access(index, model, parameter, order.Path!);
            builder.Append($".{method}({parameter} => {key})");
        }

        if (query.Skip is { } skip)
        {
            builder.Append(".Skip(").Append(skip.ToString(culture)).Append(')');
        }

        if (query.Take is { } take)
        {
            builder.Append(".Take(").Append(take.ToString(culture)).Append(')');
        }

        if (query.Projection is { } projection)
        {
            builder.Append($".Select({parameter} => {projection})");
        }
        else if (query.Columns is {Count: > 0} columns)
        {
            builder.Append($".Select({parameter} => new {{ {Columns(index, model, parameter, [], columns)} }})");
        }

        if (query.Terminal is { } terminal)
        {
            builder.Append('.').Append(terminal).Append("()");
        }

        var written = QueryPrinter.Format(builder.ToString());
        if (query.Await)
        {
            written = "await " + written;
        }

        if (query.Semicolon)
        {
            written += ";";
        }

        var preamble = Preamble(query.Preamble);
        if (preamble.Length == 0)
        {
            return written;
        }

        return $"{preamble}\n\n{written}";
    }

    /// <summary>
    /// The declarations as QueryPrinter keeps them: each line as written but for trailing whitespace,
    /// blank lines trimmed from either end, and one blank line between them and the query.
    /// </summary>
    static string Preamble(string preamble)
    {
        var lines = preamble
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(_ => _.TrimEnd())
            .SkipWhile(_ => _.Length == 0)
            .ToList();

        while (lines.Count > 0 &&
               lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    static string Condition(SchemaIndex index, string? model, string parameter, ComparisonFilter filter)
    {
        var access = Access(index, model, parameter, filter.Path);
        // A nullable bool is no condition on its own, so it is compared with the value it should hold.
        var nullable = model is not null &&
                       Member(index, model, filter.Path) is { } member &&
                       IsNullable(member.TypeDisplay);

        return filter.Operator switch
        {
            FilterOperator.Equal => $"{access} == {filter.Value}",
            FilterOperator.NotEqual => $"{access} != {filter.Value}",
            FilterOperator.LessThan => $"{access} < {filter.Value}",
            FilterOperator.LessThanOrEqual => $"{access} <= {filter.Value}",
            FilterOperator.GreaterThan => $"{access} > {filter.Value}",
            FilterOperator.GreaterThanOrEqual => $"{access} >= {filter.Value}",
            FilterOperator.Contains => $"{access}.Contains({filter.Value})",
            FilterOperator.StartsWith => $"{access}.StartsWith({filter.Value})",
            FilterOperator.EndsWith => $"{access}.EndsWith({filter.Value})",
            FilterOperator.IsTrue when nullable => $"{access} == true",
            FilterOperator.IsTrue => access,
            FilterOperator.IsFalse when nullable => $"{access} == false",
            FilterOperator.IsFalse => $"!{access}",
            FilterOperator.IsNull => $"{access} == null",
            FilterOperator.IsNotNull => $"{access} != null",
            _ => throw new InvalidOperationException($"No operator {filter.Operator}.")
        };
    }

    static bool BindsLooserThanAnd(string code) =>
        SyntaxFactory.ParseExpression(code).Kind() is
            SyntaxKind.LogicalOrExpression or
            SyntaxKind.CoalesceExpression or
            SyntaxKind.ConditionalExpression or
            SyntaxKind.SimpleLambdaExpression or
            SyntaxKind.ParenthesizedLambdaExpression or
            SyntaxKind.SimpleAssignmentExpression;

    /// <summary>
    /// A member read from the lambda's parameter, through the navigations ahead of it — each declared
    /// nullable one with the null-forgiving operator after it, which is how the generated client spells
    /// it and what keeps the editor from warning on every read.
    /// </summary>
    static string Access(SchemaIndex index, string? model, string parameter, IReadOnlyList<string> path)
    {
        var builder = new StringBuilder(parameter);
        var current = model;
        for (var position = 0; position < path.Count; position++)
        {
            var name = path[position];
            builder.Append('.').Append(CSharpIdentifier.Escape(name));
            if (position == path.Count - 1)
            {
                break;
            }

            var member = current is null ? null : Find(index, current, name);
            if (member is not null &&
                IsNullable(member.TypeDisplay))
            {
                builder.Append('!');
            }

            current = member is null ? null : Target(index, member);
        }

        return builder.ToString();
    }

    static string Columns(SchemaIndex index, string? model, string parameter, IReadOnlyList<string> prefix, IReadOnlyList<BuilderColumn> columns) =>
        string.Join(
            ", ",
            columns.Select(_ => _ switch
            {
                MemberColumn member => Access(index, model, parameter, [.. prefix, member.Member]),
                NestedColumn nested => $"{CSharpIdentifier.Escape(nested.Member)} = new {{ {Columns(index, model, parameter, [.. prefix, nested.Member], nested.Columns)} }}",
                CodeColumn code => code.Code,
                _ => throw new InvalidOperationException($"No column of kind {_.GetType().Name}.")
            }));
}
