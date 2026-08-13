using System.Collections.ObjectModel;

// The text functions, the case sensitivity a comparison may ask for, and the concatenation an
// interpolated string is rewritten into.
sealed partial class QueryTranslator
{
    Node TranslateStringMethod(MethodCallExpression call, ParameterExpression root)
    {
        Node Target() => TranslateExpr(call.Object!, root);

        Node Argument(int index) => TranslateExpr(call.Arguments[index], root);

        // The StringComparison overloads ask for a case sensitivity rather than a different operation,
        // so the target is read under it and the ordinary function applies on top. Equals also has a
        // static spelling, which puts the target in the first argument rather than in the instance.
        if (TakesComparison(call) &&
            call.Arguments.Count == (call.Object is null ? 3 : 2) &&
            call.Method.Name is "Contains" or "StartsWith" or "EndsWith" or "Equals")
        {
            var function = call.Method.Name switch
            {
                "Contains" => KnownFunction.StringContains,
                "StartsWith" => KnownFunction.StringStartsWith,
                "EndsWith" => KnownFunction.StringEndsWith,
                _ => (KnownFunction?)null
            };

            var (compared, operand) = call.Object is { } instance
                ? (instance, call.Arguments[0])
                : (call.Arguments[0], call.Arguments[1]);

            var collated = new CollateNode(TranslateExpr(compared, root), Sensitivity(call.Arguments[^1]));

            // Equals is a comparison rather than a function; under a collation it is an ordinary one.
            return function is null
                ? new BinaryNode(BinaryOp.Equal, collated, TranslateExpr(operand, root))
                : new CallNode(function.Value, collated, [TranslateExpr(operand, root)]);
        }

        switch (call.Method.Name)
        {
            case "Contains" when call.Arguments.Count == 1:
                return new CallNode(KnownFunction.StringContains, Target(), [Argument(0)]);
            case "StartsWith" when call.Arguments.Count == 1:
                return new CallNode(KnownFunction.StringStartsWith, Target(), [Argument(0)]);
            case "EndsWith" when call.Arguments.Count == 1:
                return new CallNode(KnownFunction.StringEndsWith, Target(), [Argument(0)]);
            case "ToLower" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringToLower, Target(), []);
            case "ToUpper" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringToUpper, Target(), []);
            // The static spelling of the instance CompareTo handled before this switch.
            case "Compare" when call.Arguments.Count == 2:
                return new CallNode(KnownFunction.CompareTo, Argument(0), [Argument(1)]);

            case "IsNullOrEmpty":
                return new CallNode(KnownFunction.StringIsNullOrEmpty, Argument(0), []);
            case "IsNullOrWhiteSpace":
                return new CallNode(KnownFunction.StringIsNullOrWhiteSpace, Argument(0), []);

            // The char-set overloads (Trim(params char[])) have no SQL equivalent — only the
            // whitespace-trimming forms translate.
            case "Trim" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringTrim, Target(), []);
            case "TrimStart" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringTrimStart, Target(), []);
            case "TrimEnd" when call.Arguments.Count == 0:
                return new CallNode(KnownFunction.StringTrimEnd, Target(), []);

            case "Substring" when call.Arguments.Count is 1 or 2:
                return new CallNode(KnownFunction.StringSubstring, Target(), [..call.Arguments.Select(_ => TranslateExpr(_, root))]);
            case "IndexOf" when call.Arguments.Count == 1:
                return new CallNode(KnownFunction.StringIndexOf, Target(), [Argument(0)]);
            case "Replace" when call.Arguments.Count == 2:
                return new CallNode(KnownFunction.StringReplace, Target(), [Argument(0), Argument(1)]);

            case "Concat":
                return ConcatChain([..ConcatArguments(call).Select(_ => TranslateExpr(_, root))]);

            // An interpolated string lowers to string.Format inside an expression tree, which no
            // provider translates. Plain holes carry no formatting, so they mean the same as a
            // concatenation and are rewritten into one.
            case "Format" when call.Arguments is [ConstantExpression {Value: string format}, ..]:
                return ConcatChain(Interpolation(format, [..ConcatArguments(call).Skip(1)], root));

            case "Format":
                throw new NotSupportedException("Only an interpolated string with a literal format is supported.");

            default:
                throw Unsupported(call);
        }
    }

    /// <summary>
    /// Reads the case sensitivity a <see cref="StringComparison"/> asks for. Only that much of it
    /// survives: the comparison the database then makes is its own, under the collation the server
    /// configured, which is not the culture rules the .NET value names.
    /// </summary>
    static StringMatch Sensitivity(Expression comparison)
    {
        if (Evaluate(comparison) is not StringComparison value)
        {
            throw new NotSupportedException("A string comparison mode must be a constant.");
        }

        return value switch
        {
            StringComparison.Ordinal or
                StringComparison.CurrentCulture or
                StringComparison.InvariantCulture => StringMatch.CaseSensitive,
            StringComparison.OrdinalIgnoreCase or
                StringComparison.CurrentCultureIgnoreCase or
                StringComparison.InvariantCultureIgnoreCase => StringMatch.CaseInsensitive,
            _ => throw new NotSupportedException($"String comparison '{value}' is not supported by Scry.")
        };
    }

    // Whether the call's last argument names a case sensitivity rather than an operand.
    static bool TakesComparison(MethodCallExpression call) =>
        call.Arguments.Count > 0 &&
        call.Arguments[^1].Type == typeof(StringComparison);

    // The params overloads pass their arguments as a single constructed array.
    static IReadOnlyList<Expression> ConcatArguments(MethodCallExpression call)
    {
        if (call.Arguments is [NewArrayExpression array])
        {
            return array.Expressions;
        }

        return (ReadOnlyCollection<Expression>) [.. call.Arguments];
    }

    /// <summary>
    /// Splits a format string into its literal runs and holes. A hole carrying alignment or a format
    /// specifier is refused: it would change the value, and the database has no equivalent spelling.
    /// </summary>
    List<Node> Interpolation(string format, IReadOnlyList<Expression> arguments, ParameterExpression root)
    {
        var parts = new List<Node>();
        var literal = new StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            var character = format[i];

            // Doubled braces are an escaped literal brace.
            if (character is '{' or '}' &&
                i + 1 < format.Length &&
                format[i + 1] == character)
            {
                literal.Append(character);
                i++;
                continue;
            }

            if (character != '{')
            {
                literal.Append(character);
                continue;
            }

            var close = format.IndexOf('}', i);
            var hole = close < 0 ? "" : format[(i + 1)..close];
            if (close < 0 ||
                !int.TryParse(hole, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                index >= arguments.Count)
            {
                throw new NotSupportedException(
                    "An interpolated string may only contain plain holes — alignment and format specifiers are not supported.");
            }

            if (literal.Length > 0)
            {
                parts.Add(ConstantOf(literal.ToString()));
                literal.Clear();
            }

            parts.Add(TranslateExpr(arguments[index], root));
            i = close;
        }

        if (literal.Length > 0)
        {
            parts.Add(ConstantOf(literal.ToString()));
        }

        return parts;
    }

    static Node ConcatChain(IReadOnlyList<Node> parts)
    {
        if (parts.Count == 0)
        {
            throw new NotSupportedException("A concatenation must have at least one part.");
        }

        var chain = parts[0];
        foreach (var part in parts.Skip(1))
        {
            chain = new CallNode(KnownFunction.StringConcat, chain, [part]);
        }

        return chain;
    }
}
