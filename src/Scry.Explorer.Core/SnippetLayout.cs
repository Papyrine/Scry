/// <summary>
/// Splits a snippet into the variables declared ahead of the query and the query expression that
/// reads them.
/// </summary>
/// <remarks>
/// Both halves of the explorer read a snippet through this — the workspace behind the editor splices
/// it into a method body to complete and diagnose against, and <see cref="SnippetExecutor"/> splices
/// it into the method it compiles and runs. One split rule, so a snippet cannot be squiggled at one
/// boundary and compiled at another.
/// </remarks>
sealed record SnippetLayout(string Code, int Split, ScryDiagnostic? Problem)
{
    /// <summary>
    /// The declarations ahead of the query, verbatim — separators, blank lines, and comments and all.
    /// Empty for a snippet that is only a query.
    /// </summary>
    public string Preamble => Code[..Split];

    /// <summary>The query itself: its first token through to the end of the snippet.</summary>
    public string Expression => Code[Split..];

    public static SnippetLayout Of(string code)
    {
        // Parsed rather than scanned for a ';'. A semicolon inside a string literal ends no statement,
        // and only the parser knows the difference. The brace makes the snippet a block — the one
        // statement a list can be read out of — so every span reported sits one past the snippet's own.
        if (SyntaxFactory.ParseStatement($"{{{code}\n}}") is not BlockSyntax block ||
            block.Statements.Count < 2)
        {
            return new(code, 0, null);
        }

        var split = block.Statements[^1].SpanStart - 1;
        if (split <= 0 ||
            split > code.Length)
        {
            return new(code, 0, null);
        }

        for (var index = 0; index < block.Statements.Count - 1; index++)
        {
            var statement = block.Statements[index];
            if (statement is LocalDeclarationStatementSyntax)
            {
                continue;
            }

            // A declaration is the whole of what the preamble is for: the query reads it as captured
            // state and the translator folds it into the constant it stands for, so nothing written
            // there reaches the wire on its own. Anything else would run in the browser without
            // changing the request it produced, and the snippet would stop reading as the two things
            // it is — the values, then the query that reads them.
            //
            // A rule about the snippet's shape, not a boundary around what the browser runs: an
            // initializer is ordinary code, evaluated here exactly as a compiled client would evaluate
            // it, and one that never returns takes the page with it just as a statement would have.
            return new(
                code,
                split,
                new(
                    "Only a variable declaration can come before the query.",
                    Math.Clamp(statement.SpanStart - 1, 0, code.Length),
                    Math.Clamp(statement.Span.End - 1, 0, code.Length),
                    IsError: true));
        }

        return new(code, split, null);
    }
}
