using System.Runtime.ExceptionServices;

/// <summary>
/// Reads a closure expression into the value it stands for, without compiling it into a delegate.
/// </summary>
/// <remarks>
/// Every constant in a request starts as an expression the query captured — a variable, a relative
/// date, a set tested for membership — and each one used to be compiled and invoked once. A compile
/// emits IL on a desktop runtime and builds an interpreter in a browser, and either is orders of
/// magnitude more than reading the value.
///
/// The reader answers in two passes, and the split is the point: <see cref="CanRead"/> decides from
/// the shape alone, touching nothing, and only then does <see cref="Read"/> evaluate. A one-pass
/// reader that discovered a shape it could not answer halfway through would already have run the
/// half before it, and the compiled fallback would then run that half a second time — which for a
/// captured property is a second read and for a captured method a second call.
///
/// Most of what it answers is a call: the same member the compiled form would have called, invoked
/// here instead. Those cannot disagree with the compiler, because they are not a second opinion. The
/// arithmetic and the numeric conversions are the exception — those are spelled out — so
/// <c>ClosureReaderTests</c> pins every operator, type, and conversion against the compiled answer
/// rather than against values written down by hand.
/// </remarks>
static partial class ClosureReader
{
    public static bool TryRead(Expression expression, out object? value)
    {
        if (CanRead(expression))
        {
            value = Read(expression);
            return true;
        }

        value = null;
        return false;
    }

    static bool CanRead(Expression expression)
    {
        // A ref struct is not a value this reader could hold even if it could produce one — the
        // compiler prefers the span overload of an array's Contains, so the conversion producing that
        // span is a node closure state really does reach. The translator reads what was converted
        // rather than the span (QueryTranslator.TrySequenceRead), and this is what makes the raw
        // shape a fallback rather than a reflection failure.
        if (expression.Type.IsByRefLike)
        {
            return false;
        }

        return Readable(expression);
    }

    static bool Readable(Expression expression) =>
        expression switch
        {
            ConstantExpression => true,

            // A member of an optional — its Value or its HasValue — is left to the compiler. An
            // optional boxes as the value it holds or as nothing at all, so there is no instance for
            // reflection to find Nullable<T>'s own members on.
            MemberExpression {Expression: { } owner} member =>
                !IsOptional(owner.Type) && IsReadable(member.Member) && CanRead(owner),
            MemberExpression member => IsReadable(member.Member),

            UnaryExpression unary => CanReadUnary(unary),
            BinaryExpression binary => CanReadBinary(binary),

            ConditionalExpression conditional =>
                CanRead(conditional.Test) &&
                CanRead(conditional.IfTrue) &&
                CanRead(conditional.IfFalse),

            MethodCallExpression call =>
                (call.Object is null || (!IsOptional(call.Object.Type) && CanRead(call.Object))) &&
                CanReadAll(call.Arguments),

            NewExpression {Constructor: not null} instance =>
                !IsOptional(instance.Type) && CanReadAll(instance.Arguments),

            // Only the spelled-out form. A sized array is left to the compiler: Array.CreateInstance
            // parts company with the instruction three ways — it words a negative bound differently,
            // and it accepts a length the instruction refuses, which is a silently smaller array
            // rather than the overflow the query means.
            NewArrayExpression {NodeType: ExpressionType.NewArrayInit} array => CanReadAll(array.Expressions),

            ListInitExpression list =>
                CanRead(list.NewExpression) &&
                list.Initializers.All(_ => CanReadAll(_.Arguments)),

            MemberInitExpression init =>
                CanRead(init.NewExpression) &&
                init.Bindings.All(_ => _ is MemberAssignment assignment && CanRead(assignment.Expression)),

            TypeBinaryExpression {NodeType: ExpressionType.TypeIs} typed => CanRead(typed.Expression),

            InvocationExpression invocation =>
                CanRead(invocation.Expression) &&
                CanReadAll(invocation.Arguments),

            // A lambda is the shape deliberately absent: reading one means compiling it, which is the
            // cost this exists to skip, so the call holding one falls back whole and runs once.
            _ => false
        };

    static bool CanReadAll(IReadOnlyList<Expression> expressions)
    {
        foreach (var expression in expressions)
        {
            if (!CanRead(expression))
            {
                return false;
            }
        }

        return true;
    }

    static bool IsReadable(MemberInfo member) =>
        member switch
        {
            FieldInfo => true,
            PropertyInfo property => property.GetMethod is not null && property.GetIndexParameters().Length == 0,
            _ => false
        };

    static bool CanReadUnary(UnaryExpression unary) =>
        unary.NodeType switch
        {
            ExpressionType.Convert or ExpressionType.ConvertChecked =>
                CanCoerce(unary) && CanRead(unary.Operand),
            ExpressionType.TypeAs or ExpressionType.ArrayLength =>
                CanRead(unary.Operand),
            ExpressionType.Negate or ExpressionType.Not or ExpressionType.UnaryPlus =>
                (unary.Method is not null || IsComputable(unary.Operand.Type)) && CanRead(unary.Operand),
            _ => false
        };

    static bool CanReadBinary(BinaryExpression binary)
    {
        if (!CanRead(binary.Left) ||
            !CanRead(binary.Right))
        {
            return false;
        }

        return binary.NodeType switch
        {
            // A coalesce carrying a conversion applies a lambda to the left side, which is a compile.
            ExpressionType.Coalesce => binary.Conversion is null,
            ExpressionType.ArrayIndex => true,
            _ => binary.Method is not null
                ? !IsLiftedLogical(binary)
                : CanCompute(binary)
        };
    }

    // An optional bool's & and | are three-valued — an absent and a false are a false, not an absent
    // — so they are neither the operator they name nor the propagation everything else lifted does.
    static bool IsLiftedLogical(BinaryExpression binary) =>
        binary is {
            IsLifted: true,
            NodeType: ExpressionType.And or ExpressionType.Or or
            ExpressionType.AndAlso or ExpressionType.OrElse
        } &&
        Underlying(binary.Left.Type) == typeof(bool);

    static bool CanCompute(BinaryExpression binary)
    {
        if (IsLiftedLogical(binary))
        {
            return false;
        }

        var left = Underlying(binary.Left.Type);
        var right = Underlying(binary.Right.Type);

        // A shift counts in ints whatever it is shifting.
        if (binary.NodeType is ExpressionType.LeftShift or ExpressionType.RightShift)
        {
            return right == typeof(int) && integrals.Contains(Numeric(left));
        }

        if (left != right)
        {
            return false;
        }

        // Two references compare by identity, which is what the compiler emits when neither side
        // brought an equality operator of its own.
        if (!left.IsValueType)
        {
            return binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual;
        }

        return IsComputable(left) && operators.Contains(binary.NodeType);
    }

    static readonly HashSet<ExpressionType> operators =
    [
        ExpressionType.Add, ExpressionType.Subtract, ExpressionType.Multiply,
        ExpressionType.Divide, ExpressionType.Modulo,
        ExpressionType.And, ExpressionType.Or, ExpressionType.ExclusiveOr,
        ExpressionType.AndAlso, ExpressionType.OrElse,
        ExpressionType.Equal, ExpressionType.NotEqual,
        ExpressionType.LessThan, ExpressionType.LessThanOrEqual,
        ExpressionType.GreaterThan, ExpressionType.GreaterThanOrEqual,
        ExpressionType.LeftShift, ExpressionType.RightShift
    ];

    // The checked spellings are absent throughout — Convert aside, where an identity conversion
    // cannot overflow. An overflow is the compiler's to raise, and a reader that raised it a shade
    // differently would be a difference in which queries are refused.
    static HashSet<Type> integrals =
    [
        typeof(sbyte), typeof(byte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(char)
    ];

    static bool IsComputable(Type type)
    {
        var numeric = Numeric(Underlying(type));
        return numeric == typeof(bool) ||
               numeric == typeof(float) ||
               numeric == typeof(double) ||
               integrals.Contains(numeric);
    }

    // What a type counts as: an enum counts as the integer it is written on, since that is what the
    // compiler compares and converts.
    static Type Numeric(Type type) =>
        type.IsEnum ? Enum.GetUnderlyingType(type) : type;

    static Type Underlying(Type type) =>
        Nullable.GetUnderlyingType(type) ?? type;

    static bool IsOptional(Type type) =>
        Nullable.GetUnderlyingType(type) is not null;

    // ReSharper disable TailRecursiveCall
    static object? Read(Expression expression)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                return constant.Value;

            case MemberExpression {Expression: null} member:
                return ReadMember(member.Member, null);

            case MemberExpression {Expression: { } owner} member:
                return ReadMember(member.Member, Instance(Read(owner)));

            case UnaryExpression unary:
                return ReadUnary(unary);

            case BinaryExpression binary:
                return ReadBinary(binary);

            case ConditionalExpression conditional:
                if ((bool) Read(conditional.Test)!)
                {
                    return Read(conditional.IfTrue);
                }

                return Read(conditional.IfFalse);

            case MethodCallExpression call:
                return Invoke(
                    call.Method,
                    call.Object is null ? null : Instance(Read(call.Object)),
                    ReadAll(call.Arguments));

            case NewExpression {Constructor: { } constructor} instance:
                return Invoke(constructor, null, ReadAll(instance.Arguments));

            case NewArrayExpression array:
                return ReadArray(array);

            case ListInitExpression list:
                return ReadList(list);

            case MemberInitExpression init:
                return ReadInit(init);

            case TypeBinaryExpression typed:
                return typed.TypeOperand.IsInstanceOfType(Read(typed.Expression));

            case InvocationExpression invocation:
                return InvokeDelegate(
                    (Delegate) Instance(Read(invocation.Expression)),
                    ReadAll(invocation.Arguments));

            default:
                throw new($"'{expression.NodeType}' passed CanRead with nothing to read it.");
        }
    }
    // ReSharper restore TailRecursiveCall

    // The dereference the compiled form performs. Reflection raises a complaint of its own about a
    // null target, which names reflection rather than the query.
    static object Instance(object? value) =>
        value ?? throw new NullReferenceException();

    static object?[] ReadAll(IReadOnlyList<Expression> expressions)
    {
        var values = new object?[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
        {
            values[i] = Read(expressions[i]);
        }

        return values;
    }

    static object? ReadMember(MemberInfo member, object? instance) =>
        member is FieldInfo field
            ? field.GetValue(instance)
            : ((PropertyInfo) member).GetValue(instance);

    static object ReadArray(NewArrayExpression array)
    {
        var values = Array.CreateInstance(array.Type.GetElementType()!, array.Expressions.Count);
        for (var i = 0; i < array.Expressions.Count; i++)
        {
            values.SetValue(Read(array.Expressions[i]), i);
        }

        return values;
    }

    static object ReadList(ListInitExpression list)
    {
        var instance = Invoke(list.NewExpression.Constructor!, null, ReadAll(list.NewExpression.Arguments))!;
        foreach (var initializer in list.Initializers)
        {
            Invoke(initializer.AddMethod, instance, ReadAll(initializer.Arguments));
        }

        return instance;
    }

    static object ReadInit(MemberInitExpression init)
    {
        var instance = Invoke(init.NewExpression.Constructor!, null, ReadAll(init.NewExpression.Arguments))!;
        foreach (var binding in init.Bindings)
        {
            var assignment = (MemberAssignment) binding;
            var value = Read(assignment.Expression);
            if (assignment.Member is FieldInfo field)
            {
                field.SetValue(instance, value);
            }
            else
            {
                ((PropertyInfo) assignment.Member).SetValue(instance, value);
            }
        }

        return instance;
    }

    static object? ReadUnary(UnaryExpression unary)
    {
        if (unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            return Coerce(unary, Read(unary.Operand));
        }

        var operand = Read(unary.Operand);
        switch (unary.NodeType)
        {
            case ExpressionType.TypeAs:
                return unary.Type.IsInstanceOfType(operand) ? operand : null;

            case ExpressionType.ArrayLength:
                return ((Array) Instance(operand)).Length;
        }

        // An absent operand stays absent, whichever operator was written over it.
        if (operand is null)
        {
            return null;
        }

        if (unary.Method is { } method)
        {
            return Invoke(method, null, [operand]);
        }

        return Compute(unary.NodeType, Widen(operand)!);
    }

    static object? ReadBinary(BinaryExpression binary)
    {
        // The three that stop before reading their right side, because the compiled form does.
        switch (binary.NodeType)
        {
            case ExpressionType.Coalesce:
                return Read(binary.Left) ?? Read(binary.Right);

            case ExpressionType.AndAlso:
                return (bool) Read(binary.Left)! && (bool) Read(binary.Right)!;

            case ExpressionType.OrElse:
                return (bool) Read(binary.Left)! || (bool) Read(binary.Right)!;
        }

        var left = Read(binary.Left);
        var right = Read(binary.Right);

        if (binary.NodeType == ExpressionType.ArrayIndex)
        {
            return ((Array) Instance(left)).GetValue((int) right!);
        }

        if (binary.IsLifted &&
            (left is null || right is null))
        {
            return Absent(binary, left, right);
        }

        if (binary.Method is { } method)
        {
            return Invoke(method, null, [left, right]);
        }

        if (!Underlying(binary.Left.Type).IsValueType)
        {
            return binary.NodeType == ExpressionType.Equal
                ? ReferenceEquals(left, right)
                : !ReferenceEquals(left, right);
        }

        return Compute(
            binary.NodeType,
            Widen(left)!,
            Widen(right)!);
    }

    // What a lifted operator answers when a side is absent. Equality still answers — two absent
    // values are equal — and every other comparison lifted to a plain bool is false; an operator
    // lifted to an optional result has nothing to give.
    static object? Absent(BinaryExpression binary, object? left, object? right) =>
        binary.NodeType switch
        {
            ExpressionType.Equal when !binary.IsLiftedToNull => left is null && right is null,
            ExpressionType.NotEqual when !binary.IsLiftedToNull => left is not null || right is not null,
            _ when binary.IsLiftedToNull => null,
            _ => false
        };

    // An enum compares as the integer it is written on. Only equality reaches here — the factory
    // refuses every other operator over an enum, and Roslyn converts to the underlying type before
    // comparing — so this widens rather than narrowing what an operator can answer.
    static object? Widen(object? value) =>
        value is Enum
            ? Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture)
            : value;

    static object? Invoke(MethodBase method, object? instance, object?[] arguments)
    {
        try
        {
            return method is ConstructorInfo constructor
                ? constructor.Invoke(arguments)
                : method.Invoke(instance, arguments);
        }
        // What the member threw is what the query wrote. Reflection wraps it, and so does the
        // compiled fallback's DynamicInvoke — see QueryTranslator.Evaluate, which unwraps there too,
        // so which shape a closure happened to be does not change the exception it raises.
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    static object? InvokeDelegate(Delegate target, object?[] arguments)
    {
        try
        {
            return target.DynamicInvoke(arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}
