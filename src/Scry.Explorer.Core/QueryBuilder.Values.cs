namespace Scry;

// The values a filter compares against: what kind of input a member's type takes, which operators it
// offers, and the conversion each way between what the input holds and the C# literal that is written.
public static partial class QueryBuilder
{
    // What a datetime-local input holds, with and without seconds, and a bare date for a hand-typed one.
    static readonly string[] momentFormats =
    [
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd"
    ];

    static readonly string[] timeFormats =
    [
        "HH:mm",
        "HH:mm:ss"
    ];

    /// <summary>The kind of input a member of the type is compared through; <see cref="ValueKind.None"/> for one it is not.</summary>
    public static ValueKind Kind(SchemaIndex index, string typeDisplay)
    {
        var display = typeDisplay.TrimEnd('?');
        return display switch
        {
            "string" => ValueKind.Text,
            "char" => ValueKind.Character,
            "int" or "long" or "short" or "byte" or "sbyte" or "uint" or "ulong" or "ushort" => ValueKind.Integer,
            "decimal" => ValueKind.Decimal,
            "double" => ValueKind.Double,
            "float" => ValueKind.Single,
            "bool" => ValueKind.Boolean,
            "global::System.DateOnly" => ValueKind.Date,
            "global::System.DateTime" => ValueKind.DateTime,
            "global::System.DateTimeOffset" => ValueKind.DateTimeOffset,
            "global::System.TimeOnly" => ValueKind.Time,
            "global::System.TimeSpan" => ValueKind.Duration,
            "global::System.Guid" => ValueKind.Guid,
            _ when EnumValues(index, display) is not null => ValueKind.Enum,
            _ => ValueKind.None
        };
    }

    static readonly string[] dayOfWeekValues = Enum.GetNames<DayOfWeek>();

    static readonly string[] dateTimeKindValues = Enum.GetNames<DateTimeKind>();

    // Every name, aliases included (Redirect and Found are both 302): a snippet may use either, so the
    // builder has to read either back.
    static readonly string[] httpStatusCodeValues = Enum.GetNames<System.Net.HttpStatusCode>();

    /// <summary>
    /// The values a member of an enum type can be compared with, or null for a type that is no enum:
    /// one of the model's, as introspection describes it, or one of the BCL enums a model may use,
    /// which introspection never describes because every client already has them.
    /// </summary>
    public static IReadOnlyList<string>? EnumValues(SchemaIndex index, string typeDisplay)
    {
        var display = typeDisplay.TrimEnd('?');
        if (index.Enum(display) is { } described)
        {
            return described.Values;
        }

        // The spellings the server and the generator give the two (the shared BclEnums list).
        return display switch
        {
            "global::System.DayOfWeek" => dayOfWeekValues,
            "global::System.DateTimeKind" => dateTimeKindValues,
            "global::System.Net.HttpStatusCode" => httpStatusCodeValues,
            _ => null
        };
    }

    public static bool IsNullable(string typeDisplay) =>
        typeDisplay.EndsWith('?');

    /// <summary>
    /// The comparisons a member of the type offers. Text is searched as well as matched; a bool is
    /// true or false; an enum or a Guid is equal or not, having no order worth asking for; everything
    /// else is ordered. A nullable member can be asked whether it is there at all.
    /// </summary>
    public static IReadOnlyList<FilterOperator> Operators(SchemaIndex index, string typeDisplay)
    {
        IReadOnlyList<FilterOperator> operators = Kind(index, typeDisplay) switch
        {
            ValueKind.None => [],
            ValueKind.Text =>
            [
                FilterOperator.Contains,
                FilterOperator.StartsWith,
                FilterOperator.EndsWith,
                FilterOperator.Equal,
                FilterOperator.NotEqual
            ],
            ValueKind.Boolean =>
            [
                FilterOperator.IsTrue,
                FilterOperator.IsFalse
            ],
            ValueKind.Enum or ValueKind.Guid =>
            [
                FilterOperator.Equal,
                FilterOperator.NotEqual
            ],
            _ =>
            [
                FilterOperator.Equal,
                FilterOperator.NotEqual,
                FilterOperator.GreaterThan,
                FilterOperator.GreaterThanOrEqual,
                FilterOperator.LessThan,
                FilterOperator.LessThanOrEqual
            ]
        };

        if (operators.Count == 0 ||
            !IsNullable(typeDisplay))
        {
            return operators;
        }

        return [.. operators, FilterOperator.IsNull, FilterOperator.IsNotNull];
    }

    /// <summary>Whether the operator compares against a value, or stands on the member alone.</summary>
    public static bool TakesValue(FilterOperator filterOperator) =>
        filterOperator is not (FilterOperator.IsTrue or FilterOperator.IsFalse or FilterOperator.IsNull or FilterOperator.IsNotNull);

    /// <summary>The operator a new condition on a member of the type starts with.</summary>
    public static FilterOperator DefaultOperator(SchemaIndex index, string typeDisplay) =>
        Kind(index, typeDisplay) switch
        {
            ValueKind.Text => FilterOperator.Contains,
            ValueKind.Boolean => FilterOperator.IsTrue,
            _ => FilterOperator.Equal
        };

    /// <summary>
    /// The value a new condition starts with, as C#. Text starts empty, which a contains narrows
    /// nothing with until something is typed; the rest start at a value that reads as a placeholder.
    /// </summary>
    public static string? DefaultValue(SchemaIndex index, string typeDisplay) =>
        Kind(index, typeDisplay) switch
        {
            ValueKind.Text => "\"\"",
            ValueKind.Character => "'a'",
            ValueKind.Integer or ValueKind.Double => "0",
            ValueKind.Decimal => "0m",
            ValueKind.Single => "0f",
            ValueKind.Enum when EnumValues(index, typeDisplay) is [var first, ..] => $"{typeDisplay.TrimEnd('?')}.{CSharpIdentifier.Escape(first)}",
            ValueKind.Date => "new DateOnly(2000, 1, 1)",
            ValueKind.DateTime => "new DateTime(2000, 1, 1)",
            ValueKind.DateTimeOffset => "new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)",
            ValueKind.Time => "new TimeOnly(0, 0)",
            ValueKind.Duration => "new TimeSpan(0, 0, 0)",
            ValueKind.Guid => "new Guid(\"00000000-0000-0000-0000-000000000000\")",
            _ => null
        };

    /// <summary>
    /// The C# literal an input's text means for a member of the type, or null when it means none —
    /// letters in a number, the thirty-first of February. A date or a time is written as the
    /// constructor that makes it, which the translator folds into the constant it stands for.
    /// </summary>
    public static string? Literal(SchemaIndex index, string typeDisplay, string input)
    {
        var display = typeDisplay.TrimEnd('?');
        var text = input.Trim();
        var culture = CultureInfo.InvariantCulture;
        switch (Kind(index, typeDisplay))
        {
            case ValueKind.Text:
                return SymbolDisplay.FormatLiteral(input, quote: true);
            case ValueKind.Character:
                if (input.Length == 1)
                {
                    return SymbolDisplay.FormatLiteral(input[0], quote: true);
                }

                return null;
            case ValueKind.Integer:
                if (Int128.TryParse(text, NumberStyles.AllowLeadingSign, culture, out var integer) &&
                    InRange(display, integer))
                {
                    return integer.ToString(culture);
                }

                return null;
            case ValueKind.Decimal:
                if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, culture, out var money))
                {
                    return money.ToString(culture) + "m";
                }

                return null;
            case ValueKind.Double:
                if (double.TryParse(text, NumberStyles.Float, culture, out var real) &&
                    double.IsFinite(real))
                {
                    return real.ToString("R", culture);
                }

                return null;
            case ValueKind.Single:
                if (float.TryParse(text, NumberStyles.Float, culture, out var single) &&
                    float.IsFinite(single))
                {
                    return single.ToString("R", culture) + "f";
                }

                return null;
            case ValueKind.Boolean:
                if (text is "true" or "false")
                {
                    return text;
                }

                return null;
            case ValueKind.Enum:
                if (EnumValues(index, display)!.Contains(text))
                {
                    return $"{display}.{CSharpIdentifier.Escape(text)}";
                }

                return null;
            case ValueKind.Date:
                if (Date.TryParseExact(text, "yyyy-MM-dd", culture, DateTimeStyles.None, out var date))
                {
                    return string.Create(culture, $"new DateOnly({date.Year}, {date.Month}, {date.Day})");
                }

                return null;
            case ValueKind.DateTime:
                if (DateTime.TryParseExact(text, momentFormats, culture, DateTimeStyles.None, out var moment))
                {
                    if (moment.TimeOfDay == TimeSpan.Zero)
                    {
                        return string.Create(culture, $"new DateTime({moment.Year}, {moment.Month}, {moment.Day})");
                    }

                    return string.Create(culture, $"new DateTime({moment.Year}, {moment.Month}, {moment.Day}, {moment.Hour}, {moment.Minute}, {moment.Second})");
                }

                return null;
            case ValueKind.DateTimeOffset:
                // The input has no offset to give, so the moment is taken as UTC — the one reading of a
                // bare date and time that means the same on every machine that runs the snippet.
                if (DateTime.TryParseExact(text, momentFormats, culture, DateTimeStyles.None, out var instant))
                {
                    return string.Create(culture, $"new DateTimeOffset({instant.Year}, {instant.Month}, {instant.Day}, {instant.Hour}, {instant.Minute}, {instant.Second}, TimeSpan.Zero)");
                }

                return null;
            case ValueKind.Time:
                if (Time.TryParseExact(text, timeFormats, culture, DateTimeStyles.None, out var time))
                {
                    if (time.Second == 0)
                    {
                        return string.Create(culture, $"new TimeOnly({time.Hour}, {time.Minute})");
                    }

                    return string.Create(culture, $"new TimeOnly({time.Hour}, {time.Minute}, {time.Second})");
                }

                return null;
            case ValueKind.Duration:
                if (TimeSpan.TryParse(text, culture, out var span) &&
                    span is
                    {
                        Milliseconds: 0,
                        Microseconds: 0,
                        Nanoseconds: 0
                    })
                {
                    if (span.Days == 0)
                    {
                        return string.Create(culture, $"new TimeSpan({span.Hours}, {span.Minutes}, {span.Seconds})");
                    }

                    return string.Create(culture, $"new TimeSpan({span.Days}, {span.Hours}, {span.Minutes}, {span.Seconds})");
                }

                return null;
            case ValueKind.Guid:
                if (Guid.TryParse(text, out var guid))
                {
                    return $"new Guid(\"{guid:D}\")";
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// What an input shows for a C# value written for a member of the type, or null when the code is
    /// not a literal of the kind the input edits — a variable from the preamble, a computed value.
    /// Exactly the code <see cref="Literal"/> writes reads back, so a value survives being shown and
    /// written again unchanged.
    /// </summary>
    public static string? Display(SchemaIndex index, string typeDisplay, string code)
    {
        var expression = SyntaxFactory.ParseExpression(code);
        if (expression.ContainsDiagnostics ||
            expression.FullSpan.Length != code.Length)
        {
            return null;
        }

        var culture = CultureInfo.InvariantCulture;
        switch (Kind(index, typeDisplay))
        {
            case ValueKind.Text:
                if (expression.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return ((LiteralExpressionSyntax) expression).Token.ValueText;
                }

                return null;
            case ValueKind.Character:
                if (expression.IsKind(SyntaxKind.CharacterLiteralExpression))
                {
                    return ((LiteralExpressionSyntax) expression).Token.ValueText;
                }

                return null;
            case ValueKind.Integer:
                if (Number(expression) is ({ } value and (int or long or uint or ulong), var negative))
                {
                    return Signed(Convert.ToString(value, culture)!, negative);
                }

                return null;
            case ValueKind.Decimal:
                if (Number(expression) is ({ } money and (decimal or int or long), var minus))
                {
                    return Signed(Convert.ToString(money, culture)!, minus);
                }

                return null;
            case ValueKind.Double:
                if (Number(expression) is ({ } real and (double or int or long), var below))
                {
                    return Signed(real is double number ? number.ToString("R", culture) : Convert.ToString(real, culture)!, below);
                }

                return null;
            case ValueKind.Single:
                if (Number(expression) is ({ } single and (float or int or long), var under))
                {
                    return Signed(single is float number ? number.ToString("R", culture) : Convert.ToString(single, culture)!, under);
                }

                return null;
            case ValueKind.Boolean:
                return expression.Kind() switch
                {
                    SyntaxKind.TrueLiteralExpression => "true",
                    SyntaxKind.FalseLiteralExpression => "false",
                    _ => null
                };
            case ValueKind.Enum:
                var display = typeDisplay.TrimEnd('?');
                if (expression is MemberAccessExpressionSyntax
                    {
                        Expression: var owner,
                        Name: IdentifierNameSyntax member
                    } &&
                    NamesEnum(owner.ToString(), display) &&
                    EnumValues(index, display)!.Contains(member.Identifier.ValueText))
                {
                    return member.Identifier.ValueText;
                }

                return null;
            case ValueKind.Date:
            {
                if (Constructed(expression, "DateOnly") is [var year, var month, var day] &&
                    IsDate(year, month, day))
                {
                    return string.Create(culture, $"{year:D4}-{month:D2}-{day:D2}");
                }

                return null;
            }
            case ValueKind.DateTime:
                return Constructed(expression, "DateTime") switch
                {
                    [var year, var month, var day] when IsDate(year, month, day) =>
                        string.Create(culture, $"{year:D4}-{month:D2}-{day:D2}T00:00"),
                    [var year, var month, var day, var hour, var minute, var second] when IsDate(year, month, day) && IsTime(hour, minute, second) =>
                        Moment(year, month, day, hour, minute, second),
                    _ => null
                };
            case ValueKind.DateTimeOffset:
            {
                if (expression
                        is BaseObjectCreationExpressionSyntax
                        {
                            ArgumentList.Arguments:
                            [
                                _, _, _, _, _, _, {
                                    Expression: MemberAccessExpressionSyntax
                                    {
                                        Expression: IdentifierNameSyntax
                                        {
                                            Identifier.ValueText: "TimeSpan"
                                        },
                                        Name.Identifier.ValueText: "Zero"
                                    }
                                }
                            ]
                        } &&
                    Constructed(expression, "DateTimeOffset", 6) is [var year, var month, var day, var hour, var minute, var second] &&
                    IsDate(year, month, day) &&
                    IsTime(hour, minute, second))
                {
                    return Moment(year, month, day, hour, minute, second);
                }

                return null;
            }
            case ValueKind.Time:
                return Constructed(expression, "TimeOnly") switch
                {
                    [var hour, var minute] when IsTime(hour, minute, 0) =>
                        string.Create(culture, $"{hour:D2}:{minute:D2}"),
                    [var hour, var minute, var second] when IsTime(hour, minute, second) =>
                        string.Create(culture, $"{hour:D2}:{minute:D2}:{second:D2}"),
                    _ => null
                };
            case ValueKind.Duration:
                return Constructed(expression, "TimeSpan") switch
                {
                    [var hours, var minutes, var seconds] => Duration(0, hours, minutes, seconds),
                    [var days, var hours, var minutes, var seconds] => Duration(days, hours, minutes, seconds),
                    _ => null
                };
            case ValueKind.Guid:
                if (expression is BaseObjectCreationExpressionSyntax {ArgumentList.Arguments: [{Expression: LiteralExpressionSyntax literal}]} creation &&
                    TypeName(creation) == "Guid" &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression) &&
                    Guid.TryParse(literal.Token.ValueText, out var guid))
                {
                    return guid.ToString("D");
                }

                return null;
            default:
                return null;
        }
    }

    // Whether code names the enum a member is typed as: a model enum by its bare name, as Literal writes
    // it; a BCL enum as Literal writes it (global::System.DayOfWeek), or as a snippet would more likely
    // write it, under the System import every snippet has (DayOfWeek, System.DayOfWeek).
    static bool NamesEnum(string owner, string display)
    {
        if (owner == display)
        {
            return true;
        }

        const string global = "global::";
        if (!display.StartsWith(global, StringComparison.Ordinal))
        {
            return false;
        }

        var qualified = display[global.Length..];
        return owner == qualified ||
               owner == qualified[(qualified.LastIndexOf('.') + 1)..];
    }

    static string Moment(int year, int month, int day, int hour, int minute, int second)
    {
        var culture = CultureInfo.InvariantCulture;
        if (second == 0)
        {
            return string.Create(culture, $"{year:D4}-{month:D2}-{day:D2}T{hour:D2}:{minute:D2}");
        }

        return string.Create(culture, $"{year:D4}-{month:D2}-{day:D2}T{hour:D2}:{minute:D2}:{second:D2}");
    }

    // Null for parts past what a TimeSpan holds, which the constructor refuses by throwing.
    static string? Duration(int days, int hours, int minutes, int seconds)
    {
        var ticks = (Int128) days * TimeSpan.TicksPerDay +
                    (Int128) hours * TimeSpan.TicksPerHour +
                    (Int128) minutes * TimeSpan.TicksPerMinute +
                    (Int128) seconds * TimeSpan.TicksPerSecond;
        if (ticks < TimeSpan.MinValue.Ticks ||
            ticks > TimeSpan.MaxValue.Ticks)
        {
            return null;
        }

        return new TimeSpan((long) ticks).ToString("c", CultureInfo.InvariantCulture);
    }

    static string Signed(string digits, bool negative)
    {
        if (negative)
        {
            return "-" + digits;
        }

        return digits;
    }

    /// <summary>A numeric literal's value, and whether a minus was written ahead of it.</summary>
    static (object Value, bool Negative)? Number(ExpressionSyntax expression)
    {
        var negative = false;
        if (expression is PrefixUnaryExpressionSyntax prefix &&
            prefix.IsKind(SyntaxKind.UnaryMinusExpression))
        {
            negative = true;
            expression = prefix.Operand;
        }

        if (expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
            literal.Token.Value is { } value)
        {
            return (value, negative);
        }

        return null;
    }

    /// <summary>
    /// The arguments of a <c>new</c> of the named type, where every one is an int literal — the only
    /// shape <see cref="Literal"/> writes. Null for any other expression. Qualified spellings of the
    /// type are accepted, as a hand-written one might be.
    /// </summary>
    static int[]? Constructed(ExpressionSyntax expression, string type, int? take = null)
    {
        if (expression is not BaseObjectCreationExpressionSyntax {ArgumentList: { } argumentList} creation ||
            TypeName(creation) != type)
        {
            return null;
        }

        var arguments = argumentList.Arguments;
        var count = take ?? arguments.Count;
        var values = new int[count];
        for (var position = 0; position < count; position++)
        {
            if (arguments[position] is not
                {
                    NameColon: null,
                    Expression: LiteralExpressionSyntax {Token.Value: int value} literal
                } ||
                !literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                return null;
            }

            values[position] = value;
        }

        return values;
    }

    // The type a new names, without its namespace: DateOnly for new System.DateOnly(…) as for new DateOnly(…).
    static string? TypeName(BaseObjectCreationExpressionSyntax creation) =>
        creation switch
        {
            ObjectCreationExpressionSyntax {Type: QualifiedNameSyntax qualified} => qualified.Right.Identifier.ValueText,
            ObjectCreationExpressionSyntax {Type: AliasQualifiedNameSyntax aliased} => aliased.Name.Identifier.ValueText,
            ObjectCreationExpressionSyntax {Type: IdentifierNameSyntax identifier} => identifier.Identifier.ValueText,
            _ => null
        };

    // Whether the values make a date at all; the thirty-first of February does not.
    static bool IsDate(int year, int month, int day) =>
        year is >= 1 and <= 9999 &&
        month is >= 1 and <= 12 &&
        day >= 1 &&
        day <= DateTime.DaysInMonth(year, month);

    static bool IsTime(int hour, int minute, int second) =>
        hour is >= 0 and <= 23 &&
        minute is >= 0 and <= 59 &&
        second is >= 0 and <= 59;

    static bool InRange(string type, Int128 value) =>
        type switch
        {
            "sbyte" => value >= sbyte.MinValue && value <= sbyte.MaxValue,
            "byte" => value >= byte.MinValue && value <= byte.MaxValue,
            "short" => value >= short.MinValue && value <= short.MaxValue,
            "ushort" => value >= ushort.MinValue && value <= ushort.MaxValue,
            "int" => value >= int.MinValue && value <= int.MaxValue,
            "uint" => value >= uint.MinValue && value <= uint.MaxValue,
            "long" => value >= long.MinValue && value <= long.MaxValue,
            "ulong" => value >= ulong.MinValue && value <= ulong.MaxValue,
            _ => false
        };
}
