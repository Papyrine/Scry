// The callable surface: every method call that becomes a wire function, and the refusal for one that
// cannot.
sealed partial class QueryTranslator
{
    Node TranslateMethod(MethodCallExpression call, ParameterExpression root)
    {
        var declaring = call.Method.DeclaringType;

        // Reads a value as text, whatever its type. Only the argument-less form: an overload taking a
        // format is refused, since no provider translates it — see the note on StringFrom.
        if (call is {Method.Name: "ToString", Object: { } instance, Arguments.Count: 0})
        {
            return new CallNode(KnownFunction.StringFrom, TranslateExpr(instance, root), []);
        }

        // Convert.ToString is the same read spelled statically — checked before the format refusal
        // below, whose pattern its one argument would otherwise match.
        if (call is {Method.Name: "ToString", Object: null, Arguments: [var toText]} &&
            declaring == typeof(Convert) &&
            ReferencesParameter(call, root))
        {
            return new CallNode(KnownFunction.StringFrom, TranslateExpr(toText, root), []);
        }

        if (call is {Method.Name: "ToString", Arguments.Count: > 0})
        {
            throw new NotSupportedException(
                "ToString with a format is not supported by Scry. No provider translates it, and the SQL function that would express it reads the server's language, so the same row would format differently per connection. Format the value after the query returns.");
        }

        // The three-way comparison, over the types the server compares. Only the IComparable<T> shape:
        // the object overload would hide the operand type the server reconciles the constant against.
        if (call is {Method.Name: "CompareTo", Object: { } compared, Arguments.Count: 1} &&
            declaring is not null &&
            call.Method.GetParameters()[0].ParameterType == declaring &&
            IsThreeWayComparable(declaring) &&
            ReferencesParameter(call, root))
        {
            return new CallNode(KnownFunction.CompareTo, TranslateExpr(compared, root), [TranslateExpr(call.Arguments[0], root)]);
        }

        // Equals is == spelled as a method: the same comparison, over the same operands, refused by the
        // same rules when either is not a value. The overloads taking a StringComparison are not this
        // — they ask for a case sensitivity, which the string path reads as a collation — so they are
        // left for it.
        if (call.Method.Name == "Equals" &&
            !TakesComparison(call) &&
            EqualityOperands(call) is (var equated, var against))
        {
            // One that reads nothing from the row is closure state, evaluated here as any other
            // constant expression is — the string dispatch below would otherwise reach it first and
            // refuse it for having no function to become.
            return ReferencesParameter(call, root)
                ? new BinaryNode(BinaryOp.Equal, TranslateExpr(equated, root), TranslateExpr(against, root))
                : ConstantOf(Evaluate(call));
        }

        if (declaring == typeof(string))
        {
            return TranslateStringMethod(call, root);
        }

        // GetValueOrDefault abbreviates the coalesce it stands for: the value, or — with no
        // argument — the type's default, which travels as an ordinary constant.
        if (call is {Method.Name: "GetValueOrDefault", Object: { } optional} &&
            Nullable.GetUnderlyingType(optional.Type) is { } underlying &&
            ReferencesParameter(call, root))
        {
            var fallback = call.Arguments.Count == 1
                ? TranslateExpr(call.Arguments[0], root)
                : ConstantOf(Activator.CreateInstance(underlying));
            return new BinaryNode(BinaryOp.Coalesce, TranslateExpr(optional, root), fallback);
        }

        // HasFlag reads the row's enum member; the flag travels as an ordinary enum constant, a
        // combined value spelled the way Enum.ToString spells it.
        if (call is {Method.Name: "HasFlag", Object: { } flagged} &&
            declaring == typeof(Enum) &&
            ReferencesParameter(call, root))
        {
            return new CallNode(KnownFunction.EnumHasFlag, TranslateExpr(flagged, root), [TranslateExpr(call.Arguments[0], root)]);
        }

        // Parse, and Convert's To* forms, read text as a value — the inverse of StringFrom. Only that
        // direction is carried: a numeric member is already a value, which arithmetic and comparison
        // promote without a cast, and SQL's numeric conversions truncate where the CLR's round.
        if (call is {Object: null, Arguments: [var text]} &&
            declaring is not null &&
            ReferencesParameter(call, root))
        {
            var conversion = call.Method.Name == "Parse" && parseTargets.TryGetValue(declaring, out var byType)
                ? byType
                : declaring == typeof(Convert) && convertTargets.TryGetValue(call.Method.Name, out var byName)
                    ? byName
                    : (KnownFunction?)null;

            if (conversion is { } function)
            {
                if (text.Type != typeof(string))
                {
                    throw new NotSupportedException(
                        $"'{declaring.Name}.{call.Method.Name}' reads text as a value, and '{text.Type.Name}' is already one — arithmetic and comparison promote a numeric member without a cast.");
                }

                return new CallNode(function, TranslateExpr(text, root), []);
            }
        }

        // The statics that read one temporal type as another. Each takes the value being read as its
        // argument, so the wire's target is that argument rather than an instance.
        if (IsTemporal(declaring) &&
            call is {Object: null, Arguments: [var read]} &&
            ReferencesParameter(call, root))
        {
            var conversion = call.Method.Name switch
            {
                "FromDateTime" when declaring == typeof(Date) => KnownFunction.DateOnlyFromDateTime,
                "FromDateTime" when declaring == typeof(Time) => KnownFunction.TimeOnlyFromDateTime,
                "FromTimeSpan" when declaring == typeof(Time) => KnownFunction.TimeOnlyFromTimeSpan,
                _ => (KnownFunction?)null
            };

            if (conversion is { } function)
            {
                return new CallNode(function, TranslateExpr(read, root), []);
            }
        }

        // The Unix-time readings, which are argument-less instance methods on an offset.
        if (declaring == typeof(DateTimeOffset) &&
            call is {Object: { } stamped, Arguments.Count: 0, Method.Name: "ToUnixTimeSeconds" or "ToUnixTimeMilliseconds"})
        {
            var unix = call.Method.Name == "ToUnixTimeSeconds"
                ? KnownFunction.UnixSecondsFromOffset
                : KnownFunction.UnixMillisecondsFromOffset;
            return new CallNode(unix, TranslateExpr(stamped, root), []);
        }

        // A date and a time composed back into one timestamp.
        if (declaring == typeof(Date) &&
            call is {Method.Name: "ToDateTime", Object: { } dated, Arguments: [var timed]})
        {
            return new CallNode(KnownFunction.DateTimeFromDateAndTime, TranslateExpr(dated, root), [TranslateExpr(timed, root)]);
        }

        if (IsTemporal(declaring))
        {
            var added = call.Method.Name switch
            {
                "AddYears" => KnownFunction.DateAddYears,
                "AddMonths" => KnownFunction.DateAddMonths,
                "AddDays" => KnownFunction.DateAddDays,
                "AddHours" => KnownFunction.DateAddHours,
                "AddMinutes" => KnownFunction.DateAddMinutes,
                "AddSeconds" => KnownFunction.DateAddSeconds,
                "AddMilliseconds" => KnownFunction.DateAddMilliseconds,
                _ => throw Unsupported(call)
            };
            return new CallNode(added, TranslateExpr(call.Object!, root), [TranslateExpr(call.Arguments[0], root)]);
        }

        // The angle conversions are statics on the floating types rather than on Math, but they are
        // math functions all the same.
        if ((declaring == typeof(double) || declaring == typeof(float)) &&
            call is {
                Object: null,
                Arguments.Count: 1,
                Method.Name: "DegreesToRadians" or "RadiansToDegrees"} &&
            ReferencesParameter(call, root))
        {
            var angle = call.Method.Name == "DegreesToRadians"
                ? KnownFunction.MathDegreesToRadians
                : KnownFunction.MathRadiansToDegrees;
            return new CallNode(angle, TranslateExpr(call.Arguments[0], root), []);
        }

        if (declaring == typeof(Math))
        {
            var math = call.Method.Name switch
            {
                "Abs" => KnownFunction.MathAbs,
                "Ceiling" => KnownFunction.MathCeiling,
                "Floor" => KnownFunction.MathFloor,
                "Round" => KnownFunction.MathRound,
                "Truncate" => KnownFunction.MathTruncate,
                "Sign" => KnownFunction.MathSign,
                "Sqrt" => KnownFunction.MathSqrt,
                "Pow" => KnownFunction.MathPow,
                "Exp" => KnownFunction.MathExp,
                "Log" => KnownFunction.MathLog,
                "Log10" => KnownFunction.MathLog10,
                "Sin" => KnownFunction.MathSin,
                "Cos" => KnownFunction.MathCos,
                "Tan" => KnownFunction.MathTan,
                "Asin" => KnownFunction.MathAsin,
                "Acos" => KnownFunction.MathAcos,
                "Atan" => KnownFunction.MathAtan,
                "Atan2" => KnownFunction.MathAtan2,
                "Max" => KnownFunction.MathMax,
                "Min" => KnownFunction.MathMin,
                _ => throw Unsupported(call)
            };

            // The two-operand forms — Round(value, digits), Pow(value, exponent), Log(value, base),
            // Atan2(y, x) — carry their second operand as the one argument; the rest take none.
            var arguments = call.Arguments.Count > 1
                ? new[] { TranslateExpr(call.Arguments[1], root) }
                : [];
            return new CallNode(math, TranslateExpr(call.Arguments[0], root), arguments);
        }

        // Text and binary answer a handful of Enumerable's questions without ever yielding their
        // elements — the first character, the byte at a position, whether there are any bytes at all.
        // Checked before the collection forms below, which read a navigation rather than a scalar.
        if (TrySequenceRead(call, root) is { } sequence)
        {
            return sequence;
        }

        // _.Orders.Any(o => …) — a question about a collection navigation, which the server evaluates
        // as a correlated subquery. Checked before the set-membership form below, whose Contains reads
        // a closure collection rather than one belonging to the row.
        if (TrySubquery(call, root) is { } subquery)
        {
            return subquery;
        }

        // Query.Department.Select(_ => _.Name).Contains(_.Name) — membership of a set drawn from
        // another source, which the server resolves and policy-filters before the test.
        if (TryInSource(call, root) is { } inSource)
        {
            return inSource;
        }

        // ids.Contains(_.Id) — membership of a client-side set, which becomes a SQL IN. The set must be
        // closure state (evaluated here into constants) and the tested value must come from the row.
        if (IsSetContains(call, root, out var set, out var value))
        {
            return new CallNode(KnownFunction.In, TranslateExpr(value, root), [..SetConstants(set)]);
        }

        // A call that does not touch the parameter is a closure value — evaluate it.
        if (!ReferencesParameter(call, root))
        {
            return ConstantOf(Evaluate(call));
        }

        // The call reads the row, so it cannot be evaluated into a constant — and it is not on the
        // callable surface, so there is nothing on the wire to carry it. Named in full: this is the
        // only reporter for a query the analyzer could not see into.
        var name = declaring is null
            ? call.Method.Name
            : $"{declaring.Name}.{call.Method.Name}";
        throw new NotSupportedException(
            $"'{name}' is client-side code, which cannot be carried on the wire — the callable set is closed. Evaluate it before the query, or apply it to the rows after they return.");
    }

    /// <summary>
    /// The questions a string or a binary member answers as a sequence. Both are scalars on the wire —
    /// neither ever yields its elements — so each of these folds to a single value, and the ones with
    /// no such folding (any predicate overload, anything returning a sequence) are left to fail as the
    /// client-side code they are.
    /// </summary>
    Node? TrySequenceRead(MethodCallExpression call, ParameterExpression root)
    {
        // MemoryExtensions sits alongside Enumerable here because the compiler prefers its span
        // overload for a byte[]'s Contains. The two spell one question, and the server rebinds either
        // onto the Enumerable form the provider translates.
        if (call is not {Object: null, Arguments: [var source, ..]} ||
            (call.Method.DeclaringType != typeof(Enumerable) &&
             call.Method.DeclaringType != typeof(MemoryExtensions)) ||
            !ReferencesParameter(source, root))
        {
            return null;
        }

        // MemoryExtensions takes a span, so the array reaches it through a conversion — spelled as a
        // call to the implicit operator rather than as a Convert node, since a ref struct is not a
        // type the tree can convert to on its own. The question is about what was converted, and the
        // wire carries that member rather than the span.
        source = Unconverted(source);

        if (source.Type == typeof(string))
        {
            var text = call.Method.Name switch
            {
                "FirstOrDefault" when call.Arguments.Count == 1 => KnownFunction.StringFirst,
                "LastOrDefault" when call.Arguments.Count == 1 => KnownFunction.StringLast,
                _ => (KnownFunction?)null
            };

            return text is { } reading
                ? new CallNode(reading, TranslateExpr(source, root), [])
                : null;
        }

        if (source.Type != typeof(byte[]))
        {
            return null;
        }

        // First is ElementAt at position zero, so it travels as that rather than as a function of its
        // own — the same unfolding the terminals use for ElementAtAsync.
        return call.Method.Name switch
        {
            "First" when call.Arguments.Count == 1 =>
                new CallNode(KnownFunction.BytesElementAt, TranslateExpr(source, root), [ConstantOf(0)]),
            "ElementAt" when call.Arguments is [_, var at] =>
                new CallNode(KnownFunction.BytesElementAt, TranslateExpr(source, root), [TranslateExpr(at, root)]),
            "Contains" when call.Arguments is [_, var value] =>
                new CallNode(KnownFunction.BytesContains, TranslateExpr(source, root), [TranslateExpr(value, root)]),
            _ => null
        };
    }

    // The value under any conversions wrapping it, whichever way the compiler spelled them: a Convert
    // node, or a call to a conversion operator where the target is a type the tree cannot convert to.
    static Expression Unconverted(Expression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} converted:
                    expression = converted.Operand;
                    continue;

                case MethodCallExpression
                {
                    Object: null,
                    Method.Name: "op_Implicit" or "op_Explicit",
                    Arguments: [var operand]
                }:
                    expression = operand;
                    continue;

                default:
                    return expression;
            }
        }
    }

    // The Parse owners and Convert members the text-reading functions answer for, by target type.
    static readonly Dictionary<Type, KnownFunction> parseTargets = new()
    {
        [typeof(int)] = KnownFunction.Int32From,
        [typeof(long)] = KnownFunction.Int64From,
        [typeof(decimal)] = KnownFunction.DecimalFrom,
        [typeof(double)] = KnownFunction.DoubleFrom,
        [typeof(bool)] = KnownFunction.BooleanFrom,
        [typeof(byte)] = KnownFunction.ByteFrom,
        [typeof(short)] = KnownFunction.Int16From,
        [typeof(float)] = KnownFunction.SingleFrom
    };

    // ToSingle is deliberately absent: the provider translates float.Parse but has no ToSingle
    // conversion, so carrying the spelling would trade a translation-time refusal for an execution
    // fault.
    static readonly Dictionary<string, KnownFunction> convertTargets = new(StringComparer.Ordinal)
    {
        ["ToInt32"] = KnownFunction.Int32From,
        ["ToInt64"] = KnownFunction.Int64From,
        ["ToDecimal"] = KnownFunction.DecimalFrom,
        ["ToDouble"] = KnownFunction.DoubleFrom,
        ["ToBoolean"] = KnownFunction.BooleanFrom,
        ["ToByte"] = KnownFunction.ByteFrom,
        ["ToInt16"] = KnownFunction.Int16From
    };
}
