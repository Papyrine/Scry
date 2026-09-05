namespace Scry;

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
    /// <remarks>
    /// Judged tab by tab: a value that parses can still hold a <c>null</c> where a tab should be, or
    /// a tab whose text or id is <c>null</c>, and any of those failed the page on its first render —
    /// before the button that clears the storage was reachable. A missing tab is dropped; a tab
    /// missing its text is a blank one, and one missing its id is given one.
    /// </remarks>
    public void Load(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<Stored>(json, options);
            var readable = loaded?.Tabs?
                .Where(_ => _ is not null)
                .Select(_ => new TabState
                {
                    Id = string.IsNullOrEmpty(_!.Id) ? Guid.NewGuid().ToString("n") : _.Id,
                    Query = _.Query ?? "",
                    Title = _.Title
                })
                .ToList();
            if (readable is not { Count: > 0 })
            {
                return;
            }

            tabs.Clear();
            tabs.AddRange(readable);
            ActiveIndex = Math.Clamp(loaded!.ActiveIndex, 0, tabs.Count - 1);
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

    // The shape a stored value is read through: every field optional, so what a previous session
    // wrote — or failed to — is judged here rather than thrown at.
    sealed class Stored
    {
        public List<StoredTab?>? Tabs { get; set; }

        public int ActiveIndex { get; set; }
    }

    sealed class StoredTab
    {
        public string? Id { get; set; }

        public string? Query { get; set; }

        public string? Title { get; set; }
    }
}
