namespace Scry;

/// <summary>One query tab: the text being edited, and a title if the user renamed it.</summary>
/// <remarks>
/// What a run produced is deliberately absent. A response is a fact about a moment — the rows a
/// server held then, under the policies that applied then — so restoring one on a later visit would
/// be showing something that may no longer be true, and quietly.
/// </remarks>
public sealed class TabState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Query { get; set; } = "";

    /// <summary>A title the user typed. Absent means the title is derived from the query.</summary>
    public string? Title { get; set; }
}

/// <summary>The open query tabs and which one is active.</summary>
public sealed class TabStore
{
    static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    readonly List<TabState> tabs = [];

    public IReadOnlyList<TabState> Tabs =>
        tabs;

    public int ActiveIndex { get; private set; }

    public TabState Active =>
        tabs[ActiveIndex];

    public TabStore(string initialQuery = "") =>
        tabs.Add(new()
        {
            Query = initialQuery
        });

    public void Add(string query = "")
    {
        tabs.Add(new()
        {
            Query = query
        });
        ActiveIndex = tabs.Count - 1;
    }

    public void Activate(int index)
    {
        if (index >= 0 &&
            index < tabs.Count)
        {
            ActiveIndex = index;
        }
    }

    /// <summary>
    /// Closes a tab. The last one is never closed: an explorer with no tab has nowhere to type, and
    /// the close button is hidden rather than disabled once only one is left.
    /// </summary>
    public void Close(int index)
    {
        if (tabs.Count == 1 ||
            index < 0 ||
            index >= tabs.Count)
        {
            return;
        }

        tabs.RemoveAt(index);
        ActiveIndex = Math.Clamp(ActiveIndex > index ? ActiveIndex - 1 : ActiveIndex, 0, tabs.Count - 1);
    }

    public void Rename(int index, string? title)
    {
        if (index < 0 ||
            index >= tabs.Count)
        {
            return;
        }

        var trimmed = title?.Trim();
        tabs[index].Title = trimmed is { Length: > 0 } ? trimmed : null;
    }

    /// <summary>
    /// The name on the tab: what the user typed, else the source the query reads, else a number. The
    /// source is what distinguishes two tabs in practice — an explorer session is usually one tab per
    /// thing being looked at.
    /// </summary>
    public string Title(TabState tab) =>
        tab.Title ?? SourceOf(tab.Query) ?? $"Query {tabs.IndexOf(tab) + 1}";

    /// <summary>
    /// The source a snippet reads, taken from the first <c>Query.Name</c> in it, or null. Deliberately
    /// textual rather than parsed: this runs on every keystroke of the active tab, and a title is not
    /// worth a syntax tree.
    /// </summary>
    public static string? SourceOf(string query)
    {
        const string root = "Query.";
        var start = query.IndexOf(root, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += root.Length;
        var end = start;
        while (end < query.Length &&
               (char.IsLetterOrDigit(query[end]) || query[end] == '_'))
        {
            end++;
        }

        return end > start ? query[start..end] : null;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(
            new Persisted
            {
                Tabs = tabs,
                ActiveIndex = ActiveIndex
            },
            options);

    /// <summary>
    /// Replaces the contents from a stored value. Anything that does not parse leaves the store as it
    /// was — a single empty tab — rather than failing the page.
    /// </summary>
    public void Load(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<Persisted>(json, options);
            if (loaded?.Tabs is not { Count: > 0 })
            {
                return;
            }

            tabs.Clear();
            tabs.AddRange(loaded.Tabs);
            ActiveIndex = Math.Clamp(loaded.ActiveIndex, 0, tabs.Count - 1);
        }
        catch (JsonException)
        {
            // Corrupt or from a shape this version does not read. Keep the tab already open.
        }
    }

    sealed class Persisted
    {
        public List<TabState> Tabs { get; set; } = [];

        public int ActiveIndex { get; set; }
    }
}
