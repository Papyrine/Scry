namespace Scry;

// Reading a snippet into a BuilderQuery. The reader accepts what the writer could write back without
// changing what the query means, and says what stopped it otherwise.
public static partial class QueryBuilder
{
    // The order the writer puts the operators in. A query applying them in another order means
    // something else — a Skip ahead of a Where pages the rows before filtering them — so the reader
    // refuses one rather than write it back reordered.
    enum Stage
    {
        Source,
        Where,
        OrderBy,
        Skip,
        Take,
        Select,
        Terminal
    }

    // The terminals a query can end with, written without arguments. Those taking one — a predicate,
    // a key selector — say something the builder has no row for.
    static readonly HashSet<string> terminals =
    [
        "ToListAsync",
        "ToList",
        "ToArrayAsync",
        "ToArray",
        "CountAsync",
        "Count",
        "LongCountAsync",
        "LongCount",
        "AnyAsync",
        "Any",
        "FirstAsync",
        "First",
        "FirstOrDefaultAsync",
        "FirstOrDefault",
        "SingleAsync",
        "Single",
        "SingleOrDefaultAsync",
        "SingleOrDefault"
    ];

    /// <summary>
    /// Reads a snippet into a query, or says why it cannot be read. A blank snippet is neither: it
    /// has no query, and nothing is wrong with it.
    /// </summary>
    public static BuilderRead Read(SchemaIndex index, string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return new(null, null);
        }

        var layout = SnippetLayout.Of(snippet);
        if (layout.Problem is {IsError: true} problem)
        {
            return Refuse(problem.Message);
        }

        var text = layout.Expression.Trim();
        var semicolon = text.EndsWith(';');
        if (semicolon)
        {
            text = text[..^1].TrimEnd();
        }

        var expression = SyntaxFactory.ParseExpression(text);
        if (text.Length == 0 ||
            expression.GetDiagnostics().Any(_ => _.Severity == DiagnosticSeverity.Error) ||
            // ParseExpression stops at the first token it cannot continue from and reports nothing
            // about the rest, so a query with trailing garbage parses "successfully" as its prefix.
            expression.FullSpan.End != text.Length)
        {
            return Refuse("The query does not parse yet. Finish it in the editor, or start a new one here.");
        }

        // The query is written back whole, and a comment belongs to no part the writer knows of.
        if (expression
            .DescendantTrivia(descendIntoTrivia: true)
            .Any(_ => !_.IsKind(SyntaxKind.WhitespaceTrivia) && !_.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            return Refuse("The query has a comment in it, which writing it back would lose. Edit it in the editor, or start a new one here.");
        }

        var awaited = false;
        if (expression is AwaitExpressionSyntax awaitExpression)
        {
            awaited = true;
            expression = awaitExpression.Expression;
        }

        var calls = new List<(string Name, ArgumentListSyntax Arguments)>();
        var root = expression;
        while (root is InvocationExpressionSyntax
               {
                   Expression: MemberAccessExpressionSyntax
                   {
                       Name: IdentifierNameSyntax name,
                       Expression: var inner
                   }
               } invocation)
        {
            calls.Add((name.Identifier.ValueText, invocation.ArgumentList));
            root = inner;
        }

        calls.Reverse();

        if (root is not MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax {Identifier.ValueText: "Query"},
                Name: IdentifierNameSyntax sourceName
            })
        {
            return Refuse("The builder reads a query that starts from a source, as Query.Employee does.");
        }

        var sourceText = sourceName.Identifier.ValueText;
        if (index.Sources.FirstOrDefault(_ => _.Name == sourceText) is not { } source)
        {
            return Refuse($"Query.{sourceText} is not a source this server publishes.");
        }

        var reader = new ChainReader(index, source.Model);
        var query = new BuilderQuery(source.Name)
        {
            Preamble = layout.Preamble,
            Await = awaited,
            Semicolon = semicolon
        };

        var stage = Stage.Source;
        for (var position = 0; position < calls.Count; position++)
        {
            var (name, arguments) = calls[position];
            var next = StageOf(name);
            if (next is null)
            {
                return Refuse($"The query uses {name}, which the builder does not edit. Edit it in the editor, or start a new one here.");
            }

            // ThenBy follows an OrderBy, or another ThenBy, and nothing else.
            var then = name.StartsWith("ThenBy", StringComparison.Ordinal);
            if (then != (stage == Stage.OrderBy && next == Stage.OrderBy) ||
                next < stage ||
                (next == stage && next is not Stage.Where && !then))
            {
                return Refuse($"The builder writes Where, OrderBy, ThenBy, Skip, Take and Select in that order, once each but for Where and ThenBy, and this query applies {name} out of it.");
            }

            stage = next.Value;
            if (stage == Stage.Terminal &&
                (position != calls.Count - 1 || arguments.Arguments.Count > 0))
            {
                return Refuse($"The query ends with {name}({arguments.Arguments}), which the builder does not edit.");
            }

            switch (stage)
            {
                case Stage.Where:
                    if (reader.Body(arguments) is not { } predicate)
                    {
                        return Refuse(reader.Problem!);
                    }

                    query = query with
                    {
                        Filters = [.. query.Filters, .. reader.Filters(predicate)]
                    };
                    break;
                case Stage.OrderBy:
                    if (reader.Body(arguments) is not { } key)
                    {
                        return Refuse(reader.Problem!);
                    }

                    query = query with
                    {
                        Orders = [.. query.Orders, reader.Order(key, name.EndsWith("Descending", StringComparison.Ordinal))]
                    };
                    break;
                case Stage.Skip:
                case Stage.Take:
                    if (arguments.Arguments is not [{NameColon: null, Expression: LiteralExpressionSyntax {Token.Value: int count}}] ||
                        count < 0)
                    {
                        return Refuse($"The query passes {name} something other than a number, which the builder does not edit.");
                    }

                    query = stage == Stage.Skip
                        ? query with {Skip = count}
                        : query with {Take = count};
                    break;
                case Stage.Select:
                    if (reader.Body(arguments) is not { } projection)
                    {
                        return Refuse(reader.Problem!);
                    }

                    query = projection is AnonymousObjectCreationExpressionSyntax anonymous
                        ? query with {Columns = reader.Columns(anonymous, [])}
                        : query with {Projection = projection.ToString()};
                    break;
                case Stage.Terminal:
                    query = query with
                    {
                        Terminal = name
                    };
                    break;
            }
        }

        return new(
            query with
            {
                Parameter = reader.Parameter ?? "_"
            },
            null);
    }

    static BuilderRead Refuse(string problem) =>
        new(null, problem);

    static Stage? StageOf(string name) =>
        name switch
        {
            "Where" => Stage.Where,
            "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" => Stage.OrderBy,
            "Skip" => Stage.Skip,
            "Take" => Stage.Take,
            "Select" => Stage.Select,
            _ when terminals.Contains(name) => Stage.Terminal,
            _ => null
        };

    /// <summary>
    /// Reads the lambdas of one query. Every lambda must name its parameter alike: a condition kept as
    /// code is written back into a lambda whose parameter is the query's one name, where a different
    /// name would read a variable that is not there.
    /// </summary>
    sealed class ChainReader(SchemaIndex index, string model)
    {
        public string? Parameter { get; private set; }

        public string? Problem { get; private set; }

        /// <summary>The body of the one lambda the call takes, or null with <see cref="Problem"/> saying why.</summary>
        public ExpressionSyntax? Body(ArgumentListSyntax arguments)
        {
            var (parameter, body) = arguments.Arguments switch
            {
                [
                    {
                        NameColon: null,
                        Expression: SimpleLambdaExpressionSyntax
                        {
                            Modifiers.Count: 0,
                            ExpressionBody: { } expression
                        } simple
                    }
                ] => (simple.Parameter.Identifier.ValueText, expression),
                [
                    {
                        NameColon: null,
                        Expression: ParenthesizedLambdaExpressionSyntax
                        {
                            Modifiers.Count: 0,
                            ParameterList.Parameters: [{Type: null} only],
                            ExpressionBody: { } expression
                        }
                    }
                ] => (only.Identifier.ValueText, expression),
                _ => (null, null)
            };

            if (parameter is null ||
                body is null)
            {
                Problem = "The builder reads a lambda with one parameter and an expression for its body.";
                return null;
            }

            if (Parameter is not null &&
                Parameter != parameter)
            {
                Problem = "The query's lambdas name their parameter differently, which writing it back would not keep.";
                return null;
            }

            Parameter = parameter;
            return body;
        }

        /// <summary>
        /// A predicate's conditions: the operands of its top-level <c>&amp;&amp;</c>s, each a
        /// <see cref="ComparisonFilter"/> where it reads as one and kept as code where it does not.
        /// </summary>
        public IEnumerable<BuilderFilter> Filters(ExpressionSyntax predicate)
        {
            var conditions = new List<ExpressionSyntax>();
            Split(predicate, conditions);
            return conditions.Select(_ => (BuilderFilter?) Filter(_) ?? new CodeFilter(_.ToString()));
        }

        static void Split(ExpressionSyntax expression, List<ExpressionSyntax> conditions)
        {
            while (true)
            {
                // Parentheses around a run of && change nothing about it, so the run is read through.
                var inner = expression is ParenthesizedExpressionSyntax parenthesized && parenthesized.Expression.IsKind(SyntaxKind.LogicalAndExpression)
                    ? parenthesized.Expression
                    : expression;

                if (inner is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.LogicalAndExpression))
                {
                    Split(binary.Left, conditions);
                    expression = binary.Right;
                    continue;
                }

                conditions.Add(expression);
                break;
            }
        }

        ComparisonFilter? Filter(ExpressionSyntax condition)
        {
            switch (condition)
            {
                case PrefixUnaryExpressionSyntax negation when negation.IsKind(SyntaxKind.LogicalNotExpression):
                    if (Comparand(negation.Operand) is ({ } negated, {TypeDisplay: "bool"}))
                    {
                        return new(negated, FilterOperator.IsFalse, null);
                    }

                    return null;

                case BinaryExpressionSyntax binary when Comparison(binary.Kind()) is { } comparison:
                    // A value on the left reads as the same comparison turned around.
                    if (Comparand(binary.Left) is ({ } path, { } member))
                    {
                        return Compared(path, member, comparison, binary.Right);
                    }

                    if (Comparand(binary.Right) is ({ } flipped, { } turned))
                    {
                        return Compared(flipped, turned, Mirror(comparison), binary.Left);
                    }

                    return null;

                case InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax
                        {
                            Name: IdentifierNameSyntax method,
                            Expression: var target
                        },
                        ArgumentList.Arguments: [{NameColon: null, Expression: LiteralExpressionSyntax argument}]
                    } when argument.IsKind(SyntaxKind.StringLiteralExpression):
                    var operation = method.Identifier.ValueText switch
                    {
                        "Contains" => FilterOperator.Contains,
                        "StartsWith" => FilterOperator.StartsWith,
                        "EndsWith" => FilterOperator.EndsWith,
                        _ => (FilterOperator?) null
                    };

                    if (operation is not null &&
                        Comparand(target) is ({ } searched, { } text) &&
                        Kind(index, text.TypeDisplay) == ValueKind.Text)
                    {
                        return new(searched, operation.Value, argument.ToString());
                    }

                    return null;

                default:
                    if (Comparand(condition) is ({ } flagPath, {TypeDisplay: "bool"}))
                    {
                        return new(flagPath, FilterOperator.IsTrue, null);
                    }

                    return null;
            }
        }

        /// <summary>
        /// A comparison of a member against a value, when the value is a literal the member's input can
        /// show: against null where the member is nullable, against true or false where it is a bool,
        /// and against a literal of its kind otherwise.
        /// </summary>
        ComparisonFilter? Compared(IReadOnlyList<string> path, ScryMemberInfo member, FilterOperator comparison, ExpressionSyntax value)
        {
            var nullable = IsNullable(member.TypeDisplay);
            if (value.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return (comparison, nullable) switch
                {
                    (FilterOperator.Equal, true) => new(path, FilterOperator.IsNull, null),
                    (FilterOperator.NotEqual, true) => new(path, FilterOperator.IsNotNull, null),
                    _ => null
                };
            }

            if (Kind(index, member.TypeDisplay) == ValueKind.Boolean)
            {
                return (comparison, value.Kind()) switch
                {
                    (FilterOperator.Equal, SyntaxKind.TrueLiteralExpression) => new(path, FilterOperator.IsTrue, null),
                    (FilterOperator.Equal, SyntaxKind.FalseLiteralExpression) => new(path, FilterOperator.IsFalse, null),
                    _ => null
                };
            }

            var code = value.ToString();
            if (!Operators(index, member.TypeDisplay).Contains(comparison) ||
                Display(index, member.TypeDisplay, code) is null)
            {
                return null;
            }

            return new(path, comparison, code);
        }

        /// <summary>A sort key: a member path where it is one, the code it was written as otherwise.</summary>
        public BuilderOrder Order(ExpressionSyntax key, bool descending)
        {
            if (Comparand(key) is ({ } path, _))
            {
                return new(path, null, descending);
            }

            return new(null, key.ToString(), descending);
        }

        /// <summary>
        /// The members of an anonymous object at a depth of nesting: a scalar read through the same
        /// navigation as the object it sits in is a column, an object projected from one of the row's
        /// navigations is a nested object, and anything else is kept as code.
        /// </summary>
        public List<BuilderColumn> Columns(AnonymousObjectCreationExpressionSyntax anonymous, IReadOnlyList<string> prefix)
        {
            var columns = new List<BuilderColumn>(anonymous.Initializers.Count);
            foreach (var initializer in anonymous.Initializers)
            {
                if (initializer.NameEquals is null &&
                    Path(initializer.Expression) is { } path &&
                    path.Count == prefix.Count + 1 &&
                    path.Take(prefix.Count).SequenceEqual(prefix) &&
                    Member(index, model, path) is {IsNavigation: false, IsCollection: false, IsAttachment: false})
                {
                    columns.Add(new MemberColumn(path[^1]));
                    continue;
                }

                // Nested one level only, as the translator projects them; an object inside one is
                // kept as code, which is how it would be refused if it were run.
                if (initializer is
                    {
                        NameEquals.Name.Identifier.ValueText: var name,
                        Expression: AnonymousObjectCreationExpressionSyntax nested
                    } &&
                    prefix.Count == 0 &&
                    Find(index, model, name) is {IsNavigation: true})
                {
                    columns.Add(new NestedColumn(name, Columns(nested, [.. prefix, name])));
                    continue;
                }

                columns.Add(new CodeColumn(initializer.ToString()));
            }

            return columns;
        }

        /// <summary>A member a filter or sort key can name, with the path it was read through.</summary>
        (IReadOnlyList<string>? Path, ScryMemberInfo? Member) Comparand(ExpressionSyntax expression)
        {
            if (Path(expression) is not { } path ||
                Member(index, model, path) is not { } member ||
                Kind(index, member.TypeDisplay) == ValueKind.None)
            {
                return (null, null);
            }

            return (path, member);
        }

        /// <summary>
        /// The member names a chain of member accesses reads from the lambda's parameter, the null
        /// forgiving operators between them ignored. Null for anything else.
        /// </summary>
        List<string>? Path(ExpressionSyntax expression)
        {
            var names = new List<string>();
            while (true)
            {
                switch (expression)
                {
                    case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                        expression = suppressed.Operand;
                        continue;
                    case MemberAccessExpressionSyntax {Name: IdentifierNameSyntax name} access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                        names.Add(name.Identifier.ValueText);
                        expression = access.Expression;
                        continue;
                    case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == Parameter && names.Count > 0:
                        names.Reverse();
                        return names;
                    default:
                        return null;
                }
            }
        }

        static FilterOperator? Comparison(SyntaxKind kind) =>
            kind switch
            {
                SyntaxKind.EqualsExpression => FilterOperator.Equal,
                SyntaxKind.NotEqualsExpression => FilterOperator.NotEqual,
                SyntaxKind.LessThanExpression => FilterOperator.LessThan,
                SyntaxKind.LessThanOrEqualExpression => FilterOperator.LessThanOrEqual,
                SyntaxKind.GreaterThanExpression => FilterOperator.GreaterThan,
                SyntaxKind.GreaterThanOrEqualExpression => FilterOperator.GreaterThanOrEqual,
                _ => null
            };

        static FilterOperator Mirror(FilterOperator comparison) =>
            comparison switch
            {
                FilterOperator.LessThan => FilterOperator.GreaterThan,
                FilterOperator.LessThanOrEqual => FilterOperator.GreaterThanOrEqual,
                FilterOperator.GreaterThan => FilterOperator.LessThan,
                FilterOperator.GreaterThanOrEqual => FilterOperator.LessThanOrEqual,
                _ => comparison
            };
    }
}
