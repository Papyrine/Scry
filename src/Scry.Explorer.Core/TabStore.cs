namespace Scry;

/// <summary>The open query tabs and which one is active.</summary>
public sealed class TabStore
{
    static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    readonly List<TabState> tabs = [];

    // Every tab id this store has held, the closed ones included. What Merge reads out of the store
    // is judged against it: a tab that was here and was closed is not another window's to reopen.
    readonly HashSet<string> seen = [];

    public IReadOnlyList<TabState> Tabs =>
        tabs;

    public int ActiveIndex { get; private set; }

    public TabState Active =>
        tabs[ActiveIndex];

    public TabStore(string initialQuery = "") =>
        Open(new()
        {
            Query = initialQuery
        });

    /// <summary>Back to one tab carrying the seeded query — what clearing the stored data does.</summary>
    public void Reset(string initialQuery = "")
    {
        tabs.Clear();
        Open(new()
        {
            Query = initialQuery
        });
        ActiveIndex = 0;
    }

    public void Add(string query = "")
    {
        Open(new()
        {
            Query = query
        });
        ActiveIndex = tabs.Count - 1;
    }

    void Open(TabState tab)
    {
        tabs.Add(tab);
        seen.Add(tab.Id);
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
        if (Read(json) is not { } read)
        {
            return;
        }

        var (readable, activeIndex) = read;
        tabs.Clear();
        foreach (var tab in readable)
        {
            Open(tab);
        }

        ActiveIndex = Math.Clamp(activeIndex, 0, tabs.Count - 1);
    }

    /// <summary>
    /// Adopts the tabs another window of this explorer has written to the store since this one last
    /// read it, so that what this window writes next carries both windows' tabs rather than only its
    /// own. A tab this window has held before — open now, or closed here — is not adopted again.
    /// </summary>
    /// <returns>Whether anything was adopted.</returns>
    public bool Merge(string? stored)
    {
        if (Read(stored) is not { } written)
        {
            return false;
        }

        var adopted = false;
        foreach (var tab in written.Tabs)
        {
            if (!seen.Add(tab.Id))
            {
                continue;
            }

            tabs.Add(tab);
            adopted = true;
        }

        return adopted;
    }

    // The tabs a stored value holds, judged tab by tab, or null for a value holding none.
    static (List<TabState> Tabs, int ActiveIndex)? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
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
            return readable is { Count: > 0 } ? (readable, loaded!.ActiveIndex) : null;
        }
        catch (JsonException)
        {
            // Corrupt or from a shape this version does not read. Keep the tab already open.
            return null;
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
