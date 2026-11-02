/// <summary>
/// A row asked about by a key the explorer cannot type, looked up under every spelling the typed key
/// could be — see <see cref="DisclosureKeys"/>. The record holds a GUID key in lowercase with hyphens
/// and a date key in ISO form, whatever was typed; with no model to say which a key is, each spelling
/// is asked about, and the answers are read as one listing, newest first, in the order the store lists
/// them.
/// </summary>
sealed class SpelledRow(IScryDisclosureReader reader, string source, IReadOnlyList<IReadOnlyList<object?>> spellings)
{
    // Which spellings the record has been found to hold the row under, of what has been read so far.
    HashSet<int> found = [];

    /// <summary>
    /// The key as the record holds the row: the one spelling it was found under, or the key as typed
    /// where it was found under none — or, as text can be, under several.
    /// </summary>
    public string ReadAs
    {
        get
        {
            if (found.Count == 1)
            {
                return ScryDisclosureEntity.KeyOf(spellings[found.Single()]);
            }

            return ScryDisclosureEntity.KeyOf(spellings[0]);
        }
    }

    /// <summary>
    /// Who received the row, under every spelling at once: the store's listing for each, newest
    /// first, merged into one that is. The store applies a cursor to each listing on its own, so a
    /// later page takes up where the merged one left off.
    /// </summary>
    public async IAsyncEnumerable<ScryRowDisclosure> ReceiversOf(
        DateTimeOffset? from,
        DateTimeOffset? to,
        ScryDisclosureCursor? after,
        [EnumeratorCancellation] Cancel cancel = default)
    {
        var listings = new List<IAsyncEnumerator<ScryRowDisclosure>>(spellings.Count);
        try
        {
            foreach (var spelling in spellings)
            {
                listings.Add(reader.ReceiversOf(source, spelling, from, to, after, cancel).GetAsyncEnumerator(cancel));
            }

            // The head of each listing, or null once it is spent.
            var heads = new ScryRowDisclosure?[listings.Count];
            for (var index = 0; index < listings.Count; index++)
            {
                heads[index] = await Next(listings[index]);
            }

            while (true)
            {
                var newest = -1;
                for (var index = 0; index < heads.Length; index++)
                {
                    if (heads[index] is { } head &&
                        (newest < 0 || Newer(head, heads[newest]!)))
                    {
                        newest = index;
                    }
                }

                if (newest < 0)
                {
                    yield break;
                }

                found.Add(newest);
                yield return heads[newest]!;
                heads[newest] = await Next(listings[newest]);
            }
        }
        finally
        {
            foreach (var listing in listings)
            {
                await listing.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The spellings the record holds the row under — or the key as typed where it holds the row under
    /// none, so that what is done to the row is done to, and recorded against, what was asked.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<object?>>> Held(Cancel cancel)
    {
        if (spellings.Count == 1)
        {
            return spellings;
        }

        var held = new List<IReadOnlyList<object?>>();
        foreach (var spelling in spellings)
        {
            await using var listing = reader.ReceiversOf(source, spelling, cancel: cancel).GetAsyncEnumerator(cancel);
            if (await listing.MoveNextAsync())
            {
                held.Add(spelling);
            }
        }

        if (held.Count == 0)
        {
            return [spellings[0]];
        }

        return held;
    }

    static async ValueTask<ScryRowDisclosure?> Next(IAsyncEnumerator<ScryRowDisclosure> listing)
    {
        if (await listing.MoveNextAsync())
        {
            return listing.Current;
        }

        return null;
    }

    // The order a store lists in: by when the row reached somebody, newest first; at one instant by
    // whose reaching it was; and within that, unit by unit in the order they were sent.
    static bool Newer(ScryRowDisclosure left, ScryRowDisclosure right)
    {
        var when = When(left).CompareTo(When(right));
        if (when != 0)
        {
            return when > 0;
        }

        var mark = Mark(left).CompareTo(Mark(right));
        if (mark != 0)
        {
            return mark > 0;
        }

        return left.Ordinal < right.Ordinal;
    }

    // When a row reached somebody: when the answer was made, or when a reviewer was shown it.
    static DateTimeOffset When(ScryRowDisclosure disclosure) =>
        disclosure.Review?.At ?? disclosure.Event.At;

    // Whose reaching it the entry is: the review's where a reviewer was shown the event, else the
    // event's own.
    static Guid Mark(ScryRowDisclosure disclosure) =>
        disclosure.Review?.Id ?? disclosure.Event.Id;
}
