// The callable surface and the literals: every KnownFunction spelled back as the call that captures
// to it, and every ConstNode spelled back as the C# expression ValueTag reads to the same bytes.
partial class QueryRenderer
{
    string RenderCall(CallNode call, Scope scope)
    {
        var arguments = call.Arguments;

        string Target() => this.Target(call.Target, scope);

        // A value-typed receiver whose member is unlifted in C# gets its .Value back — the forward
        // pass strips it. Needing this without a model to ask is a resolution failure.
        string ValueTarget() => RenderValue(call.Target, scope);

        string Argument(int index) => RenderNode(arguments[index], scope);

        string ValueArgument(int index) => RenderValue(arguments[index], scope);

        string From(string keyword)
        {
            var type = InferType(call.Target, scope);
            if (type is null ||
                (Nullable.GetUnderlyingType(type) ?? type) == typeof(string))
            {
                return $"{keyword}.Parse({RenderNode(call.Target, scope)})";
            }

            var cast = Nullable.GetUnderlyingType(type) is null ? keyword : $"{keyword}?";
            var operand = RenderNode(call.Target, scope);
            return call.Target is BinaryNode or ConditionalNode
                ? $"({cast})({operand})"
                : $"({cast}){operand}";
        }

        switch (call.Function)
        {
            case KnownFunction.StringContains or KnownFunction.StringStartsWith or KnownFunction.StringEndsWith:
            {
                if (arguments.Count != 1)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                var name = call.Function switch
                {
                    KnownFunction.StringContains => "Contains",
                    KnownFunction.StringStartsWith => "StartsWith",
                    _ => "EndsWith"
                };

                var argument = arguments[0] is ConstNode text
                    ? RenderConst(text, typeof(string))
                    : Argument(0);

                if (call.Target is CollateNode collate)
                {
                    return $"{this.Target(collate.Target, scope)}.{name}({argument}, {Comparison(collate.Match)})";
                }

                return $"{Target()}.{name}({argument})";
            }

            case KnownFunction.StringToLower:
                return $"{Target()}.ToLower()";
            case KnownFunction.StringToUpper:
                return $"{Target()}.ToUpper()";
            case KnownFunction.StringTrim:
                return $"{Target()}.Trim()";
            case KnownFunction.StringTrimStart:
                return $"{Target()}.TrimStart()";
            case KnownFunction.StringTrimEnd:
                return $"{Target()}.TrimEnd()";

            case KnownFunction.StringIsNullOrEmpty:
                return $"string.IsNullOrEmpty({RenderNode(call.Target, scope)})";
            case KnownFunction.StringIsNullOrWhiteSpace:
                return $"string.IsNullOrWhiteSpace({RenderNode(call.Target, scope)})";

            case KnownFunction.StringLength:
                return $"{Target()}.Length";

            case KnownFunction.StringSubstring:
                return arguments.Count switch
                {
                    1 => $"{Target()}.Substring({Argument(0)})",
                    2 => $"{Target()}.Substring({Argument(0)}, {Argument(1)})",
                    _ => throw Refuse(RenderRefusal.UnsupportedShape)
                };

            case KnownFunction.StringIndexOf:
                return $"{Target()}.IndexOf({Argument(0)})";
            case KnownFunction.StringReplace:
                return $"{Target()}.Replace({Argument(0)}, {Argument(1)})";

            case KnownFunction.StringFirst:
                return $"{Target()}.FirstOrDefault()";
            case KnownFunction.StringLast:
                return $"{Target()}.LastOrDefault()";

            // The left-folded chain flattens back into the one static spelling that captures to the
            // identical fold whatever the operand types are.
            case KnownFunction.StringConcat:
            {
                var parts = new List<Node>();
                FlattenConcat(call, parts);
                return $"string.Concat({string.Join(", ", parts.Select(_ => RenderNode(_, scope)))})";
            }

            case KnownFunction.StringFrom:
                return $"{Target()}.ToString()";

            case KnownFunction.DateYear or
                KnownFunction.DateMonth or
                KnownFunction.DateDay or
                KnownFunction.DateHour or
                KnownFunction.DateMinute or
                KnownFunction.DateSecond or
                KnownFunction.DateMillisecond or
                KnownFunction.DateMicrosecond or
                KnownFunction.DateNanosecond or
                KnownFunction.DateDayOfYear or
                KnownFunction.DateDayNumber or
                KnownFunction.DateDayOfWeek or
                KnownFunction.DateDate or
                KnownFunction.DateTimeOfDay:
            {
                var name = call.Function switch
                {
                    KnownFunction.DateDayOfYear => "DayOfYear",
                    KnownFunction.DateDayNumber => "DayNumber",
                    KnownFunction.DateDayOfWeek => "DayOfWeek",
                    KnownFunction.DateTimeOfDay => "TimeOfDay",
                    _ => call.Function.ToString()["Date".Length..]
                };
                return $"{ValueTarget()}.{name}";
            }

            case KnownFunction.TimeSpanHours or
                KnownFunction.TimeSpanMinutes or
                KnownFunction.TimeSpanSeconds or
                KnownFunction.TimeSpanMilliseconds or
                KnownFunction.TimeSpanMicroseconds or
                KnownFunction.TimeSpanNanoseconds:
                return $"{ValueTarget()}.{call.Function.ToString()["TimeSpan".Length..]}";

            case KnownFunction.DateAddYears or
                KnownFunction.DateAddMonths or
                KnownFunction.DateAddDays or
                KnownFunction.DateAddHours or
                KnownFunction.DateAddMinutes or
                KnownFunction.DateAddSeconds or
                KnownFunction.DateAddMilliseconds:
                return $"{ValueTarget()}.{call.Function.ToString()["Date".Length..]}({ValueArgument(0)})";

            case KnownFunction.DateOnlyFromDateTime:
                return $"DateOnly.FromDateTime({ValueTarget()})";
            case KnownFunction.TimeOnlyFromDateTime:
                return $"TimeOnly.FromDateTime({ValueTarget()})";
            case KnownFunction.TimeOnlyFromTimeSpan:
                return $"TimeOnly.FromTimeSpan({ValueTarget()})";
            case KnownFunction.DateTimeFromDateAndTime:
            {
                // The time of day travels under the String tag, having none of its own, so rendering
                // it needs the type it composes with — without that it spells as text, which is not
                // what ToDateTime takes.
                var moment = arguments is [ConstNode time, ..]
                    ? RenderConst(time, typeof(Time))
                    : ValueArgument(0);
                return $"{ValueTarget()}.ToDateTime({moment})";
            }

            case KnownFunction.UnixSecondsFromOffset:
                return $"{ValueTarget()}.ToUnixTimeSeconds()";
            case KnownFunction.UnixMillisecondsFromOffset:
                return $"{ValueTarget()}.ToUnixTimeMilliseconds()";

            case KnownFunction.MathDegreesToRadians:
                return $"double.DegreesToRadians({ValueTarget()})";
            case KnownFunction.MathRadiansToDegrees:
                return $"double.RadiansToDegrees({ValueTarget()})";

            case KnownFunction.MathAbs or
                KnownFunction.MathCeiling or
                KnownFunction.MathFloor or
                KnownFunction.MathRound or
                KnownFunction.MathTruncate or
                KnownFunction.MathSign or
                KnownFunction.MathSqrt or
                KnownFunction.MathPow or
                KnownFunction.MathExp or
                KnownFunction.MathLog or
                KnownFunction.MathLog10 or
                KnownFunction.MathSin or
                KnownFunction.MathCos or
                KnownFunction.MathTan or
                KnownFunction.MathAsin or
                KnownFunction.MathAcos or
                KnownFunction.MathAtan or
                KnownFunction.MathAtan2 or
                KnownFunction.MathMax or
                KnownFunction.MathMin:
            {
                var name = call.Function.ToString()["Math".Length..];
                return arguments.Count switch
                {
                    0 => $"Math.{name}({ValueTarget()})",
                    1 => $"Math.{name}({ValueTarget()}, {ValueArgument(0)})",
                    _ => throw Refuse(RenderRefusal.UnsupportedShape)
                };
            }

            case KnownFunction.In:
                return RenderIn(call, scope);

            case KnownFunction.EnumHasFlag:
            {
                if (arguments is not [ConstNode flag])
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                var enumType = InferType(call.Target, scope);
                return $"{ValueTarget()}.HasFlag({RenderConst(flag, enumType)})";
            }

            // Over text these parse it; over a number they are the widening cast the client wrote,
            // and read back as one — lifted where the member is optional, since that is how C# spells
            // a conversion of a nullable.
            case KnownFunction.Int32From:
                return From("int");
            case KnownFunction.Int64From:
                return From("long");
            case KnownFunction.DecimalFrom:
                return From("decimal");
            case KnownFunction.DoubleFrom:
                return From("double");
            case KnownFunction.BooleanFrom:
                return From("bool");
            case KnownFunction.ByteFrom:
                return From("byte");
            case KnownFunction.Int16From:
                return From("short");
            case KnownFunction.SingleFrom:
                return From("float");

            case KnownFunction.CompareTo:
            {
                var argument = arguments[0] is ConstNode compared
                    ? RenderConst(compared, InferType(call.Target, scope))
                    : Argument(0);
                return $"{ValueTarget()}.CompareTo({argument})";
            }

            case KnownFunction.BytesLength:
                return $"{Target()}.Length";

            case KnownFunction.BytesContains:
            {
                // The byte the wire compares travels as its code point, and the snippet has to hand
                // the overload a byte again for the same capture to happen.
                var value = arguments[0] is ConstNode {Tag: ClrTypeTag.Int32, Value: { } number}
                    ? $"(byte){number}"
                    : Argument(0);
                return $"{Target()}.Contains({value})";
            }

            case KnownFunction.BytesElementAt:
                return $"{Target()}.ElementAt({Argument(0)})";

            default:
                throw Refuse(RenderRefusal.UnsupportedShape);
        }
    }

    static void FlattenConcat(Node node, List<Node> parts)
    {
        if (node is CallNode {Function: KnownFunction.StringConcat, Arguments: [var right]} concat)
        {
            FlattenConcat(concat.Target, parts);
            parts.Add(right);
            return;
        }

        parts.Add(node);
    }

    string RenderIn(CallNode call, Scope scope)
    {
        var elementType = InferType(call.Target, scope);
        var items = new List<string>();
        foreach (var argument in call.Arguments)
        {
            if (argument is not ConstNode constant)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            items.Add(RenderConst(constant, elementType));
        }

        var tested = RenderNode(call.Target, scope);

        // A List rather than an array: an array receiver binds MemoryExtensions.Contains, whose
        // overload for a non-IEquatable element (an enum) carries an optional comparer the forward
        // pass refuses. List.Contains is the one-argument instance call it always reads. An empty
        // set has no element to infer a type from, so the List has to say it — which takes the
        // member's model.
        if (elementType is null)
        {
            if (items.Count == 0)
            {
                throw Refuse(RenderRefusal.UnresolvedModel);
            }

            return $"new[] {{ {string.Join(", ", items)} }}.Contains({tested})";
        }

        var list = items.Count == 0
            ? $"new List<{TypeName(elementType)}>()"
            : $"new List<{TypeName(elementType)}> {{ {string.Join(", ", items)} }}";
        return $"{list}.Contains({tested})";
    }

    // A member whose C# spelling needs the wrapped value rather than the optional: the wire carries
    // no wrapper, so a nullable member gets its .Value back — which takes knowing it is one.
    string RenderValue(Node node, Scope scope)
    {
        if (node is not MemberNode member)
        {
            return Target(node, scope);
        }

        var type = InferType(member, scope) ?? throw Refuse(RenderRefusal.UnresolvedModel);
        var text = RenderNode(member, scope);
        return Nullable.GetUnderlyingType(type) is null ? text : $"{text}.Value";
    }

    static string RenderConst(ConstNode constant, Type? expected)
    {
        var value = constant.Value;
        switch (constant.Tag)
        {
            case ClrTypeTag.Null:
                return "null";

            case ClrTypeTag.Boolean:
                return value is "true" or "false" ? value : throw Refuse(RenderRefusal.UnsupportedShape);

            case ClrTypeTag.Int32:
                return value ?? throw Refuse(RenderRefusal.UnsupportedShape);

            case ClrTypeTag.Int64:
                return value is null ? throw Refuse(RenderRefusal.UnsupportedShape) : $"{value}L";

            case ClrTypeTag.Decimal:
                return value is null ? throw Refuse(RenderRefusal.UnsupportedShape) : $"{value}m";

            case ClrTypeTag.Double:
                return value switch
                {
                    null => throw Refuse(RenderRefusal.UnsupportedShape),
                    "NaN" => "double.NaN",
                    "Infinity" => "double.PositiveInfinity",
                    "-Infinity" => "double.NegativeInfinity",
                    _ => value.AsSpan().ContainsAny('.', 'e', 'E') ? value : $"{value}d"
                };

            // Spelled as the constructor rather than ParseExact: it names the ticks and the Kind
            // outright, where a rendered parse would hand the reader a text to take on trust. The
            // parse happens here instead, and only a text the constructed value re-serializes to
            // exactly is rendered.
            case ClrTypeTag.DateTime:
            {
                if (value is null ||
                    !DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ||
                    parsed.ToString("o", CultureInfo.InvariantCulture) != value)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                return $"new DateTime({parsed.Ticks.ToString(CultureInfo.InvariantCulture)}L, DateTimeKind.{parsed.Kind})";
            }

            case ClrTypeTag.DateOnly:
            {
                if (value?.Split('-') is not [var year, var month, var day] ||
                    !int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var y) ||
                    !int.TryParse(month, NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
                    !int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d))
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                return $"new DateOnly({y}, {m}, {d})";
            }

            case ClrTypeTag.Guid:
                return value is null
                    ? throw Refuse(RenderRefusal.UnsupportedShape)
                    : $"Guid.Parse({CSharpLiteral.String(value)})";

            case ClrTypeTag.Bytes:
                return value is null
                    ? throw Refuse(RenderRefusal.UnsupportedShape)
                    : $"Convert.FromBase64String({CSharpLiteral.String(value)})";

            case ClrTypeTag.Enum:
                return RenderEnum(constant, expected);

            case ClrTypeTag.String:
                return RenderText(constant, expected);

            default:
                throw Refuse(RenderRefusal.UnsupportedShape);
        }
    }

    // An enum constant travels by name, so spelling it back takes the enum's CLR type — resolved
    // from the model of the member it is compared against.
    static string RenderEnum(ConstNode constant, Type? expected)
    {
        var type = expected is null ? null : Nullable.GetUnderlyingType(expected) ?? expected;
        if (type is not {IsEnum: true})
        {
            throw Refuse(RenderRefusal.UnresolvedModel);
        }

        if (constant.Value is not { } text)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        object parsed;
        try
        {
            parsed = Enum.Parse(type, text);
        }
        catch (Exception)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        // The wire text is what Enum.ToString produced; a text the round-trip would respell
        // differently cannot be reproduced from this enum.
        if (parsed.ToString() != text)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        // A numeric text is an undefined value, spelled as the cast that folds back to it.
        if (text.Length > 0 &&
            (char.IsAsciiDigit(text[0]) || text[0] == '-'))
        {
            return $"({type.Name}){text}";
        }

        var parts = text.Split(", ");
        if (parts.Length == 1)
        {
            return $"{type.Name}.{parts[0]}";
        }

        return $"({string.Join(" | ", parts.Select(_ => $"{type.Name}.{_}"))})";
    }

    // A string-tagged constant is usually a string — but it is also how every type ValueTag has no
    // tag for travels, so the compared member's type decides the spelling that reads back to the
    // same text.
    static string RenderText(ConstNode constant, Type? expected)
    {
        if (constant.Value is not { } text)
        {
            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        var type = expected is null ? null : Nullable.GetUnderlyingType(expected) ?? expected;

        if (type == typeof(char))
        {
            if (text.Length == 1)
            {
                return CSharpLiteral.Char(text[0]);
            }

            throw Refuse(RenderRefusal.UnsupportedShape);
        }

        if (type == typeof(TimeSpan))
        {
            return $"TimeSpan.Parse({CSharpLiteral.String(text)}, System.Globalization.CultureInfo.InvariantCulture)";
        }

        // The temporal types are spelled as constructors — see the DateTime case — after verifying
        // the constructed value re-serializes to the wire's exact text. Round-trip format on both
        // sides of that check: ValueTag writes one, so a default spelling here would read every one
        // of them as a text no constructed value reproduces, and refuse the render.
        if (type == typeof(Time))
        {
            if (!Time.TryParse(text, CultureInfo.InvariantCulture, out var time) ||
                time.ToString("o", CultureInfo.InvariantCulture) != text)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            return $"new TimeOnly({time.Ticks.ToString(CultureInfo.InvariantCulture)}L)";
        }

        if (type == typeof(DateTimeOffset))
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var offset) ||
                offset.ToString("o", CultureInfo.InvariantCulture) != text)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }

            return $"new DateTimeOffset({offset.Ticks.ToString(CultureInfo.InvariantCulture)}L, new TimeSpan({offset.Offset.Ticks.ToString(CultureInfo.InvariantCulture)}L))";
        }

        return CSharpLiteral.String(text);
    }
}