/// <summary>
/// A view of the explorer and what it was asked, as the fragment of the page's address:
/// <c>#row?source=Patient&amp;key=1</c>.
/// </summary>
/// <remarks>
/// The fragment and not the path or the query string, because a browser never sends it. A link can
/// then be bookmarked or handed to a colleague while the row's key and the caller's name stay out of
/// every access log between the browser and the server — which a record of who saw what should not
/// be leaking into.
/// </remarks>
/// <param name="View">Which view: <c>row</c>, <c>caller</c>, <c>member</c>, <c>event</c> or <c>status</c>.</param>
/// <param name="Values">What it was asked, by name. A name with no value is left out.</param>
sealed record DisclosureLink(string View, IReadOnlyDictionary<string, string> Values)
{
    public const string Row = "row";
    public const string Caller = "caller";
    public const string Member = "member";
    public const string Event = "event";
    public const string Status = "status";

    static string[] views = [Row, Caller, Member, Event, Status];

    /// <summary>The view a page opened at no particular one shows.</summary>
    public static DisclosureLink Home { get; } = new(Row, new Dictionary<string, string>());

    /// <summary>What it was asked under <paramref name="name"/>, or null where it was not.</summary>
    public string? this[string name] => Values.GetValueOrDefault(name);

    /// <summary>
    /// Reads a fragment, with or without its <c>#</c>. Anything that is not a view this page has —
    /// an old link, a mistyped one — reads as <see cref="Home"/> rather than as an error: a link is
    /// somewhere to start, and the page can always start somewhere.
    /// </summary>
    public static DisclosureLink Parse(string? fragment)
    {
        var text = (fragment ?? "").TrimStart('#');
        var split = text.IndexOf('?');
        var view = text;
        var rest = "";
        if (split >= 0)
        {
            view = text[..split];
            rest = text[(split + 1)..];
        }

        if (!views.Contains(view, StringComparer.Ordinal))
        {
            return Home;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in rest.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var value = Unescape(pair[(equals + 1)..]);
            if (value.Length > 0)
            {
                values[Unescape(pair[..equals])] = value;
            }
        }

        return new(view, values);
    }

    /// <summary>The fragment, without its <c>#</c>. Names in a fixed order, so one question has one address.</summary>
    public override string ToString()
    {
        var pairs = Values
            .Where(_ => _.Value.Length > 0)
            .OrderBy(_ => _.Key, StringComparer.Ordinal)
            .Select(_ => $"{Uri.EscapeDataString(_.Key)}={Uri.EscapeDataString(_.Value)}")
            .ToList();
        if (pairs.Count == 0)
        {
            return View;
        }

        return $"{View}?{string.Join('&', pairs)}";
    }

    /// <summary>A link to <paramref name="view"/> asked the given things. A null or empty value is left out.</summary>
    public static DisclosureLink To(string view, params (string Name, string? Value)[] values)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                kept[name] = value;
            }
        }

        return new(view, kept);
    }

    // A fragment somebody edited by hand can hold an escape that is not one. It is read as the text
    // it is rather than refused.
    static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }
}
