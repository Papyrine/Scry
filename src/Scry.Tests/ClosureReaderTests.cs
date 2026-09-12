/// <summary>
/// The reader that turns a captured expression into its value without compiling it, checked against
/// the compiler it stands in for.
/// </summary>
/// <remarks>
/// Nothing here asserts a value written down by hand. A reader that disagrees with the compiler
/// sends a query built on a constant the code never wrote — a silently wrong answer rather than a
/// refusal — so every case asks both, and asserts they said the same thing. The matrices cover the
/// spelled-out half (operators, conversions) exhaustively; the shapes below cover the delegating
/// half, and are written as real lambdas so that what is tested is what Roslyn actually emits.
/// </remarks>
[TestFixture]
public class ClosureReaderTests
{
    #region Matrices

    static ExpressionType[] binaryOperators =
    [
        ExpressionType.Add, ExpressionType.Subtract, ExpressionType.Multiply,
        ExpressionType.Divide, ExpressionType.Modulo,
        ExpressionType.And, ExpressionType.Or, ExpressionType.ExclusiveOr,
        ExpressionType.AndAlso, ExpressionType.OrElse,
        ExpressionType.Equal, ExpressionType.NotEqual,
        ExpressionType.LessThan, ExpressionType.LessThanOrEqual,
        ExpressionType.GreaterThan, ExpressionType.GreaterThanOrEqual,
        ExpressionType.LeftShift, ExpressionType.RightShift,
        ExpressionType.Coalesce
    ];

    // The ends of each range, the values either side of them, and for the floating types the three
    // that are not numbers at all: the places an operator changes its mind.
    static IReadOnlyList<object?> Values(Type type) =>
        Type.GetTypeCode(type) switch
        {
            TypeCode.SByte => [(sbyte) 0, (sbyte) 1, (sbyte) -1, sbyte.MinValue, sbyte.MaxValue],
            TypeCode.Byte => [(byte) 0, (byte) 1, (byte) 200, byte.MaxValue],
            TypeCode.Int16 => [(short) 0, (short) 1, (short) -1, short.MinValue, short.MaxValue],
            TypeCode.UInt16 => [(ushort) 0, (ushort) 1, (ushort) 40000, ushort.MaxValue],
            TypeCode.Int32 => [0, 1, -1, 7, -7, int.MinValue, int.MaxValue],
            TypeCode.UInt32 => [0u, 1u, 7u, 3_000_000_000u, uint.MaxValue],
            TypeCode.Int64 => [0L, 1L, -1L, 7L, long.MinValue, long.MaxValue],
            TypeCode.UInt64 => [0ul, 1ul, 7ul, 10_000_000_000_000_000_000ul, ulong.MaxValue],
            TypeCode.Single => [0f, -0f, 1f, -1f, 2.5f, float.MinValue, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity],
            TypeCode.Double => [0d, -0d, 1d, -1d, 2.5d, double.MinValue, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity],
            TypeCode.Char => ['\0', 'a', char.MaxValue],
            TypeCode.Boolean => [true, false],
            _ => [Priority.Low, Priority.High]
        };

    static Type[] operandTypes =
    [
        typeof(bool), typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(Priority)
    ];

    // Every operator over every operand type, at every interesting pair of values. What the factory
    // refuses is a node the compiler could not have built either, and what the reader declines is
    // left to the fallback — only an answer it gives has to match.
    [Test]
    public void EveryOperatorAgreesWithTheCompiler()
    {
        var read = 0;
        foreach (var type in operandTypes)
        {
            foreach (var nullable in Lifted(type))
            {
                var values = Values(type);
                foreach (var op in binaryOperators)
                {
                    foreach (var left in Operands(values, nullable != type))
                    {
                        foreach (var right in Operands(values, nullable != type))
                        {
                            read += AssertAgreement(() => Expression.MakeBinary(
                                op,
                                Expression.Constant(left, nullable),
                                Expression.Constant(right, ShiftCount(op) ?? nullable)));
                        }
                    }
                }
            }
        }

        // The count is not the point, but a matrix the reader declined outright would pass every
        // assertion above without reading anything.
        Assert.That(read, Is.GreaterThan(2000));
    }

    [Test]
    public void EveryUnaryOperatorAgreesWithTheCompiler()
    {
        ExpressionType[] operators =
            [ExpressionType.Negate, ExpressionType.Not, ExpressionType.UnaryPlus];

        var read = 0;
        foreach (var type in operandTypes)
        {
            foreach (var nullable in Lifted(type))
            {
                foreach (var op in operators)
                {
                    foreach (var operand in Operands(Values(type), nullable != type))
                    {
                        read += AssertAgreement(() => Expression.MakeUnary(op, Expression.Constant(operand, nullable), null!));
                    }
                }
            }
        }

        Assert.That(read, Is.GreaterThan(100));
    }

    // Every numeric conversion in both directions, including the ones that truncate and the ones
    // that cannot hold what they are given.
    [Test]
    public void EveryConversionAgreesWithTheCompiler()
    {
        Type[] types =
        [
            typeof(sbyte), typeof(byte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(char), typeof(float), typeof(double), typeof(decimal), typeof(Priority)
        ];

        var read = 0;
        foreach (var from in types)
        {
            foreach (var to in types)
            {
                foreach (var nullable in Lifted(from))
                {
                    foreach (var value in Operands(Values(from), nullable != from))
                    {
                        read += AssertAgreement(() => Expression.Convert(Expression.Constant(value, nullable), to));
                        AssertAgreement(() => Expression.Convert(Expression.Constant(value, nullable), typeof(object)));
                    }
                }
            }
        }

        Assert.That(read, Is.GreaterThan(500));
    }

    static Type[] Lifted(Type type) =>
        [type, typeof(Nullable<>).MakeGenericType(type)];

    static IEnumerable<object?> Operands(IReadOnlyList<object?> values, bool includeAbsent) =>
        includeAbsent ? [..values, null] : values;

    // A shift counts in ints whatever it shifts, so the right operand of one is not the left's type.
    static Type? ShiftCount(ExpressionType op)
    {
        if (op is ExpressionType.LeftShift or ExpressionType.RightShift)
        {
            return typeof(int);
        }

        return null;
    }

    /// <summary>
    /// Asks the reader and the compiler the same expression and asserts they agree — on the value,
    /// or on the exception. Returns 1 when the reader answered, 0 when it left the shape alone.
    /// </summary>
    static int AssertAgreement(Func<Expression> build)
    {
        Expression expression;
        try
        {
            expression = build();
        }
        // A node the factory refuses is one the compiler could not have emitted.
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return 0;
        }

        // A throw out of the reader is an answer: CanRead touches nothing, so a shape it declined
        // never reaches the half that could raise.
        var answered = true;
        var actual = Outcome(() =>
        {
            answered = ClosureReader.TryRead(expression, out var value);
            return value;
        });

        if (!answered)
        {
            return 0;
        }

        var compiled = Outcome(() => Expression.Lambda(expression).Compile().DynamicInvoke());

        Assert.That(actual, Is.EqualTo(compiled), () => expression.ToString());
        return 1;
    }

    // The answer or the failure, as one comparable thing. An exception is compared by type: the two
    // paths word the message differently and neither wording is the query's.
    static object? Outcome(Func<object?> answer)
    {
        try
        {
            return answer();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        {
            return inner.GetType();
        }
        catch (Exception exception)
        {
            return exception.GetType();
        }
    }

    #endregion

    #region Shapes

    // Each of these is a shape the reader used to hand to the compiler. They are asserted through
    // the translator rather than directly, since what matters is that a real query stops compiling.
    [TestCaseSource(nameof(Shapes))]
    public void AShapeIsReadRatherThanCompiled(string name, Expression<Func<Order, bool>> predicate)
    {
        _ = name;
        Assert.That(Reads(predicate.Body), Is.True);
    }

    static IEnumerable<TestCaseData> Shapes()
    {
        var region = "north";
        var cutoff = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var ids = new List<int> {1, 2, 3};
        var size = 20;
        var flag = true;
        object boxed = region;
        Func<int, int> twice = _ => _ * 2;

        yield return Case("an instance call", _ => _.Region == region.PadLeft(8));
        yield return Case("a static call", _ => _.Amount > (decimal) Math.Clamp(1.5, 0, 2));
        yield return Case("a call over the clock", _ => _.Placed > DateTime.UtcNow.AddDays(-7));
        yield return Case("an extension call", _ => _.Id == ids.First());
        yield return Case("a constructed value", _ => _.Placed > new DateTime(2026, 1, 1));
        yield return Case("an array", _ => new[] {1, 2, 3}.Contains(_.Id));
        yield return Case("an array of the captured", _ => new[] {region, "south"}.Contains(_.Region));
        yield return Case("a collection initializer", _ => new List<int> {1, 2, size}.Contains(_.Id));
        yield return Case("an object initializer", _ => _.Region == new Bounds {Name = region}.Name);
        yield return Case("arithmetic", _ => _.Id > size * 3 + 1);
        yield return Case("a widening conversion", _ => _.Id > (int) ((long) size * 2));
        yield return Case("an operator over a decimal", _ => _.Amount > 10.5m * size);
        yield return Case("a date and a span", _ => _.Placed > cutoff + TimeSpan.FromDays(1));
        yield return Case("a conditional", _ => _.Region == (flag ? region : "south"));
        yield return Case("a coalesce", _ => _.Region == (region ?? "south"));
        yield return Case("an array index", _ => _.Id == ids.ToArray()[0]);
        yield return Case("an array length", _ => _.Id == ids.ToArray().Length);
        yield return Case("a delegate", _ => _.Id == twice(size));
        yield return Case("a type test", _ => _.Id == (boxed is string ? 1 : 2));
        yield return Case("a negation", _ => _.Id > -size);
        yield return Case("a complement", _ => _.Id > ~size);
        yield return Case("a not", _ => _.Audited == (!flag).ToString());
        yield return Case("a chain of all of it", _ => _.Placed > DateTime.UtcNow.AddDays(-(size / 4)));

        yield break;

        static TestCaseData Case(string name, Expression<Func<Order, bool>> predicate) =>
            new TestCaseData(name, predicate).SetName($"{{m}}({name})");
    }

    // The right side of each shape above is the closure half; the left is the row. Only the closure
    // half is asked of the reader.
    static bool Reads(Expression body)
    {
        var closure = body is BinaryExpression binary
            ? binary.Right
            : ((MethodCallExpression) body).Object ?? ((MethodCallExpression) body).Arguments[0];

        // The compiler prefers MemoryExtensions for an array's Contains, so the array reaches it as a
        // span through the implicit operator. The translator reads what was converted rather than the
        // span, and so does this.
        if (closure is MethodCallExpression
            {
                Method.Name: "op_Implicit",
                Arguments: [var converted]
            } &&
            closure.Type.IsByRefLike)
        {
            closure = converted;
        }

        return ClosureReader.TryRead(closure, out _);
    }

    sealed class Bounds
    {
        public string Name { get; set; } = "";
    }

    #endregion

    #region Boundaries

    // A shape the reader cannot answer has to be left whole. Reading half of one and then handing
    // the whole to the compiler runs the half twice, which for a captured property is a second read
    // and for a captured method a second call.
    [Test]
    public void AnUnreadableShapeIsNotPartlyRead()
    {
        var counting = new Counting();

        var request = Client().Source<Order>("Order", ["Region"])
            .Where(_ => counting.Ids.Where(_ => _ > 1).Contains(_.Id))
            .ToScryRequest();

        Assert.Multiple(() =>
        {
            Assert.That(((WhereOp) request.Pipeline[0]).Predicate, Is.InstanceOf<CallNode>());
            Assert.That(counting.Reads, Is.EqualTo(1));
        });
    }

    // The counterpart: a shape the reader does answer reads it once too.
    [Test]
    public void AReadableShapeIsReadOnce()
    {
        var counting = new Counting();

        Client().Source<Order>("Order", ["Region"])
            .Where(_ => counting.Ids.Contains(_.Id))
            .ToScryRequest();

        Assert.That(counting.Reads, Is.EqualTo(1));
    }

    // A sized array is left to the compiler too, and for a sharper reason than the optionals above:
    // Array.CreateInstance accepts a length the instruction refuses, so a bound past what an array
    // can hold would read as a silently smaller array where the query means an overflow.
    [Test]
    public void ASizedArrayIsLeftToTheCompiler()
    {
        var size = 3;
        Expression<Func<Order, bool>> predicate = _ => _.Id == new int[size].Length;

        Assert.That(Reads(predicate.Body), Is.False);
    }

    // A member of an optional is left to the compiler: an optional boxes as the value it holds, so
    // reflection has no Nullable<T> to find HasValue on.
    [Test]
    public void AnOptionalsOwnMembersStillTranslate()
    {
        int? maybe = 4;

        var request = Client().Source<Order>("Order", ["Region"])
            .Where(_ => _.Id == maybe.Value &&
                        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                        maybe.HasValue)
            .ToScryRequest();

        var predicate = (BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate;
        Assert.That(((ConstNode) ((BinaryNode) predicate.Left).Right).Value, Is.EqualTo("4"));
    }

    // What the closure threw is what the query raises, whichever path the shape took. The reader
    // answers the first and the compiler the second, and reflection wraps both.
    [Test]
    public void AThrowingClosureRaisesWhatItThrew()
    {
        var counting = new Counting();

        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidTimeZoneException>(
                () => Client().Source<Order>("Order", ["Region"])
                    .Where(_ => _.Region == counting.Throwing())
                    .ToScryRequest());

            Assert.Throws<InvalidTimeZoneException>(
                () => Client().Source<Order>("Order", ["Region"])
                    .Where(_ => new[] {"a"}.Select(text => counting.Throwing() + text).Contains(_.Region))
                    .ToScryRequest());
        });
    }

    sealed class Counting
    {
        public int Reads { get; private set; }

        public List<int> Ids
        {
            get
            {
                Reads++;
                return [1, 2];
            }
        }

        public string Throwing() =>
            throw new InvalidTimeZoneException($"Read {Reads} times.");
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));

    #endregion
}
