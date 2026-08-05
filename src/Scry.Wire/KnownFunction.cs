namespace Scry;

// begin-snippet: wireFunctions
/// <summary>The closed set of functions a client may call on a value. No free-form method names.</summary>
public enum KnownFunction
{
    StringContains,
    StringStartsWith,
    StringEndsWith,
    StringToLower,
    StringToUpper,
    StringIsNullOrEmpty,
    StringIsNullOrWhiteSpace,
    StringLength,
    StringTrim,
    StringTrimStart,
    StringTrimEnd,
    StringSubstring,
    StringIndexOf,
    StringReplace,
    DateYear,
    DateMonth,
    DateDay,
    DateHour,
    DateMinute,
    DateSecond,
    DateMillisecond,
    DateDayOfYear,

    /// <summary>
    /// The day of the week, numbered as <see cref="System.DayOfWeek"/> does — 0 for Sunday. The server
    /// owns how that is expressed in SQL, since the obvious formulation is not deterministic.
    /// </summary>
    DateDayOfWeek,
    DateDate,
    DateAddYears,
    DateAddMonths,
    DateAddDays,
    DateAddHours,
    DateAddMinutes,
    DateAddSeconds,
    DateAddMilliseconds,
    /// <summary>
    /// Joins the target and the argument into one string, converting either if it is not one already.
    /// C# writes this as <c>+</c>, but the operator alone does not say it: an Add of a string and a
    /// number is a concatenation, while an Add of two numbers is arithmetic, and only the client can
    /// tell which was written.
    /// </summary>
    StringConcat,

    /// <summary>
    /// The target's value as text — <c>ToString()</c> with no arguments. The formatted overload is not
    /// part of the set: no provider translates it, and the SQL function that would express it reads
    /// the server's language, so the same row would format differently per connection.
    /// </summary>
    StringFrom,

    MathAbs,
    MathCeiling,
    MathFloor,
    MathRound,
    MathTruncate,
    /// <summary>
    /// The sign of the target: -1, 0, or 1. The server composes it from comparisons rather than from
    /// SQL's own function, whose result takes the argument's type and so cannot be read back as the
    /// <see cref="int"/> this returns.
    /// </summary>
    MathSign,

    MathSqrt,
    MathPow,
    MathExp,

    /// <summary>Natural logarithm, or — with one argument — the logarithm to that base.</summary>
    MathLog,
    MathLog10,
    MathSin,
    MathCos,
    MathTan,
    MathAsin,
    MathAcos,
    MathAtan,

    /// <summary>The angle whose tangent is the target over the argument (<c>Math.Atan2(y, x)</c>).</summary>
    MathAtan2,

    /// <summary>
    /// The greater / lesser of the target and the argument (<c>Math.Max</c> / <c>Math.Min</c>). The
    /// server composes each from a comparison rather than using SQL's GREATEST and LEAST, which exist
    /// only from SQL Server 2022; a null operand keeps the answer null.
    /// </summary>
    MathMax,
    MathMin,

    /// <summary>
    /// Membership of a client-supplied set (SQL <c>IN</c>). The target is the value being tested and
    /// every argument is a <see cref="ConstNode"/>; the server caps the number of values.
    /// </summary>
    In,

    /// <summary>
    /// Whether the target — a [Flags] enum member — carries the argument's bits
    /// (<c>Enum.HasFlag</c>). A combined flag travels by name exactly as <c>Enum.ToString</c> spells
    /// it: <c>"Parking, Gym"</c>.
    /// </summary>
    EnumHasFlag,

    /// <summary>
    /// Reads text as a value — <c>int.Parse</c> / <c>Convert.ToInt32</c> and their siblings; the
    /// inverse of <see cref="StringFrom"/>. Only that direction exists: a numeric member is already a
    /// value, and SQL's numeric-to-numeric conversions truncate where the CLR's round, so those are
    /// not carried. Text that does not parse faults at execution, exactly as it would in memory.
    /// </summary>
    Int32From,
    Int64From,
    DecimalFrom,
    DoubleFrom,
    BooleanFrom,
    ByteFrom,
    Int16From,
    SingleFrom,

    /// <summary>
    /// Three-way comparison (<c>a.CompareTo(b)</c>, <c>string.Compare(a, b)</c>): -1, 0, or 1, or
    /// null when either operand is — a comparison against a value that is not there has no direction.
    /// Numbers, text and dates compare; text compares under the server's collation, exactly as its
    /// ordering does.
    /// </summary>
    CompareTo
}
// end-snippet
