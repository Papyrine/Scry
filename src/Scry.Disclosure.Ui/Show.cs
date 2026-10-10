/// <summary>
/// How the page writes the things every view shows: a time, an address, a number of bytes, a piece
/// of JSON. In one place so that a time reads the same in a table as in a heading.
/// </summary>
static class Show
{
    /// <summary>
    /// A time in UTC, to the second. Always UTC and never the reader's own zone: two people reading
    /// the same record of who saw what, in two places, should be reading the same times.
    /// </summary>
    public static string When(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string When(DateTimeOffset? at)
    {
        if (at is { } known)
        {
            return When(known);
        }

        return "";
    }

    /// <summary>The start of an address, which is enough to tell two versions of a row apart at a glance.</summary>
    public static string Short(string address)
    {
        if (address.Length <= 10)
        {
            return address;
        }

        return $"{address[..10]}…";
    }

    public static string Bytes(long count)
    {
        if (count < 1024)
        {
            return $"{count.ToString(CultureInfo.InvariantCulture)} B";
        }

        if (count < 1024 * 1024)
        {
            return $"{(count / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} KB";
        }

        return $"{(count / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }

    /// <summary>Who an answer was recorded under, with a name for nobody.</summary>
    public static string Caller(string? caller) =>
        caller ?? "(nobody)";

    /// <summary>
    /// JSON laid out to be read, where the text is JSON. Anything else — SQL, a marker for content
    /// the store no longer holds — is shown as it is.
    /// </summary>
    public static string Json(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       buffer,
                       new()
                       {
                           Indented = true
                       }))
            {
                document.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int) buffer.Length);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>What a time field shows before anything is typed in it: the one form it reads.</summary>
    public const string TimeHint = "2026-03-01 09:00";

    // A date, or a date and a time, written the one way that means the same wherever it is read. A
    // field the browser draws would show it in the reader's own order of day and month, under a
    // heading that says UTC, and be read differently by two people looking at one link.
    static string[] forms =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mm:ss"
    ];

    /// <summary>
    /// The instant a typed time names, or null where it names none: nothing typed, or something this
    /// page does not read as a time. There is no zone to type, and one zone on the page: UTC.
    /// </summary>
    public static DateTimeOffset? Instant(string? typed)
    {
        if (DateTime.TryParseExact((typed ?? "").Trim(), forms, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>Whether something was typed that is not a time. Nothing typed is not a mistake.</summary>
    public static bool Mistyped(string? typed) =>
        !string.IsNullOrWhiteSpace(typed) &&
        Instant(typed) is null;

    /// <summary>A typed time as an address carries it, or null where there is none to carry.</summary>
    public static string? Stamp(string? typed) =>
        Instant(typed)?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>What an address carries as a time, as its field shows it: to the minute, unless it has seconds.</summary>
    public static string Typed(string? stamp)
    {
        if (Instant(stamp) is not { } instant)
        {
            return "";
        }

        if (instant.Second == 0)
        {
            return instant.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return instant.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
