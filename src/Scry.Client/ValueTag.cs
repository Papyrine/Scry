/// <summary>
/// The client's one mapping from a CLR value to its wire form — the invariant-culture string plus the
/// shape tag. Shared by the query translator, which writes constants, and the attachment binder, which
/// writes key values: a key that travelled differently from the constant the same value would have
/// become is a key the server would parse differently.
///
/// The server keeps its own half of this mapping in <c>CursorCodec.TagValue</c>, which the two
/// packages cannot share. Change a spelling here and change it there.
/// </summary>
static class ValueTag
{
    public static (string? Value, ClrTypeTag Tag) Of(object? value)
    {
        var culture = CultureInfo.InvariantCulture;
        return value switch
        {
            null => (null, ClrTypeTag.Null),
            string text => (text, ClrTypeTag.String),
            // No tag of its own; the server reconciles it against the member's type. A comparison
            // promotes char to int, so the value often arrives as a code point instead — which the
            // server also accepts.
            char character => (character.ToString(), ClrTypeTag.String),
            bool flag => (flag ? "true" : "false", ClrTypeTag.Boolean),
            // Compared against a day-of-week the server computes as a number, and not part of any
            // model's schema, so this one enum travels as its value rather than by name.
            DayOfWeek day => (((int) day).ToString(culture), ClrTypeTag.Int32),
            Enum enumeration => (enumeration.ToString(), ClrTypeTag.Enum),
            int number => (number.ToString(culture), ClrTypeTag.Int32),
            long number => (number.ToString(culture), ClrTypeTag.Int64),
            short number => (number.ToString(culture), ClrTypeTag.Int32),
            byte number => (number.ToString(culture), ClrTypeTag.Int32),
            decimal number => (number.ToString(culture), ClrTypeTag.Decimal),
            double number => (number.ToString(culture), ClrTypeTag.Double),
            float number => (number.ToString(culture), ClrTypeTag.Double),
            DateTime date => (Timestamp(date, culture), ClrTypeTag.DateTime),
            Date date => (date.ToString("yyyy-MM-dd", culture), ClrTypeTag.DateOnly),
            // Neither has a tag of its own, so both would otherwise fall to the text form below —
            // where the default spelling of an offset drops its sub-second part, and the default
            // spelling of a time of day drops its seconds as well. The round-trip forms carry the
            // whole value, and the server's own parse reads either back exactly.
            DateTimeOffset stamped => (stamped.ToString("o", culture), ClrTypeTag.String),
            Time time => (time.ToString("o", culture), ClrTypeTag.String),
            Guid guid => (guid.ToString(), ClrTypeTag.Guid),
            byte[] bytes => (Convert.ToBase64String(bytes), ClrTypeTag.Bytes),
            _ => (Convert.ToString(value, culture) ?? "", ClrTypeTag.String)
        };
    }

    /// <summary>
    /// A timestamp as the wall clock it names, plus the Z that marks a UTC one.
    /// </summary>
    /// <remarks>
    /// A local <see cref="DateTimeKind"/> is flattened to that wall clock rather than carrying the
    /// client's offset. An offset on the wire is read back against the <em>server's</em> zone — the
    /// value the provider then binds is the same instant re-spelled, so one request names a different
    /// moment on two deployments. That is the dependency <c>LocalDateTime</c> and <c>DayOfWeek</c> are
    /// refused for, and a constant may not smuggle it in silently. Flattened, what the client wrote as
    /// its own wall clock reaches SQL as that wall clock, exactly as the same LINQ would in process,
    /// and identically wherever the server runs.
    /// </remarks>
    static string Timestamp(DateTime date, CultureInfo culture) =>
        date.Kind == DateTimeKind.Local
            ? date.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", culture)
            : date.ToString("o", culture);
}
