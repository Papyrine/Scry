namespace Scry;

/// <summary>One remembered query, with the label and favorite mark the history pane can attach to it.</summary>
public sealed class HistoryItem
{
    public string Query { get; set; } = "";

    /// <summary>A name the user typed for this entry, shown instead of the query text.</summary>
    public string? Label { get; set; }

    /// <summary>Favorites are never evicted by the cap, and are listed above the rest.</summary>
    public bool Favorite { get; set; }
}

/// <summary>
/// The queries this browser remembers. Ordinary entries are a capped, newest-first, deduplicated list;
/// favorites sit outside the cap and are never evicted.
/// </summary>
/// <remarks>
/// The history is this browser's alone — nothing here is ever sent to the server.
/// </remarks>
public sealed class HistoryStore
{
    /// <summary>How many non-favorite entries are kept.</summary>
    public const int MaxItems = 20;

    /// <summary>The storage key the list is persisted under.</summary>
    public const string Key = "queries";

    /// <summary>
    /// The key the explorer used before entries carried labels and favorites, when the value was a
    /// plain array of query strings. Read once so an upgrade does not discard anyone's history.
    /// </summary>
    public const string LegacyKey = "scry-history";

    static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    readonly List<HistoryItem> items = [];

    /// <summary>Favorites first, then the ordinary entries, both newest-first.</summary>
    public IReadOnlyList<HistoryItem> Items =>
        [.. items.Where(_ => _.Favorite), .. items.Where(_ => !_.Favorite)];

    public int Count =>
        items.Count;

    /// <summary>
    /// Records a query at the head of the list. An exact repeat moves the existing entry up rather
    /// than adding a second, so its label and favorite mark survive.
    /// </summary>
    public void Add(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return;
        }

        var existing = items.FirstOrDefault(_ => _.Query == query);
        if (existing is not null)
        {
            items.Remove(existing);
            items.Insert(0, existing);
            return;
        }

        items.Insert(0, new()
        {
            Query = query
        });

        Evict();
    }

    // Removal is by text rather than by index: entries are deduplicated on exactly that, so it
    // identifies one, and it survives the list having moved under a render the click raced.
    public void Remove(string query) =>
        items.RemoveAll(_ => _.Query == query);

    /// <summary>Forgets the ordinary entries. Favorites are kept — losing one to this is not recoverable.</summary>
    public void Clear() =>
        items.RemoveAll(_ => !_.Favorite);

    public void SetLabel(string query, string? label)
    {
        var item = items.FirstOrDefault(_ => _.Query == query);
        if (item is null)
        {
            return;
        }

        var trimmed = label?.Trim();
        if (trimmed is { Length: > 0 })
        {
            item.Label = trimmed;
            return;
        }

        item.Label = null;
    }

    /// <summary>Marks or unmarks a favorite. Unmarking one puts it back under the cap.</summary>
    public void SetFavorite(string query, bool favorite)
    {
        var item = items.FirstOrDefault(_ => _.Query == query);
        if (item is null)
        {
            return;
        }

        item.Favorite = favorite;
        if (!favorite)
        {
            Evict();
        }
    }

    /// <summary>
    /// Whether an entry matches the pane's search box. Both the label and the query text are searched,
    /// so an entry found by either spelling is found.
    /// </summary>
    public static bool Matches(HistoryItem item, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return item.Query.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               (item.Label?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// The one-line text the pane shows: the label if there is one, otherwise the query flattened.
    /// </summary>
    public static string DisplayText(HistoryItem item) =>
        item.Label ?? Flatten(item.Query);

    // The list shows a query on one line; the stored text keeps its formatting for the editor. Lines
    // are joined by trimming each and appending a continuation (a line starting with '.') directly, so
    // a multi-line query reads as the fluent chain it is rather than carrying its indentation along as
    // stray spaces before every operator.
    public static string Flatten(string query)
    {
        var builder = new StringBuilder();
        foreach (var line in query.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0 &&
                !trimmed.StartsWith('.'))
            {
                builder.Append(' ');
            }

            builder.Append(trimmed);
        }

        return builder.ToString();
    }

    public string Serialize() =>
        JsonSerializer.Serialize(items, options);

    /// <summary>
    /// Replaces the contents from a stored value. Anything that does not parse is treated as an empty
    /// history rather than surfaced — a debug convenience never fails the page.
    /// </summary>
    /// <remarks>
    /// Judged entry by entry, not as a whole: a value that parses can still hold a <c>null</c> where an
    /// entry should be, or an entry with no text, and either would have failed the page on its first
    /// render — before the button that clears the storage was reachable. Those entries are dropped and
    /// the rest are kept.
    /// </remarks>
    public void Load(string? json)
    {
        items.Clear();
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<HistoryItem?>>(json, options);
            if (loaded is not null)
            {
                items.AddRange(loaded.Where(_ => !string.IsNullOrWhiteSpace(_?.Query))!);
                Evict();
            }
        }
        catch (JsonException)
        {
            // Corrupt or from a shape this version does not read. Start empty.
        }
    }

    /// <summary>
    /// Adopts a value written under <see cref="LegacyKey"/> — a plain array of query strings — as
    /// unlabelled, non-favorite entries, newest-first as they were stored.
    /// </summary>
    public void LoadLegacy(string? json)
    {
        items.Clear();
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var queries = JsonSerializer.Deserialize<List<string?>>(json);
            if (queries is null)
            {
                return;
            }

            items.AddRange(
                queries
                    .Where(_ => !string.IsNullOrWhiteSpace(_))
                    .Select(_ => new HistoryItem
                    {
                        Query = _!
                    }));
            Evict();
        }
        catch (JsonException)
        {
            // Corrupt. Start empty.
        }
    }

    // The cap counts only the ordinary entries: a favorite is a deliberate keep, so it neither
    // occupies a slot nor is evicted from one.
    void Evict()
    {
        var ordinary = items.Count(_ => !_.Favorite);
        for (var index = items.Count - 1; index >= 0 && ordinary > MaxItems; index--)
        {
            if (items[index].Favorite)
            {
                continue;
            }

            items.RemoveAt(index);
            ordinary--;
        }
    }
}
