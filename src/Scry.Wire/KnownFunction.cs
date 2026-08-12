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

    /// <summary>
    /// The first and last character of a string, as <c>FirstOrDefault</c> and <c>LastOrDefault</c>
    /// spell them — a substring of one, taken at either end. The indexer that looks like it means the
    /// same is not carried: no provider translates it, and one that reads past the end of the text
    /// would fault where these answer with the default.
    /// </summary>
    StringFirst,
    StringLast,
    DateYear,
    DateMonth,
    DateDay,
    DateHour,
    DateMinute,
    DateSecond,
    DateMillisecond,
    DateDayOfYear,

    /// <summary>
    /// The sub-millisecond parts, each within the one above it: 0-999 microseconds of the
    /// millisecond, 0-999 nanoseconds of the microsecond. SQL Server's DATEPART counts them from the
    /// whole second, so the server takes the remainder, exactly as EF does.
    /// </summary>
    DateMicrosecond,
    DateNanosecond,

    /// <summary>The count of days since 0001-01-01 (<c>DateOnly.DayNumber</c>).</summary>
    DateDayNumber,

    /// <summary>
    /// The day of the week, numbered as <see cref="System.DayOfWeek"/> does — 0 for Sunday. The server
    /// owns how that is expressed in SQL, since the obvious formulation is not deterministic.
    /// </summary>
    DateDayOfWeek,
    DateDate,

    /// <summary>
    /// The time of day a date carries, as the <see cref="System.TimeSpan"/> since midnight. The
    /// counterpart of <see cref="DateDate"/>, which drops the same part instead of keeping it.
    /// </summary>
    DateTimeOfDay,

    /// <summary>
    /// The parts of an elapsed time, each within the unit above it — the hours of the day, the
    /// minutes of the hour, and so on down. Whole totals (<c>TotalHours</c> and its siblings) are a
    /// division rather than a part and no provider translates them, so they are not carried.
    /// </summary>
    TimeSpanHours,
    TimeSpanMinutes,
    TimeSpanSeconds,
    TimeSpanMilliseconds,
    TimeSpanMicroseconds,
    TimeSpanNanoseconds,

    /// <summary>
    /// Reading one temporal type as another: the date or the time half of a timestamp, a time read as
    /// an elapsed time, and a date and a time composed back into one. Each is a conversion the
    /// database performs, so the answer does not depend on the client's calendar or its clock.
    /// </summary>
    DateOnlyFromDateTime,
    TimeOnlyFromDateTime,
    TimeOnlyFromTimeSpan,
    DateTimeFromDateAndTime,

    /// <summary>
    /// Unix time, counted from 1970-01-01 UTC (<c>DateTimeOffset.ToUnixTimeSeconds</c>). The
    /// <c>DateTime</c> / <c>UtcDateTime</c> / <c>LocalDateTime</c> readings of an offset are not
    /// carried alongside them: the provider has a translation only for a column whose store type is
    /// <c>datetimeoffset</c> and refuses the expression otherwise, and the local reading would go
    /// through <c>CURRENT_TIMEZONE_ID()</c> — the server's own zone — even where it does translate.
    /// </summary>
    UnixSecondsFromOffset,
    UnixMillisecondsFromOffset,

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
    /// Degrees to radians and back (<c>double.DegreesToRadians</c> / <c>RadiansToDegrees</c> —
    /// statics on the floating types rather than on <c>Math</c>). Defined over double alone, so the
    /// target is widened to reach them.
    /// </summary>
    MathDegreesToRadians,
    MathRadiansToDegrees,

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
    CompareTo,

    /// <summary>
    /// Questions about a binary member's bytes, without reading them: how many there are
    /// (<c>DATALENGTH</c>), whether a byte is among them (<c>CHARINDEX</c>), and the byte at one
    /// position. An <c>[Attachment]</c> answers none of them — its value is the one thing no query
    /// reads — so these reach a plain or <c>[BinaryTransfer]</c> member only. <c>Any()</c> is absent
    /// because the provider refuses it; ask whether <see cref="BytesLength"/> is above zero, which is
    /// the same question and does translate.
    /// </summary>
    BytesLength,
    BytesContains,
    BytesElementAt
}
// end-snippet
