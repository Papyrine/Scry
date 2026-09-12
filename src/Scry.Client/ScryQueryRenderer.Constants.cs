// The callable surface and the literals: every KnownFunction spelled back as the call that captures
// to it, and every ConstNode spelled back as the C# expression ValueTag reads to the same bytes.
partial class QueryRenderer
{
    void RenderCall(CallNode call, Scope scope)
    {
        var arguments = call.Arguments;

        // The receiver, then the rest of the call spelled whole: `.ToLower()`, `.Length`.
        void OnTarget(string spelling)
        {
            Target(call.Target, scope);
            builder.Append(spelling);
        }

        // A value-typed receiver whose member is unlifted in C# gets its .Value back — the forward
        // pass strips it. Needing this without a model to ask is a resolution failure.
        void OnValueTarget(string spelling)
        {
            RenderValue(call.Target, scope);
            builder.Append(spelling);
        }

        void Argument(int index) => RenderNode(arguments[index], scope);

        void ValueArgument(int index) => RenderValue(arguments[index], scope);

        void From(string keyword)
        {
            var type = InferType(call.Target, scope);
            if (type is null ||
                (Nullable.GetUnderlyingType(type) ?? type) == typeof(string))
            {
                builder.Append(keyword).Append(".Parse(");
                RenderNode(call.Target, scope);
                builder.Append(')');
                return;
            }

            builder.Append('(').Append(keyword);
            if (Nullable.GetUnderlyingType(type) is not null)
            {
                builder.Append('?');
            }

            builder.Append(')');
            Operand(call.Target, scope);
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

                var collate = call.Target as CollateNode;
                Target(collate?.Target ?? call.Target, scope);
                builder.Append('.').Append(name).Append('(');
                if (arguments[0] is ConstNode text)
                {
                    builder.Append(RenderConst(text, typeof(string)));
                }
                else
                {
                    Argument(0);
                }

                if (collate is not null)
                {
                    builder.Append(", ").Append(Comparison(collate.Match));
                }

                builder.Append(')');
                return;
            }

            case KnownFunction.StringToLower:
                OnTarget(".ToLower()");
                return;
            case KnownFunction.StringToUpper:
                OnTarget(".ToUpper()");
                return;
            case KnownFunction.StringTrim:
                OnTarget(".Trim()");
                return;
            case KnownFunction.StringTrimStart:
                OnTarget(".TrimStart()");
                return;
            case KnownFunction.StringTrimEnd:
                OnTarget(".TrimEnd()");
                return;

            case KnownFunction.StringIsNullOrEmpty:
                builder.Append("string.IsNullOrEmpty(");
                RenderNode(call.Target, scope);
                builder.Append(')');
                return;
            case KnownFunction.StringIsNullOrWhiteSpace:
                builder.Append("string.IsNullOrWhiteSpace(");
                RenderNode(call.Target, scope);
                builder.Append(')');
                return;

            case KnownFunction.StringLength:
                OnTarget(".Length");
                return;

            case KnownFunction.StringSubstring:
                if (arguments.Count is not (1 or 2))
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                OnTarget(".Substring(");
                Argument(0);
                if (arguments.Count == 2)
                {
                    builder.Append(", ");
                    Argument(1);
                }

                builder.Append(')');
                return;

            case KnownFunction.StringIndexOf:
                OnTarget(".IndexOf(");
                Argument(0);
                builder.Append(')');
                return;
            case KnownFunction.StringReplace:
                OnTarget(".Replace(");
                Argument(0);
                builder.Append(", ");
                Argument(1);
                builder.Append(')');
                return;

            case KnownFunction.StringFirst:
                OnTarget(".FirstOrDefault()");
                return;
            case KnownFunction.StringLast:
                OnTarget(".LastOrDefault()");
                return;

            // The left-folded chain flattens back into the one static spelling that captures to the
            // identical fold whatever the operand types are.
            case KnownFunction.StringConcat:
            {
                var parts = new List<Node>();
                FlattenConcat(call, parts);
                builder.Append("string.Concat(");
                for (var i = 0; i < parts.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    RenderNode(parts[i], scope);
                }

                builder.Append(')');
                return;
            }

            case KnownFunction.StringFrom:
                OnTarget(".ToString()");
                return;

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
                RenderValue(call.Target, scope);
                builder.Append('.').Append(name);
                return;
            }

            case KnownFunction.TimeSpanHours or
                KnownFunction.TimeSpanMinutes or
                KnownFunction.TimeSpanSeconds or
                KnownFunction.TimeSpanMilliseconds or
                KnownFunction.TimeSpanMicroseconds or
                KnownFunction.TimeSpanNanoseconds:
                RenderValue(call.Target, scope);
                builder.Append('.').Append(call.Function.ToString()["TimeSpan".Length..]);
                return;

            case KnownFunction.DateAddYears or
                KnownFunction.DateAddMonths or
                KnownFunction.DateAddDays or
                KnownFunction.DateAddHours or
                KnownFunction.DateAddMinutes or
                KnownFunction.DateAddSeconds or
                KnownFunction.DateAddMilliseconds:
                RenderValue(call.Target, scope);
                builder.Append('.').Append(call.Function.ToString()["Date".Length..]).Append('(');
                ValueArgument(0);
                builder.Append(')');
                return;

            case KnownFunction.DateOnlyFromDateTime:
                builder.Append("DateOnly.FromDateTime(");
                RenderValue(call.Target, scope);
                builder.Append(')');
                return;
            case KnownFunction.TimeOnlyFromDateTime:
                builder.Append("TimeOnly.FromDateTime(");
                RenderValue(call.Target, scope);
                builder.Append(')');
                return;
            case KnownFunction.TimeOnlyFromTimeSpan:
                builder.Append("TimeOnly.FromTimeSpan(");
                RenderValue(call.Target, scope);
                builder.Append(')');
                return;
            case KnownFunction.DateTimeFromDateAndTime:
                OnValueTarget(".ToDateTime(");

                // The time of day travels under the String tag, having none of its own, so rendering
                // it needs the type it composes with — without that it spells as text, which is not
                // what ToDateTime takes.
                if (arguments is [ConstNode time, ..])
                {
                    builder.Append(RenderConst(time, typeof(Time)));
                }
                else
                {
                    ValueArgument(0);
                }

                builder.Append(')');
                return;

            case KnownFunction.UnixSecondsFromOffset:
                OnValueTarget(".ToUnixTimeSeconds()");
                return;
            case KnownFunction.UnixMillisecondsFromOffset:
                OnValueTarget(".ToUnixTimeMilliseconds()");
                return;

            case KnownFunction.MathDegreesToRadians:
                builder.Append("double.DegreesToRadians(");
                RenderValue(call.Target, scope);
                builder.Append(')');
                return;
            case KnownFunction.MathRadiansToDegrees:
                builder.Append("double.RadiansToDegrees(");
                RenderValue(call.Target, scope);
                builder.Append(')');
                return;

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
                if (arguments.Count > 1)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                builder.Append("Math.").Append(call.Function.ToString()["Math".Length..]).Append('(');
                RenderValue(call.Target, scope);
                if (arguments.Count == 1)
                {
                    builder.Append(", ");
                    ValueArgument(0);
                }

                builder.Append(')');
                return;

            case KnownFunction.In:
                RenderIn(call, scope);
                return;

            case KnownFunction.EnumHasFlag:
            {
                if (arguments is not [ConstNode flag])
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                var enumType = InferType(call.Target, scope);
                OnValueTarget(".HasFlag(");
                builder.Append(RenderConst(flag, enumType)).Append(')');
                return;
            }

            // Over text these parse it; over a number they are the widening cast the client wrote,
            // and read back as one — lifted where the member is optional, since that is how C# spells
            // a conversion of a nullable.
            case KnownFunction.Int32From:
                From("int");
                return;
            case KnownFunction.Int64From:
                From("long");
                return;
            case KnownFunction.DecimalFrom:
                From("decimal");
                return;
            case KnownFunction.DoubleFrom:
                From("double");
                return;
            case KnownFunction.BooleanFrom:
                From("bool");
                return;
            case KnownFunction.ByteFrom:
                From("byte");
                return;
            case KnownFunction.Int16From:
                From("short");
                return;
            case KnownFunction.SingleFrom:
                From("float");
                return;

            case KnownFunction.CompareTo:
                OnValueTarget(".CompareTo(");
                if (arguments[0] is ConstNode compared)
                {
                    builder.Append(RenderConst(compared, InferType(call.Target, scope)));
                }
                else
                {
                    Argument(0);
                }

                builder.Append(')');
                return;

            case KnownFunction.BytesLength:
                OnTarget(".Length");
                return;

            case KnownFunction.BytesContains:
                OnTarget(".Contains(");

                // The byte the wire compares travels as its code point, and the snippet has to hand
                // the overload a byte again for the same capture to happen.
                if (arguments[0] is ConstNode {Tag: ClrTypeTag.Int32, Value: { } number})
                {
                    builder.Append("(byte)").Append(number);
                }
                else
                {
                    Argument(0);
                }

                builder.Append(')');
                return;

            case KnownFunction.BytesElementAt:
                OnTarget(".ElementAt(");
                Argument(0);
                builder.Append(')');
                return;

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

    void RenderIn(CallNode call, Scope scope)
    {
        var elementType = InferType(call.Target, scope);
        foreach (var argument in call.Arguments)
        {
            if (argument is not ConstNode)
            {
                throw Refuse(RenderRefusal.UnsupportedShape);
            }
        }

        // A List rather than an array: an array receiver binds MemoryExtensions.Contains, whose
        // overload for a non-IEquatable element (an enum) carries an optional comparer the forward
        // pass refuses. List.Contains is the one-argument instance call it always reads. An empty
        // set has no element to infer a type from, so the List has to say it — which takes the
        // member's model.
        if (elementType is null)
        {
            if (call.Arguments.Count == 0)
            {
                throw Refuse(RenderRefusal.UnresolvedModel);
            }

            builder.Append("new[]");
        }
        else
        {
            builder.Append("new List<").Append(TypeName(elementType)).Append('>');
            if (call.Arguments.Count == 0)
            {
                builder.Append("()");
            }
        }

        if (call.Arguments.Count > 0)
        {
            builder.Append(" { ");
            for (var i = 0; i < call.Arguments.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(RenderConst((ConstNode) call.Arguments[i], elementType));
            }

            builder.Append(" }");
        }

        builder.Append(".Contains(");
        RenderNode(call.Target, scope);
        builder.Append(')');
    }

    // A member whose C# spelling needs the wrapped value rather than the optional: the wire carries
    // no wrapper, so a nullable member gets its .Value back — which takes knowing it is one.
    void RenderValue(Node node, Scope scope)
    {
        if (node is not MemberNode member)
        {
            Target(node, scope);
            return;
        }

        var type = InferType(member, scope) ?? throw Refuse(RenderRefusal.UnresolvedModel);
        RenderNode(member, scope);
        if (Nullable.GetUnderlyingType(type) is not null)
        {
            builder.Append(".Value");
        }
    }

    static string RenderConst(ConstNode constant, Type? expected)
    {
        var value = constant.Value;
        switch (constant.Tag)
        {
            case ClrTypeTag.Null:
                return "null";

            case ClrTypeTag.Boolean:
                if (value is "true" or "false")
                {
                    return value;
                }

                throw Refuse(RenderRefusal.UnsupportedShape);

            case ClrTypeTag.Int32:
                return value ?? throw Refuse(RenderRefusal.UnsupportedShape);

            case ClrTypeTag.Int64:
                if (value is null)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                return $"{value}L";

            case ClrTypeTag.Decimal:
                if (value is null)
                {
                    throw Refuse(RenderRefusal.UnsupportedShape);
                }

                return $"{value}m";

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
