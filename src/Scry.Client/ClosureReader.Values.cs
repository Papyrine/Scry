// The half of the reader that is not a call: the operators and the numeric conversions the compiler
// emits as instructions rather than as a member it could be asked to invoke. Everything here is a
// second spelling of what C# already means, which is why ClosureReaderTests checks it against the
// compiled answer for every operator over every type rather than against expected values.
static partial class ClosureReader
{
    static bool CanCoerce(UnaryExpression convert)
    {
        // The conversion operators — every one decimal takes part in, and every one a type of the
        // consumer's declared — are members, so they are invoked rather than reproduced.
        if (convert.Method is not null)
        {
            return true;
        }

        // A boxing, a widening to a base, or a conversion to an interface leaves the value as it is,
        // and cannot overflow, so the checked spelling is answerable here too.
        if (convert.Type.IsAssignableFrom(convert.Operand.Type))
        {
            return true;
        }

        if (convert.NodeType == ExpressionType.ConvertChecked)
        {
            return false;
        }

        var from = Numeric(Underlying(convert.Operand.Type));
        var to = Numeric(Underlying(convert.Type));

        // Integers convert to integers by truncating to the target's width, and to the floating types
        // by rounding to nearest — both exactly stated. A float narrowed to an integer is the one
        // left out: what an unchecked conversion does with a value the target cannot hold is a
        // property of the instruction rather than of the language, so it stays the compiler's.
        return (Integrals.Contains(from) && (Integrals.Contains(to) || IsFloating(to))) ||
               (IsFloating(from) && IsFloating(to));
    }

    static bool IsFloating(Type type) =>
        type == typeof(float) ||
        type == typeof(double);

    static object? Coerce(UnaryExpression convert, object? value)
    {
        var target = Underlying(convert.Type);

        if (value is null)
        {
            // Reading the value of an absent optional is the throw the compiled form raises, in the
            // words the runtime uses for it.
            if (convert.Type == target &&
                target.IsValueType)
            {
                throw new InvalidOperationException("Nullable object must have a value.");
            }

            return null;
        }

        if (convert.Method is { } method)
        {
            return Invoke(method, null, [value]);
        }

        if (convert.Type.IsAssignableFrom(convert.Operand.Type))
        {
            return value;
        }

        // An enum converts as the integer it is written on, in both directions.
        var source = value is Enum
            ? Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture)
            : value;
        var numeric = ToNumeric(source, Type.GetTypeCode(Numeric(target)));
        return target.IsEnum ? Enum.ToObject(target, numeric) : numeric;
    }

    static object ToNumeric(object value, TypeCode target) =>
        target switch
        {
            TypeCode.Double => ToDouble(value),
            TypeCode.Single => ToSingle(value),
            _ => FromBits(Bits(value), target)
        };

    static double ToDouble(object value) =>
        value switch
        {
            sbyte number => number,
            byte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            char number => number,
            float number => number,
            double number => number,
            _ => throw new($"'{value.GetType()}' passed CanCoerce with nothing to read it as a double.")
        };

    static float ToSingle(object value) =>
        value switch
        {
            sbyte number => number,
            byte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            char number => number,
            float number => number,
            // The one narrowing here, and it does not throw: a magnitude a float cannot hold becomes
            // an infinity, as the instruction produces.
            double number => (float) number,
            _ => throw new($"'{value.GetType()}' passed CanCoerce with nothing to read it as a float.")
        };

    // An integer's value as the 64 bits that hold it, sign-extended, which is the form every
    // narrowing and widening between the integer types is stated over.
    static ulong Bits(object value) =>
        unchecked(value switch
        {
            sbyte number => (ulong) (long) number,
            byte number => number,
            short number => (ulong) (long) number,
            ushort number => number,
            int number => (ulong) (long) number,
            uint number => number,
            long number => (ulong) number,
            ulong number => number,
            char number => number,
            _ => throw new($"'{value.GetType()}' passed CanCoerce with no bits to read.")
        });

    static object FromBits(ulong bits, TypeCode target)
    {
        unchecked
        {
            return target switch
            {
                TypeCode.SByte => (sbyte) bits,
                TypeCode.Byte => (byte) bits,
                TypeCode.Int16 => (short) bits,
                TypeCode.UInt16 => (ushort) bits,
                TypeCode.Int32 => (int) bits,
                TypeCode.UInt32 => (uint) bits,
                TypeCode.Int64 => (long) bits,
                TypeCode.UInt64 => bits,
                TypeCode.Char => (char) bits,
                _ => throw new($"'{target}' passed CanCoerce with no integer to read it as.")
            };
        }
    }

    static object Compute(ExpressionType op, object operand) =>
        operand switch
        {
            bool value => op is ExpressionType.Not ? !value : value,
            int value => Unary(op, value),
            uint value => Unary(op, value),
            long value => Unary(op, value),
            ulong value => Unary(op, value),
            float value => Unary(op, value),
            double value => Unary(op, value),
            _ => throw new($"'{operand.GetType()}' passed CanRead with no '{op}' to read.")
        };

    static object Unary(ExpressionType op, int operand) =>
        unchecked(op switch
        {
            ExpressionType.Negate => -operand,
            ExpressionType.Not => ~operand,
            _ => operand
        });

    static object Unary(ExpressionType op, uint operand) =>
        unchecked(op switch
        {
            // An unsigned integer negates through the signed width above it, as the language states
            // it and as the compiler emits it.
            ExpressionType.Negate => -(long) operand,
            ExpressionType.Not => ~operand,
            _ => operand
        });

    static object Unary(ExpressionType op, long operand) =>
        unchecked(op switch
        {
            ExpressionType.Negate => -operand,
            ExpressionType.Not => ~operand,
            _ => operand
        });

    // An unsigned long has no negation to emit, so only its complement reaches here.
    static object Unary(ExpressionType op, ulong operand) =>
        op is ExpressionType.Not ? ~operand : operand;

    static object Unary(ExpressionType op, float operand) =>
        op is ExpressionType.Negate ? -operand : operand;

    static object Unary(ExpressionType op, double operand) =>
        op is ExpressionType.Negate ? -operand : operand;

    static object Compute(ExpressionType op, object left, object right)
    {
        if (op is ExpressionType.LeftShift or ExpressionType.RightShift)
        {
            return Shift(op, left, (int) right);
        }

        return (left, right) switch
        {
            (bool first, bool second) => Binary(op, first, second),
            (int first, int second) => Binary(op, first, second),
            (uint first, uint second) => Binary(op, first, second),
            (long first, long second) => Binary(op, first, second),
            (ulong first, ulong second) => Binary(op, first, second),
            (float first, float second) => Binary(op, first, second),
            (double first, double second) => Binary(op, first, second),
            _ => throw new($"'{left.GetType()}' passed CanRead with no '{op}' to read.")
        };
    }

    // The count is masked to the operand's width, which is the shift the instruction performs and
    // what the language says it means.
    static object Shift(ExpressionType op, object left, int count) =>
        (left, op) switch
        {
            (int value, ExpressionType.LeftShift) => value << count,
            (int value, _) => value >> count,
            (uint value, ExpressionType.LeftShift) => value << count,
            (uint value, _) => value >> count,
            (long value, ExpressionType.LeftShift) => value << count,
            (long value, _) => value >> count,
            (ulong value, ExpressionType.LeftShift) => value << count,
            (ulong value, _) => value >> count,
            _ => throw new($"'{left.GetType()}' passed CanRead with no '{op}' to read.")
        };

    static object Binary(ExpressionType op, bool left, bool right) =>
        op switch
        {
            ExpressionType.And or ExpressionType.AndAlso => left & right,
            ExpressionType.Or or ExpressionType.OrElse => left | right,
            ExpressionType.ExclusiveOr => left ^ right,
            ExpressionType.Equal => left == right,
            _ => left != right
        };

    static object Binary(ExpressionType op, int left, int right) =>
        unchecked(op switch
        {
            ExpressionType.Add => left + right,
            ExpressionType.Subtract => left - right,
            ExpressionType.Multiply => left * right,
            ExpressionType.Divide => left / right,
            ExpressionType.Modulo => left % right,
            ExpressionType.And => left & right,
            ExpressionType.Or => left | right,
            ExpressionType.ExclusiveOr => left ^ right,
            ExpressionType.Equal => left == right,
            ExpressionType.NotEqual => left != right,
            ExpressionType.LessThan => left < right,
            ExpressionType.LessThanOrEqual => left <= right,
            ExpressionType.GreaterThan => left > right,
            _ => left >= right
        });

    static object Binary(ExpressionType op, uint left, uint right) =>
        unchecked(op switch
        {
            ExpressionType.Add => left + right,
            ExpressionType.Subtract => left - right,
            ExpressionType.Multiply => left * right,
            ExpressionType.Divide => left / right,
            ExpressionType.Modulo => left % right,
            ExpressionType.And => left & right,
            ExpressionType.Or => left | right,
            ExpressionType.ExclusiveOr => left ^ right,
            ExpressionType.Equal => left == right,
            ExpressionType.NotEqual => left != right,
            ExpressionType.LessThan => left < right,
            ExpressionType.LessThanOrEqual => left <= right,
            ExpressionType.GreaterThan => left > right,
            _ => left >= right
        });

    static object Binary(ExpressionType op, long left, long right) =>
        unchecked(op switch
        {
            ExpressionType.Add => left + right,
            ExpressionType.Subtract => left - right,
            ExpressionType.Multiply => left * right,
            ExpressionType.Divide => left / right,
            ExpressionType.Modulo => left % right,
            ExpressionType.And => left & right,
            ExpressionType.Or => left | right,
            ExpressionType.ExclusiveOr => left ^ right,
            ExpressionType.Equal => left == right,
            ExpressionType.NotEqual => left != right,
            ExpressionType.LessThan => left < right,
            ExpressionType.LessThanOrEqual => left <= right,
            ExpressionType.GreaterThan => left > right,
            _ => left >= right
        });

    static object Binary(ExpressionType op, ulong left, ulong right) =>
        unchecked(op switch
        {
            ExpressionType.Add => left + right,
            ExpressionType.Subtract => left - right,
            ExpressionType.Multiply => left * right,
            ExpressionType.Divide => left / right,
            ExpressionType.Modulo => left % right,
            ExpressionType.And => left & right,
            ExpressionType.Or => left | right,
            ExpressionType.ExclusiveOr => left ^ right,
            ExpressionType.Equal => left == right,
            ExpressionType.NotEqual => left != right,
            ExpressionType.LessThan => left < right,
            ExpressionType.LessThanOrEqual => left <= right,
            ExpressionType.GreaterThan => left > right,
            _ => left >= right
        });

    // The floating comparisons are written out rather than deferred to Equals, which answers that a
    // NaN equals itself where the operator answers that it does not.
    static object Binary(ExpressionType op, float left, float right) =>
        op switch
        {
            ExpressionType.Add => left + right,
            ExpressionType.Subtract => left - right,
            ExpressionType.Multiply => left * right,
            ExpressionType.Divide => left / right,
            ExpressionType.Modulo => left % right,
            ExpressionType.Equal => left == right,
            ExpressionType.NotEqual => left != right,
            ExpressionType.LessThan => left < right,
            ExpressionType.LessThanOrEqual => left <= right,
            ExpressionType.GreaterThan => left > right,
            _ => left >= right
        };

    static object Binary(ExpressionType op, double left, double right) =>
        op switch
        {
            ExpressionType.Add => left + right,
            ExpressionType.Subtract => left - right,
            ExpressionType.Multiply => left * right,
            ExpressionType.Divide => left / right,
            ExpressionType.Modulo => left % right,
            ExpressionType.Equal => left == right,
            ExpressionType.NotEqual => left != right,
            ExpressionType.LessThan => left < right,
            ExpressionType.LessThanOrEqual => left <= right,
            ExpressionType.GreaterThan => left > right,
            _ => left >= right
        };
}
