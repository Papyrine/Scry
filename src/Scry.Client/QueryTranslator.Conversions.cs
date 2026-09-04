// The conversions a row is read through. Most of what C# writes as a Convert node says nothing the
// wire needs — every operand is optional there, an enum travels by name, a char as itself — and is
// dropped. The rest either changes the answer if dropped, and is carried, or cannot be carried the
// same way on every source, and is refused.
sealed partial class QueryTranslator
{
    /// <summary>
    /// Translates a conversion the row is read through. A widening between numeric types travels as
    /// the function that reads the target type — the same one that parses text — since the provider
    /// makes it a CAST; dropped, <c>(double)a / b</c> over two integers would divide as integers. A
    /// narrowing one is refused: the database truncates where the CLR rounds, so no one answer would
    /// hold across sources. An enum or a char read as a number is refused too — neither travels as
    /// one, and the response would carry the name or the character the client then could not read.
    /// </summary>
    Node TranslateConversion(UnaryExpression convert, ParameterExpression root)
    {
        var source = Nullable.GetUnderlyingType(convert.Operand.Type) ?? convert.Operand.Type;
        var target = Nullable.GetUnderlyingType(convert.Type) ?? convert.Type;

        if (source.IsEnum)
        {
            throw new NotSupportedException(
                $"'({target.Name}){source.Name}' reads an enum as a number, which the wire does not carry: an enum travels by name. Compare, order by, or project the member itself.");
        }

        if (source == typeof(char))
        {
            throw new NotSupportedException(
                $"'({target.Name})' over a char reads it as a number, which the wire does not carry: a char travels as itself. Compare, order by, or project the member itself.");
        }

        if (Rank(source) is not { } from ||
            Rank(target) is not { } to)
        {
            throw new NotSupportedException(
                $"The conversion from '{source.Name}' to '{target.Name}' is not supported by Scry.");
        }

        // The same width under a different sign: nothing the provider would compute differently, so
        // the operand travels as it is and the server's own promotion reconciles the pair.
        if (to == from)
        {
            return TranslateExpr(convert.Operand, root);
        }

        if (to < from)
        {
            throw new NotSupportedException(
                $"'({target.Name})' narrows a '{source.Name}', which the wire does not carry: the database truncates where the CLR rounds, so the two would answer differently. Compute over the wider type, or round with Math.Round or Math.Truncate first.");
        }

        if (!parseTargets.TryGetValue(target, out var function))
        {
            throw new NotSupportedException(
                $"A conversion to '{target.Name}' is not supported by Scry. Widen to int, long, float, double, or decimal instead.");
        }

        return new CallNode(function, TranslateExpr(convert.Operand, root), []);
    }

    /// <summary>
    /// The operand of a comparison, with the conversions C# lowers the comparison through peeled off:
    /// an enum or a char read as its number, a value lifted to its nullable, a box. The wire compares
    /// the member and its constant as they were written — an enum by name, a char as itself.
    /// </summary>
    static Expression Comparand(Expression expression)
    {
        while (expression is UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} convert &&
               IsBuiltIn(convert) &&
               (IsLifting(convert.Operand.Type, convert.Type) || IsComparisonLowering(convert.Operand.Type, convert.Type)))
        {
            expression = convert.Operand;
        }

        return expression;
    }

    // A conversion the language defines rather than a type's own: one with no method behind it, or
    // one of decimal's operators, which the tree records as a method call though it is a numeric
    // conversion like any other. Anything else is code the client wrote, which cannot travel.
    static bool IsBuiltIn(UnaryExpression convert) =>
        convert.Method is null ||
        convert.Method.DeclaringType == typeof(decimal);

    // A conversion that changes nothing the wire carries: to or from a nullable of the same type, or
    // to a reference type (a box, or a cast to a base or an interface).
    static bool IsLifting(Type from, Type to)
    {
        if (!to.IsValueType)
        {
            return true;
        }

        var source = Nullable.GetUnderlyingType(from) ?? from;
        var target = Nullable.GetUnderlyingType(to) ?? to;
        return source == target;
    }

    // What C# converts a comparison's operands through — an enum to its underlying number, a char to
    // an int, a narrower number to the wider operand's type — which means nothing on the wire: the
    // server promotes a comparison's operands itself, exactly as C# did, and compares an enum or a
    // char as what it is. Anywhere else the same conversion is a real read of the value.
    static bool IsComparisonLowering(Type from, Type to)
    {
        var source = Nullable.GetUnderlyingType(from) ?? from;
        var target = Nullable.GetUnderlyingType(to) ?? to;
        if (source.IsEnum)
        {
            return target == Enum.GetUnderlyingType(source);
        }

        if (source == typeof(char))
        {
            return target == typeof(int);
        }

        return Rank(source) is { } narrow &&
               Rank(target) is { } wide &&
               wide >= narrow;
    }

    static bool IsComparison(ExpressionType type) =>
        type is ExpressionType.Equal or
            ExpressionType.NotEqual or
            ExpressionType.LessThan or
            ExpressionType.LessThanOrEqual or
            ExpressionType.GreaterThan or
            ExpressionType.GreaterThanOrEqual;

    // The numeric widths, as the server ranks them for promotion. Must stay in lockstep with
    // ExpressionBuilder.Rank: a conversion this side calls widening is one the server accepts.
    static int? Rank(Type type) =>
        Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or TypeCode.SByte => 1,
            TypeCode.Int16 or TypeCode.UInt16 => 2,
            TypeCode.Int32 or TypeCode.UInt32 => 3,
            TypeCode.Int64 or TypeCode.UInt64 => 4,
            TypeCode.Decimal => 5,
            TypeCode.Single => 6,
            TypeCode.Double => 7,
            _ => null
        };
}
