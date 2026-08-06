using System.Globalization;

/// <summary>
/// The client's one mapping from a CLR value to its wire form — the invariant-culture string plus the
/// shape tag. Shared by the query translator, which writes constants, and the attachment binder, which
/// writes key values: a key that travelled differently from the constant the same value would have
/// become is a key the server would parse differently.
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
            DateTime date => (date.ToString("o", culture), ClrTypeTag.DateTime),
            Date date => (date.ToString("yyyy-MM-dd", culture), ClrTypeTag.DateOnly),
            Guid guid => (guid.ToString(), ClrTypeTag.Guid),
            byte[] bytes => (Convert.ToBase64String(bytes), ClrTypeTag.Bytes),
            _ => (Convert.ToString(value, culture) ?? "", ClrTypeTag.String)
        };
    }
}
