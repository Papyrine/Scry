namespace Scry;

/// <summary>
/// Formats a snippet the way the explorer writes one: the fluent chain down the page, and a projection
/// down the page after it.
/// </summary>
/// <remarks>
/// <para>
/// One printer serves both the button that formats what a caller typed and the starter query the
/// schema pane offers — the latter composes its query compactly and prints it through here, so the
/// generated shape and the formatted shape cannot drift apart.
/// </para>
/// <para>
/// A line is broken only where breaking it says something. The chain breaks because each operator is
/// a step, and a projection breaks because each member is a column; a predicate stays on one line,
/// because <c>_.Created >= since &amp;&amp; wanted.Contains(_.Name)</c> is one thought and stacking it
/// would not make it a clearer one.
/// </para>
/// </remarks>
public static class QueryPrinter
{
    const int step = 4;

    // LF rather than Environment.NewLine. The snippet lives in a Monaco model the explorer reads with
    // EndOfLinePreference.LF, and a printer whose output followed the host would have the schema pane
    // offer different bytes on Windows than on CI.
    const char newLine = '\n';

    /// <summary>
    /// Formats <paramref name="snippet"/>, or reports why it could not be. Anything that does not
    /// parse is reported rather than rewritten: the alternative is a button that quietly reformats a
    /// half-typed query into a differently half-typed one.
    /// </summary>
    public static bool TryFormat(string snippet, out string formatted, out string? error)
    {
        formatted = snippet;
        error = null;

        var layout = SnippetLayout.Of(snippet);
        if (layout.Problem is {IsError: true} problem)
        {
            error = problem.Message;
            return false;
        }

        var text = layout.Expression.Trim();
        if (text.Length == 0)
        {
            error = "There is no query to format.";
            return false;
        }

        // A trailing semicolon makes the query a statement rather than an expression. It is neither
        // required nor rejected, so it is set aside and put back rather than parsed around.
        var semicolon = text.EndsWith(';');
        if (semicolon)
        {
            text = text[..^1].TrimEnd();
        }

        var expression = SyntaxFactory.ParseExpression(text);
        if (expression.GetDiagnostics().Any(_ => _.Severity == DiagnosticSeverity.Error) ||
            // ParseExpression stops at the first token it cannot continue from and reports nothing
            // about the rest, so a query with trailing garbage parses "successfully" as its own prefix.
            expression.FullSpan.End != text.Length)
        {
            error = "The query could not be formatted because it does not parse.";
            return false;
        }

        var builder = new StringBuilder();
        AppendPreamble(builder, layout.Preamble);
        AppendExpression(builder, expression, 0);
        if (semicolon)
        {
            builder.Append(';');
        }

        formatted = builder.ToString();
        return true;
    }

    /// <summary>
    /// Formats <paramref name="snippet"/>, or returns it unchanged. For a caller composing a query it
    /// knows parses.
    /// </summary>
    public static string Format(string snippet)
    {
        if (TryFormat(snippet, out var formatted, out _))
        {
            return formatted;
        }

        return snippet;
    }

    // The declarations are kept as written — comments, blank lines and all. Reindenting them would be
    // rewriting a caller's own code to no purpose; the query is what this button is about. Only the
    // separation from the query is imposed.
    static void AppendPreamble(StringBuilder builder, string preamble)
    {
        var lines = preamble
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(_ => _.TrimEnd())
            .SkipWhile(_ => _.Length == 0)
            .ToList();

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return;
        }

        foreach (var line in lines)
        {
            builder.Append(line).Append(newLine);
        }

        builder.Append(newLine);
    }

    static void AppendExpression(StringBuilder builder, ExpressionSyntax expression, int indent)
    {
        var calls = new List<InvocationExpressionSyntax>();
        var root = expression;
        while (root is InvocationExpressionSyntax
               {
                   Expression: MemberAccessExpressionSyntax {Expression: var inner}
               } invocation)
        {
            calls.Add(invocation);
            root = inner;
        }

        calls.Reverse();

        builder.Append(' ', indent).Append(Inline(root));
        foreach (var call in calls)
        {
            builder.Append(newLine);
            AppendCall(builder, call, indent + step);
        }
    }

    static void AppendCall(StringBuilder builder, InvocationExpressionSyntax call, int indent)
    {
        var name = ((MemberAccessExpressionSyntax) call.Expression).Name;
        builder.Append(' ', indent).Append('.').Append(Inline(name));

        // The one argument shape worth breaking: a projection, whose members are the result's columns.
        var argumentList = call.ArgumentList;
        if (argumentList.Arguments is [
            {
                Expression: SimpleLambdaExpressionSyntax
                {
                    Body: AnonymousObjectCreationExpressionSyntax anonymous
                } lambda
            }])
        {
            builder.Append('(').Append(Inline(lambda.Parameter)).Append(" =>").Append(newLine);
            AppendAnonymous(builder, anonymous, indent + step);
            builder.Append(')');
            return;
        }

        builder.Append(Inline(argumentList));
    }

    static void AppendAnonymous(StringBuilder builder, AnonymousObjectCreationExpressionSyntax anonymous, int indent)
    {
        builder.Append(' ', indent).Append("new").Append(newLine);
        builder.Append(' ', indent).Append('{').Append(newLine);

        for (var index = 0; index < anonymous.Initializers.Count; index++)
        {
            var member = anonymous.Initializers[index];
            var last = index == anonymous.Initializers.Count - 1;

            // A member projecting into a navigation is another object, and it breaks the same way the
            // one containing it did.
            if (member is
                {
                    NameEquals: { } nameEquals,
                    Expression: AnonymousObjectCreationExpressionSyntax nested
                })
            {
                builder.Append(' ', indent + step).Append(Inline(nameEquals.Name)).Append(" =").Append(newLine);
                AppendAnonymous(builder, nested, indent + step + step);
            }
            else
            {
                builder.Append(' ', indent + step).Append(Inline(member));
            }

            if (!last)
            {
                builder.Append(',');
            }

            builder.Append(newLine);
        }

        builder.Append(' ', indent).Append('}');
    }

    // Whatever this node is, on one line. NormalizeWhitespace collapses however it was written; the
    // join then flattens the few constructs it still spreads over lines.
    static string Inline(SyntaxNode node) =>
        string.Join(
            ' ',
            node.NormalizeWhitespace()
                .ToFullString()
                .ReplaceLineEndings("\n")
                .Split('\n')
                .Select(_ => _.Trim())
                .Where(_ => _.Length > 0));
}
