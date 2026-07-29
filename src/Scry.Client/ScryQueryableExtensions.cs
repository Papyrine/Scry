namespace Scry.Client;

/// <summary>Async terminal operators that execute a captured Scry query against the server.</summary>
public static class ScryQueryableExtensions
{
    /// <summary>Executes the query and returns all rows.</summary>
    public static async Task<List<T>> ToListAsync<T>(this IQueryable<T> source, Cancel cancel = default)
    {
        var response = await Send(source, terminal: null, cancel);
        EnsureKind(response, ResultKind.List);
        return Materialize<List<T>>(source, response) ?? [];
    }

    // The collection-shaping terminals below all send the same "enumerate to a list" request as
    // ToListAsync and reshape the materialised rows in memory — Scry has no streaming wire, so there
    // is no separate server op for them. The key/element selectors and comparers therefore run
    // client-side over the returned rows, exactly like the synchronous System.Linq equivalents.

    /// <summary>Executes the query and returns all rows as an array.</summary>
    public static async Task<T[]> ToArrayAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        [.. await ToListAsync(source, cancel)];

    /// <summary>Executes the query and returns all rows in a hash set.</summary>
    public static Task<HashSet<T>> ToHashSetAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        ToHashSetAsync(source, comparer: null, cancel);

    /// <summary>Executes the query and returns all rows in a hash set using the given comparer.</summary>
    public static async Task<HashSet<T>> ToHashSetAsync<T>(this IQueryable<T> source, IEqualityComparer<T>? comparer, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToHashSet(comparer);

    /// <summary>Executes the query and returns all rows keyed by <paramref name="keySelector"/>.</summary>
    public static async Task<Dictionary<TKey, T>> ToDictionaryAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector, Cancel cancel = default)
        where TKey : notnull =>
        (await ToListAsync(source, cancel)).ToDictionary(keySelector);

    /// <summary>Executes the query and returns all rows keyed by <paramref name="keySelector"/> using the given comparer.</summary>
    public static async Task<Dictionary<TKey, T>> ToDictionaryAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector, IEqualityComparer<TKey>? comparer, Cancel cancel = default)
        where TKey : notnull =>
        (await ToListAsync(source, cancel)).ToDictionary(keySelector, comparer);

    /// <summary>Executes the query and returns a dictionary of keys and elements projected from each row.</summary>
    public static async Task<Dictionary<TKey, TValue>> ToDictionaryAsync<T, TKey, TValue>(this IQueryable<T> source, Func<T, TKey> keySelector, Func<T, TValue> elementSelector, Cancel cancel = default)
        where TKey : notnull =>
        (await ToListAsync(source, cancel)).ToDictionary(keySelector, elementSelector);

    /// <summary>Executes the query and returns a dictionary of keys and elements projected from each row, using the given comparer.</summary>
    public static async Task<Dictionary<TKey, TValue>> ToDictionaryAsync<T, TKey, TValue>(this IQueryable<T> source, Func<T, TKey> keySelector, Func<T, TValue> elementSelector, IEqualityComparer<TKey>? comparer, Cancel cancel = default)
        where TKey : notnull =>
        (await ToListAsync(source, cancel)).ToDictionary(keySelector, elementSelector, comparer);

    /// <summary>Executes the query and returns all rows grouped into a lookup by <paramref name="keySelector"/>.</summary>
    public static async Task<ILookup<TKey, T>> ToLookupAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToLookup(keySelector);

    /// <summary>Executes the query and returns all rows grouped into a lookup by <paramref name="keySelector"/> using the given comparer.</summary>
    public static async Task<ILookup<TKey, T>> ToLookupAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector, IEqualityComparer<TKey>? comparer, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToLookup(keySelector, comparer);

    /// <summary>Executes the query and returns a lookup of keys and elements projected from each row.</summary>
    public static async Task<ILookup<TKey, TValue>> ToLookupAsync<T, TKey, TValue>(this IQueryable<T> source, Func<T, TKey> keySelector, Func<T, TValue> elementSelector, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToLookup(keySelector, elementSelector);

    /// <summary>Executes the query and returns a lookup of keys and elements projected from each row, using the given comparer.</summary>
    public static async Task<ILookup<TKey, TValue>> ToLookupAsync<T, TKey, TValue>(this IQueryable<T> source, Func<T, TKey> keySelector, Func<T, TValue> elementSelector, IEqualityComparer<TKey>? comparer, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToLookup(keySelector, elementSelector, comparer);

    /// <summary>
    /// Streaming enumeration is not supported yet. Scry currently returns each query result in a single
    /// response, so there is nothing to stream incrementally; a streaming wire is tracked as a future
    /// enhancement (see docs/querying.md, "Future enhancements"). Use <see cref="ToListAsync{T}"/> for now.
    /// </summary>
    public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IQueryable<T> source, Cancel cancel = default) =>
        throw new NotSupportedException(
            "ToAsyncEnumerable is not supported yet — streaming results is a planned enhancement. Use ToListAsync for now.");

    /// <summary>Executes the query and returns the first row, or default if empty.</summary>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new FirstOp(OrDefault: true, Predicate: null), cancel);

    /// <summary>Executes the query and returns the first row.</summary>
    public static Task<T?> FirstAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new FirstOp(OrDefault: false, Predicate: null), cancel);

    /// <summary>Executes the query and returns the single row, or default if empty.</summary>
    public static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new SingleOp(OrDefault: true, Predicate: null), cancel);

    /// <summary>Executes the query and returns the single row.</summary>
    public static Task<T?> SingleAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new SingleOp(OrDefault: false, Predicate: null), cancel);

    /// <summary>Executes the query and returns the row count.</summary>
    public static async Task<int> CountAsync<T>(this IQueryable<T> source, Cancel cancel = default)
    {
        var response = await Send(source, new CountOp(), cancel);
        EnsureKind(response, ResultKind.Scalar);
        return response.Payload.Deserialize<int>(ScryJson.Options);
    }

    /// <summary>Executes the query and returns whether any rows match.</summary>
    public static async Task<bool> AnyAsync<T>(this IQueryable<T> source, Cancel cancel = default)
    {
        var response = await Send(source, new AnyOp(Predicate: null), cancel);
        EnsureKind(response, ResultKind.Scalar);
        return response.Payload.Deserialize<bool>(ScryJson.Options);
    }

    /// <summary>
    /// Executes the query and returns a bounded page using the server's default page size, plus
    /// whether further rows exist. Advance to the next page with <c>Skip</c>.
    /// </summary>
    public static Task<ScryPage<T>> ToPageAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Page(source, new PageOp(Size: null), cancel);

    /// <summary>
    /// Executes the query and returns a page of at most <paramref name="pageSize"/> rows (capped by the
    /// server's <c>MaxPageSize</c>), plus whether further rows exist. Advance with <c>Skip</c>.
    /// </summary>
    public static Task<ScryPage<T>> ToPageAsync<T>(this IQueryable<T> source, int pageSize, Cancel cancel = default) =>
        Page(source, new PageOp(pageSize), cancel);

    /// <summary>
    /// Executes the query and returns a page of at most <paramref name="pageSize"/> rows, resuming from
    /// <paramref name="cursor"/> (keyset paging). Pass the <see cref="ScryPage{T}.Cursor"/> from the
    /// previous page; a null cursor starts from the beginning. The query must be ordered.
    /// </summary>
    public static Task<ScryPage<T>> ToPageAsync<T>(this IQueryable<T> source, int pageSize, string? cursor, Cancel cancel = default) =>
        Page(source, new PageOp(pageSize, cursor), cancel);

    static async Task<ScryPage<T>> Page<T>(IQueryable<T> source, PageOp terminal, Cancel cancel)
    {
        var response = await Send(source, terminal, cancel);
        EnsureKind(response, ResultKind.Page);
        return Materialize<ScryPage<T>>(source, response) ??
               throw new ScryWireException("Page result deserialized to null.");
    }

    static async Task<T?> Single<T>(IQueryable<T> source, QueryOp terminal, Cancel cancel)
    {
        var response = await Send(source, terminal, cancel);
        EnsureKind(response, ResultKind.Single);
        if (response.Payload.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        return Materialize<T>(source, response);
    }

    /// <summary>
    /// Reads a result payload into the client's generated model, classifying a failure the schema
    /// stamp already attributes to drift.
    /// </summary>
    /// <remarks>
    /// The alias machinery bridges renames, but not every model change: a widened numeric whose value
    /// now overflows the old property, a member that became nullable, a scalar whose representation
    /// changed. Those surface here as a parse failure. When the stamp already shows the client behind
    /// the server — recorded from the response header before this payload is touched — the cause is
    /// drift rather than a wire fault, so it joins every other stale-client failure under one
    /// exception. When the stamps agree, the original propagates untouched: a current client failing
    /// to read a current payload is a real bug and must stay loud.
    /// </remarks>
    static T? Materialize<T>(IQueryable source, QueryResponse response)
    {
        try
        {
            return ScryJson.DeserializePayload<T>(response);
        }
        catch (JsonException exception)
            when (source.Provider is QueryProvider {Client.SchemaStale: true})
        {
            throw new ScryStaleClientException(
                $"The query result could not be read into this client's generated model: {exception.Message} " +
                "The server's queryable surface has changed — regenerate the client, or reload the deployed app.",
                exception);
        }
    }

    /// <summary>
    /// Translates a captured Scry query into its wire <see cref="QueryRequest"/> without executing it.
    /// Intended for tooling that needs to inspect or forward the serialized query (e.g. the query
    /// explorer); the terminal operators above are what application code normally uses.
    /// </summary>
    public static QueryRequest ToScryRequest<T>(this IQueryable<T> source, QueryOp? terminal = null)
    {
        if (source.Provider is not QueryProvider provider)
        {
            throw new("This IQueryable is not a Scry source.");
        }

        var pipeline = new List<QueryOp>(QueryTranslator.Translate(source.Expression));
        AddDefaultProjection(pipeline, provider, terminal);
        if (terminal is not null)
        {
            pipeline.Add(terminal);
        }

        return QueryRequest.Create(provider.Root, pipeline, provider.Client.SchemaStamp);
    }

    /// <summary>
    /// Appends a projection over the source's scalar members when the query wrote no <c>Select</c>.
    /// Without it the server picks the response keys from its own model, which a client generated
    /// before a member rename cannot read; naming them here makes the response keys the client's own.
    /// </summary>
    static void AddDefaultProjection(List<QueryOp> pipeline, QueryProvider provider, QueryOp? terminal)
    {
        // A grouped query's Select is the aggregate projection and is mandatory; Count/Any return a
        // scalar and project nothing. Neither wants a member projection bolted on. A terminal carrying
        // its own predicate is rejected server-side once a Select is present, so it falls back to the
        // server's default projection rather than being made invalid.
        if (provider.DefaultProjection is not { Count: > 0 } members ||
            terminal is CountOp or AnyOp ||
            terminal is FirstOp { Predicate: not null } or SingleOp { Predicate: not null } ||
            pipeline.Any(_ => _ is SelectOp or GroupByOp))
        {
            return;
        }

        pipeline.Add(
            new SelectOp(
                new([..members.Select(_ => new ProjectionMember(_, new NodeValue(new MemberNode([_]))))])));
    }

    static Task<QueryResponse> Send<T>(IQueryable<T> source, QueryOp? terminal, Cancel cancel)
    {
        if (source.Provider is not QueryProvider provider)
        {
            throw new("This IQueryable is not a Scry source.");
        }

        return provider.Client.SendAsync(source.ToScryRequest(terminal), cancel);
    }

    static void EnsureKind(QueryResponse response, ResultKind expected)
    {
        if (response.Kind != expected)
        {
            throw new ScryWireException($"Expected a {expected} result but the server returned {response.Kind}.");
        }
    }
}
