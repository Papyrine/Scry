// The closed LINQ surface, spelled the way C# writes it rather than the way the wire carries it.
//
// Compiled into two places on purpose. The analyzer reads it to report what falls outside the set at
// compile time; Scry.Tests reads it to pin it against KnownFunction and against QueryTranslator, so a
// function added to the wire but not here surfaces as a failing test rather than as an analyzer that
// rejects a query the client would have translated happily.
//
// Nothing here is a guarantee. The client is assumed hostile, so the server re-validates every
// request against its own allow-list regardless — see docs/security.md. This exists to move a mistake
// from a stack trace to a squiggle, and nowhere else.
static class SupportedLinq
{
    public const string Queryable = "System.Linq.Queryable";
    public const string Enumerable = "System.Linq.Enumerable";
    public const string ModelAttribute = "Scry.ScryModelAttribute";
    public const string Attachment = "Scry.ScryAttachment";
    public const string Client = "Scry.ScryClient";
    public const string Extensions = "Scry.ScryQueryableExtensions";
    public const string Batch = "Scry.ScryBatchExtensions";

    /// <summary>
    /// Where the reasoning for every rule lives. Attached to each diagnostic as its help link, so the
    /// "why" is written once rather than restated in a message that has no room for it.
    /// </summary>
    public const string Docs = "https://github.com/SimonCropp/Scry/blob/main/docs/linq-coverage.md";

    /// <summary>
    /// The composing operators, and how many arguments each carries in the <c>Queryable</c> static
    /// form — the source included, which is how the call is spelled once an extension method is
    /// resolved. An operator called with an argument count that is not listed is an overload outside
    /// the set: a comparer, an element selector, or an indexed lambda.
    /// </summary>
    public static readonly Dictionary<string, int[]> Operators = new(StringComparer.Ordinal)
    {
        ["Where"] = [2],
        ["Select"] = [2],
        ["OrderBy"] = [2],
        ["OrderByDescending"] = [2],
        ["ThenBy"] = [2],
        ["ThenByDescending"] = [2],
        ["Skip"] = [2],
        ["Take"] = [2],
        // The 3-argument form is the result selector, which unfolds into GroupBy + Select; the
        // element-selector spelling of the same arity is told apart by its lambda and reported.
        ["GroupBy"] = [2, 3],
        ["OfType"] = [1],
        ["SelectMany"] = [2],
        ["Distinct"] = [1],
        ["Reverse"] = [1],
        ["Union"] = [2],
        ["Concat"] = [2],
        ["Intersect"] = [2],
        ["Except"] = [2],
        ["Join"] = [5],
        ["LeftJoin"] = [5],
        ["RightJoin"] = [5],
        ["GroupJoin"] = [5]
    };

    /// <summary>
    /// Operators a query may carry at most once. Each is rejected on the second occurrence by
    /// <c>QueryValidator</c> — server-side, so today the cost of writing two is a round trip.
    /// </summary>
    public static readonly Dictionary<string, string> SingleUse = new(StringComparer.Ordinal)
    {
        ["Select"] = "Select",
        ["Distinct"] = "Distinct",
        ["GroupBy"] = "GroupBy",
        ["SelectMany"] = "SelectMany",
        ["Join"] = "Join",
        ["LeftJoin"] = "Join",
        ["RightJoin"] = "Join",
        ["GroupJoin"] = "Join"
    };

    /// <summary>The operators that establish an ordering, which <c>Reverse</c> requires.</summary>
    public static readonly HashSet<string> Ordering = new(StringComparer.Ordinal)
    {
        "OrderBy",
        "OrderByDescending",
        "ThenBy",
        "ThenByDescending"
    };

    /// <summary>
    /// The types whose members read as date functions rather than as a member path. Kept in step with
    /// <c>QueryTranslator.IsTemporal</c>.
    /// </summary>
    public static readonly HashSet<string> Temporal = new(StringComparer.Ordinal)
    {
        "System.DateTime",
        "System.DateOnly",
        "System.DateTimeOffset",
        "System.TimeOnly"
    };

    /// <summary>
    /// Every callable member on a scalar, as <c>Owner.Member/arity</c>, paired with the
    /// <see cref="Scry.KnownFunction"/> it becomes. An empty function name marks a member the
    /// translator carries as something other than a call — <c>string.Equals</c> under a
    /// <c>StringComparison</c> becomes a collated comparison — so it belongs to the surface without
    /// contributing to the wire's function set.
    /// </summary>
    public static readonly (string Signature, string Function)[] Functions =
    [
        ("System.String.Contains/1", "StringContains"),
        ("System.String.Contains/2", "StringContains"),
        ("System.String.StartsWith/1", "StringStartsWith"),
        ("System.String.StartsWith/2", "StringStartsWith"),
        ("System.String.EndsWith/1", "StringEndsWith"),
        ("System.String.EndsWith/2", "StringEndsWith"),
        ("System.String.Equals/2", ""),
        ("System.String.ToLower/0", "StringToLower"),
        ("System.String.ToUpper/0", "StringToUpper"),
        ("System.String.IsNullOrEmpty/1", "StringIsNullOrEmpty"),
        ("System.String.IsNullOrWhiteSpace/1", "StringIsNullOrWhiteSpace"),
        ("System.String.Length/0", "StringLength"),
        ("System.String.Trim/0", "StringTrim"),
        ("System.String.TrimStart/0", "StringTrimStart"),
        ("System.String.TrimEnd/0", "StringTrimEnd"),
        ("System.String.Substring/1", "StringSubstring"),
        ("System.String.Substring/2", "StringSubstring"),
        ("System.String.IndexOf/1", "StringIndexOf"),
        ("System.String.Replace/2", "StringReplace"),
        ("System.String.Concat/1", "StringConcat"),
        ("System.String.Concat/2", "StringConcat"),
        ("System.String.Concat/3", "StringConcat"),
        ("System.String.Concat/4", "StringConcat"),
        // Carried as the text aggregate rather than a function: string.Join over a group becomes
        // AggregateFn.Join on the wire.
        ("System.String.Join/2", ""),
        ("System.Object.ToString/0", "StringFrom"),
        ("System.Math.Abs/1", "MathAbs"),
        ("System.Math.Ceiling/1", "MathCeiling"),
        ("System.Math.Floor/1", "MathFloor"),
        ("System.Math.Round/1", "MathRound"),
        ("System.Math.Round/2", "MathRound"),
        ("System.Math.Truncate/1", "MathTruncate"),
        ("System.Math.Sign/1", "MathSign"),
        ("System.Math.Sqrt/1", "MathSqrt"),
        ("System.Math.Pow/2", "MathPow"),
        ("System.Math.Exp/1", "MathExp"),
        ("System.Math.Log/1", "MathLog"),
        ("System.Math.Log/2", "MathLog"),
        ("System.Math.Log10/1", "MathLog10"),
        ("System.Math.Sin/1", "MathSin"),
        ("System.Math.Cos/1", "MathCos"),
        ("System.Math.Tan/1", "MathTan"),
        ("System.Math.Asin/1", "MathAsin"),
        ("System.Math.Acos/1", "MathAcos"),
        ("System.Math.Atan/1", "MathAtan"),
        ("System.Math.Atan2/2", "MathAtan2"),
        ("System.Math.Max/2", "MathMax"),
        ("System.Math.Min/2", "MathMin"),
        ("System.Double.DegreesToRadians/1", "MathDegreesToRadians"),
        ("System.Double.RadiansToDegrees/1", "MathRadiansToDegrees"),
        ("System.Single.DegreesToRadians/1", "MathDegreesToRadians"),
        ("System.Single.RadiansToDegrees/1", "MathRadiansToDegrees"),
        ("$temporal.Year/0", "DateYear"),
        ("$temporal.Month/0", "DateMonth"),
        ("$temporal.Day/0", "DateDay"),
        ("$temporal.Hour/0", "DateHour"),
        ("$temporal.Minute/0", "DateMinute"),
        ("$temporal.Second/0", "DateSecond"),
        ("$temporal.Millisecond/0", "DateMillisecond"),
        ("$temporal.DayOfYear/0", "DateDayOfYear"),
        ("$temporal.DayOfWeek/0", "DateDayOfWeek"),
        ("$temporal.Date/0", "DateDate"),
        ("$temporal.AddYears/1", "DateAddYears"),
        ("$temporal.AddMonths/1", "DateAddMonths"),
        ("$temporal.AddDays/1", "DateAddDays"),
        ("$temporal.AddHours/1", "DateAddHours"),
        ("$temporal.AddMinutes/1", "DateAddMinutes"),
        ("$temporal.AddSeconds/1", "DateAddSeconds"),
        ("$temporal.AddMilliseconds/1", "DateAddMilliseconds"),
        ("System.Enum.HasFlag/1", "EnumHasFlag"),
        ("System.Int32.Parse/1", "Int32From"),
        ("System.Int64.Parse/1", "Int64From"),
        ("System.Decimal.Parse/1", "DecimalFrom"),
        ("System.Double.Parse/1", "DoubleFrom"),
        ("System.Convert.ToInt32/1", "Int32From"),
        ("System.Convert.ToInt64/1", "Int64From"),
        ("System.Convert.ToDecimal/1", "DecimalFrom"),
        ("System.Convert.ToDouble/1", "DoubleFrom"),
        ("System.Convert.ToString/1", "StringFrom"),
        ("System.Boolean.Parse/1", "BooleanFrom"),
        ("System.Byte.Parse/1", "ByteFrom"),
        ("System.Int16.Parse/1", "Int16From"),
        ("System.Single.Parse/1", "SingleFrom"),
        ("System.Convert.ToBoolean/1", "BooleanFrom"),
        ("System.Convert.ToByte/1", "ByteFrom"),
        ("System.Convert.ToInt16/1", "Int16From"),
        ("System.String.CompareTo/1", "CompareTo"),
        ("System.String.Compare/2", "CompareTo"),
        ("$temporal.CompareTo/1", "CompareTo"),
        ("System.Byte.CompareTo/1", "CompareTo"),
        ("System.SByte.CompareTo/1", "CompareTo"),
        ("System.Int16.CompareTo/1", "CompareTo"),
        ("System.UInt16.CompareTo/1", "CompareTo"),
        ("System.Int32.CompareTo/1", "CompareTo"),
        ("System.UInt32.CompareTo/1", "CompareTo"),
        ("System.Int64.CompareTo/1", "CompareTo"),
        ("System.UInt64.CompareTo/1", "CompareTo"),
        ("System.Single.CompareTo/1", "CompareTo"),
        ("System.Double.CompareTo/1", "CompareTo"),
        ("System.Decimal.CompareTo/1", "CompareTo"),
        // Not calls of a wire function: GetValueOrDefault is the coalesce it abbreviates, carried as
        // the ordinary binary operator.
        ("$nullable.GetValueOrDefault/0", ""),
        ("$nullable.GetValueOrDefault/1", ""),
        // Not written as a call. A client-supplied set reaches the wire as In through Contains over a
        // closure collection, which is an Enumerable call rather than a member of a scalar.
        ("$set.Contains/1", "In")
    ];

    /// <summary>
    /// The signature every temporal type shares, since the four spell the same members. A member on a
    /// scalar is looked up under the type's own name, falling back to this for a temporal one.
    /// </summary>
    public const string TemporalOwner = "$temporal";

    /// <summary>The signature <c>Nullable&lt;T&gt;</c>'s members are looked up under, whatever the T.</summary>
    public const string NullableOwner = "$nullable";

    static readonly HashSet<string> signatures = Build();

    static HashSet<string> Build()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (signature, _) in Functions)
        {
            set.Add(signature);
        }

        return set;
    }

    /// <summary>
    /// Whether a member read off a scalar — a string, a date, or one of <c>Math</c>'s statics — is one
    /// the wire can carry. Only asked about scalars: the same name over a collection, a group, or
    /// another source means something else entirely, and guessing which would cost precision the
    /// analyzer cannot afford.
    /// </summary>
    public static bool IsFunction(string owner, string member, int arity) =>
        signatures.Contains($"{owner}.{member}/{arity}");

    /// <summary>Whether any overload of the member is callable, whatever its arity.</summary>
    public static bool IsFunctionName(string owner, string member)
    {
        var prefix = $"{owner}.{member}/";
        foreach (var signature in signatures)
        {
            if (signature.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
