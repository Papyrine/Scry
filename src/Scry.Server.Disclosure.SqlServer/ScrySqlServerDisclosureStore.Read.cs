namespace Scry;

public sealed partial class ScrySqlServerDisclosureStore
{
    // How many rows a listing asks the database for at a time. A caller reads as far as it wants;
    // this only bounds what is fetched ahead of it.
    const int page = 200;

    /// <inheritdoc />
    public async IAsyncEnumerable<ScryRowDisclosure> ReceiversOf(
        string source,
        IReadOnlyList<object?> key,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        ScryDisclosureCursor? after = null,
        [EnumeratorCancellation] Cancel cancel = default)
    {
        // A question can arrive before the first answer has: the tables are made by whichever comes first.
        await EnsureAsync(cancel);
        var text = ScryDisclosureEntity.KeyOf(key);

        // A release of the row, and — for every reviewer since shown an event that released it — the
        // showing. Both cut at how far the event really got: a unit accepted and never handed on is
        // not a disclosure, to its caller or to anybody shown the event afterwards.
        var releases =
            $"""
             FROM {sql.Entities} n
             JOIN {sql.Event} e ON e.[EventId] = n.[EventId]
             JOIN {sql.Units} u ON u.[EventId] = n.[EventId] AND u.[Ordinal] = n.[Ordinal]
             {sql.Ended}
             """;

        // Found by the hash and then held to the text it is a hash of. And once for each unit, however
        // many of the unit's rows this one was: a row joined to itself is still sent once.
        var released =
            $"""
             n.[RowHash] = @row AND n.[Source] = @source AND n.[RowKey] = @key
             AND (c.[Units] IS NULL OR n.[Ordinal] < c.[Units])
             AND NOT EXISTS (SELECT 1 FROM {sql.Entities} m WHERE m.[EventId] = n.[EventId] AND m.[Ordinal] = n.[Ordinal] AND m.[Slot] < n.[Slot] AND m.[RowHash] = n.[RowHash] AND m.[Source] = n.[Source] AND m.[RowKey] = n.[RowKey])
             """;
        var query =
            $"""
             SELECT TOP (@page) q.* FROM
             (
                 SELECT {sql.Columns}, n.[Ordinal], u.[Content], n.[Via],
                        CAST(NULL AS uniqueidentifier) AS [ReviewId], CAST(NULL AS datetimeoffset(7)) AS [ReviewedAt], CAST(NULL AS nvarchar(max)) AS [Reviewer],
                        CAST(NULL AS tinyint) AS [Question], CAST(NULL AS binary(32)) AS [Parameters], CAST(NULL AS int) AS [Results], CAST(NULL AS nvarchar(max)) AS [ReviewNode],
                        e.[At] AS [When], e.[EventId] AS [Mark]
                 {releases}
                 WHERE {released}
                 UNION ALL
                 SELECT {sql.Columns}, n.[Ordinal], u.[Content], n.[Via],
                        r.[ReviewId], r.[At], r.[Reviewer], r.[Question], r.[Parameters], r.[Results], r.[Node],
                        r.[At], r.[ReviewId]
                 {releases}
                 JOIN {sql.ReviewEvent} v ON v.[EventId] = e.[EventId]
                 JOIN {sql.Review} r ON r.[ReviewId] = v.[ReviewId]
                 WHERE {released}
             ) q
             WHERE (@from IS NULL OR q.[When] >= @from) AND (@to IS NULL OR q.[When] <= @to)
               AND (@afterAt IS NULL OR q.[When] < @afterAt OR (q.[When] = @afterAt AND (q.[Mark] < @afterId OR (q.[Mark] = @afterId AND q.[Ordinal] > @afterOrdinal))))
             ORDER BY q.[When] DESC, q.[Mark] DESC, q.[Ordinal];
             """;

        while (true)
        {
            var read = new List<ScryRowDisclosure>(page);
            await using (var connection = await OpenAsync(cancel))
            await using (var command = Command(connection, query))
            {
                command.Parameters.AddWithValue("@page", page);
                command.Parameters.Add(new("@row", SqlDbType.Binary, 32) {Value = DisclosureSql.RowHash(source, text)});
                command.Parameters.Add(new("@source", SqlDbType.NVarChar, -1) {Value = source});
                command.Parameters.Add(new("@key", SqlDbType.NVarChar, -1) {Value = text});
                command.Parameters.Add(new("@from", SqlDbType.DateTimeOffset) {Value = Value(from)});
                command.Parameters.Add(new("@to", SqlDbType.DateTimeOffset) {Value = Value(to)});
                After(command, after);
                await using var reader = await command.ExecuteReaderAsync(cancel);
                while (await reader.ReadAsync(cancel))
                {
                    var disclosed = Event(reader);
                    var row = new ScryRowDisclosure(disclosed, reader.GetInt32(18), ScryDisclosureAddress.From((byte[]) reader[19]), reader.GetString(20))
                    {
                        Close = Closed(reader)
                    };
                    if (!reader.IsDBNull(21))
                    {
                        row = row with
                        {
                            Review = new(reader.GetGuid(21), reader.GetDateTimeOffset(22), (ScryDisclosureQuestion) reader.GetByte(24))
                            {
                                Reviewer = Text(reader, 23),
                                Parameters = Address(reader, 25),
                                Results = reader.GetInt32(26),
                                Events = [disclosed.Id],
                                Node = Text(reader, 27)
                            }
                        };
                    }

                    read.Add(row);
                }
            }

            foreach (var row in read)
            {
                yield return row;
            }

            if (read.Count < page)
            {
                yield break;
            }

            after = ScryDisclosureCursor.After(read[^1]);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ScryDisclosureEntry> ReceivedBy(
        string? caller,
        DateTimeOffset from,
        DateTimeOffset to,
        ScryDisclosureCursor? after = null,
        Cancel cancel = default) =>
        Listed(
            caller,
            "AND e.[At] >= @from AND e.[At] <= @to",
            command =>
            {
                command.Parameters.Add(new("@from", SqlDbType.DateTimeOffset) {Value = from});
                command.Parameters.Add(new("@to", SqlDbType.DateTimeOffset) {Value = to});
            },
            after,
            cancel);

    /// <inheritdoc />
    public IAsyncEnumerable<ScryDisclosureEntry> MemberReceivedBy(
        string? caller,
        string source,
        string member,
        ScryDisclosureCursor? after = null,
        Cancel cancel = default) =>
        Listed(
            caller,
            // The event's shape says the member was returned, and something of that source left: a
            // row named by key, or — for a source with no key to name one by — any unit at all.
            $"""
             AND EXISTS (SELECT 1 FROM {sql.Field} f WHERE f.[Shape] = e.[Shape] AND f.[FieldHash] = @field AND f.[FieldUse] = @returned AND f.[Source] = @source AND f.[Member] = @member)
             AND (
                 EXISTS (SELECT 1 FROM {sql.Entities} n WHERE n.[EventId] = e.[EventId] AND n.[Source] = @source AND n.[Ordinal] < ISNULL(c.[Units], 2147483647))
                 OR (
                     NOT EXISTS (SELECT 1 FROM {sql.Entities} n WHERE n.[EventId] = e.[EventId] AND n.[Source] = @source)
                     AND EXISTS (SELECT 1 FROM {sql.Units} u WHERE u.[EventId] = e.[EventId] AND u.[Ordinal] < ISNULL(c.[Units], 2147483647))
                 )
             )
             """,
            command =>
            {
                command.Parameters.Add(new("@field", SqlDbType.Binary, 32) {Value = DisclosureSql.FieldHash(source, member)});
                command.Parameters.Add(new("@returned", SqlDbType.TinyInt) {Value = (byte) ScryDisclosureFieldUse.Returned});
                command.Parameters.Add(new("@source", SqlDbType.NVarChar, -1) {Value = source});
                command.Parameters.Add(new("@member", SqlDbType.NVarChar, -1) {Value = member});
            },
            after,
            cancel);

    // A caller's events, newest first, less the ones withdrawn before anything of them left.
    async IAsyncEnumerable<ScryDisclosureEntry> Listed(
        string? caller,
        string narrowing,
        Action<SqlCommand> bind,
        ScryDisclosureCursor? after,
        [EnumeratorCancellation] Cancel cancel)
    {
        await EnsureAsync(cancel);

        // Found by the hash and then held to the name it is a hash of. Written as one or the other
        // rather than as a choice the database makes per row, so that either is a seek on the index.
        var who = "e.[CallerHash] = @caller AND e.[Caller] = @name";
        if (caller is null)
        {
            who = "e.[CallerHash] IS NULL";
        }

        while (true)
        {
            // The first half of the cursor is the half the index can be entered by; the second says
            // which of the events at that same instant have already been read.
            var rest = "";
            if (after is not null)
            {
                rest = "AND e.[At] <= @afterAt AND (e.[At] < @afterAt OR e.[EventId] < @afterId)";
            }

            var query =
                $"""
                 SELECT TOP (@page) {sql.Columns}
                 FROM {sql.Event} e
                 {sql.Ended}
                 WHERE {who}
                   AND (c.[Outcome] IS NULL OR c.[Outcome] <> @retracted)
                   {narrowing}
                   {rest}
                 ORDER BY e.[At] DESC, e.[EventId] DESC;
                 """;
            var read = new List<ScryDisclosureEntry>(page);
            await using (var connection = await OpenAsync(cancel))
            await using (var command = Command(connection, query))
            {
                command.Parameters.AddWithValue("@page", page);
                command.Parameters.Add(new("@caller", SqlDbType.Binary, 32) {Value = Value(DisclosureSql.CallerHash(caller))});
                command.Parameters.Add(new("@name", SqlDbType.NVarChar, -1) {Value = Value(caller)});
                command.Parameters.Add(new("@retracted", SqlDbType.TinyInt) {Value = (byte) ScryDisclosureOutcome.Retracted});
                bind(command);
                After(command, after);
                await using var reader = await command.ExecuteReaderAsync(cancel);
                while (await reader.ReadAsync(cancel))
                {
                    read.Add(new(Event(reader), Closed(reader)));
                }
            }

            foreach (var entry in read)
            {
                yield return entry;
            }

            if (read.Count < page)
            {
                yield break;
            }

            after = ScryDisclosureCursor.After(read[^1]);
        }
    }

    static void After(SqlCommand command, ScryDisclosureCursor? after)
    {
        command.Parameters.Add(new("@afterAt", SqlDbType.DateTimeOffset) {Value = Value(after?.At)});
        command.Parameters.Add(new("@afterId", SqlDbType.UniqueIdentifier) {Value = Value(after?.Id)});
        command.Parameters.Add(new("@afterOrdinal", SqlDbType.Int) {Value = Value(after?.Ordinal)});
    }

    // An event as the listings select it: the columns of DisclosureSql.Columns, in that order.
    static ScryDisclosureEvent Event(SqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetDateTimeOffset(1), (ScryDisclosureKind) reader.GetByte(3), reader.GetString(6))
        {
            Caller = Text(reader, 2),
            Subscribed = reader.GetBoolean(4),
            Delivery = (ScryDisclosureDelivery) reader.GetByte(5),
            Request = Address(reader, 7),
            Shape = Address(reader, 8),
            Sensitive = reader.GetBoolean(9),
            Stamp = Text(reader, 10),
            Correlation = Text(reader, 11),
            Node = Text(reader, 12),
            ContentType = Text(reader, 13)
        };

    static ScryDisclosureClose? Closed(SqlDataReader reader)
    {
        if (reader.IsDBNull(14))
        {
            return null;
        }

        return new((ScryDisclosureOutcome) reader.GetByte(14), reader.GetInt32(15))
        {
            At = reader.GetDateTimeOffset(16),
            Response = Address(reader, 17)
        };
    }

    /// <inheritdoc />
    public async ValueTask<ScryDisclosedResponse?> Reconstruct(Guid eventId, Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        ScryDisclosureEvent disclosed;
        ScryDisclosureClose? closed;
        var fields = new List<ScryDisclosureField>();
        var units = new List<(int Ordinal, ScryDisclosureAddress Address, Kept? Kept)>();
        var entities = new List<ScryDisclosureEntity>();
        await using (var connection = await OpenAsync(cancel))
        await using (var command = Command(
                         connection,
                         $"""
                          SELECT {sql.Columns} FROM {sql.Event} e {sql.Ended} WHERE e.[EventId] = @event;
                          SELECT f.[Source], f.[Member], f.[FieldUse], f.[Sensitive] FROM {sql.Field} f JOIN {sql.Event} e ON e.[Shape] = f.[Shape] WHERE e.[EventId] = @event ORDER BY f.[Position];
                          SELECT u.[Ordinal], u.[Content], k.[Kind], k.[Length], k.[Bytes], k.[Elsewhere], k.[Erased] FROM {sql.Units} u LEFT JOIN {sql.Content} k ON k.[Address] = u.[Content] WHERE u.[EventId] = @event ORDER BY u.[Ordinal];
                          SELECT n.[Ordinal], n.[Slot], n.[Source], n.[RowKey], n.[Via] FROM {sql.Entities} n WHERE n.[EventId] = @event ORDER BY n.[Ordinal], n.[Slot];
                          """))
        {
            command.Parameters.AddWithValue("@event", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancel);
            if (!await reader.ReadAsync(cancel))
            {
                return null;
            }

            disclosed = Event(reader);
            closed = Closed(reader);
            await reader.NextResultAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                fields.Add(new(reader.GetString(0), reader.GetString(1), (ScryDisclosureFieldUse) reader.GetByte(2), reader.GetBoolean(3)));
            }

            await reader.NextResultAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                Kept? kept = null;
                if (!reader.IsDBNull(2))
                {
                    kept = Held(reader, 2);
                }

                units.Add((reader.GetInt32(0), ScryDisclosureAddress.From((byte[]) reader[1]), kept));
            }

            await reader.NextResultAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                entities.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }
        }

        ReadOnlyMemory<byte>? request = null;
        if (disclosed.Request is { } asked &&
            await Content(asked, cancel) is {Held: true} body)
        {
            request = body.Bytes;
        }

        // A shape always names at least one member, so one with none is one the record never held.
        ScryDisclosureShape? shape = null;
        if (disclosed.Shape is { } described &&
            fields.Count > 0)
        {
            shape = new(described, fields);
        }

        var rebuilt = new List<ScryDisclosedUnit>(units.Count);
        foreach (var (ordinal, address, kept) in units)
        {
            var from = entities.Where(_ => _.Ordinal == ordinal).ToList();
            if (kept is not { } known)
            {
                rebuilt.Add(new(ordinal, new(address, ScryDisclosureContentKind.Row, 0, default), from, Erased: false));
                continue;
            }

            rebuilt.Add(new(ordinal, await Content(address, known, cancel), from, known.Erased));
        }

        return new(disclosed, closed, request, shape, rebuilt);
    }

    /// <inheritdoc />
    public async ValueTask<ScryDisclosureContent?> Content(ScryDisclosureAddress address, Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        Kept kept;
        await using (var connection = await OpenAsync(cancel))
        await using (var command = Command(connection, $"SELECT k.[Kind], k.[Length], k.[Bytes], k.[Elsewhere], k.[Erased] FROM {sql.Content} k WHERE k.[Address] = @address;"))
        {
            command.Parameters.Add(new("@address", SqlDbType.Binary, 32) {Value = address.ToArray()});
            await using var reader = await command.ExecuteReaderAsync(cancel);
            if (!await reader.ReadAsync(cancel))
            {
                return null;
            }

            kept = Held(reader, 0);
        }

        return await Content(address, kept, cancel);
    }

    // What the content table says of one piece: its bytes where they are in the table, and whether
    // they are kept elsewhere instead, or were erased.
    readonly record struct Kept(ScryDisclosureContentKind Kind, int Length, byte[]? Bytes, bool Elsewhere, bool Erased);

    static Kept Held(SqlDataReader reader, int first)
    {
        byte[]? bytes = null;
        if (!reader.IsDBNull(first + 2))
        {
            bytes = (byte[]) reader[first + 2];
        }

        return new((ScryDisclosureContentKind) reader.GetByte(first), reader.GetInt32(first + 1), bytes, reader.GetBoolean(first + 3), reader.GetBoolean(first + 4));
    }

    async ValueTask<ScryDisclosureContent> Content(ScryDisclosureAddress address, Kept kept, Cancel cancel)
    {
        ReadOnlyMemory<byte> bytes = default;
        if (kept.Bytes is { } inTable)
        {
            bytes = inTable;
        }
        else if (kept.Elsewhere &&
                 options.Content is { } elsewhere &&
                 await elsewhere.GetAsync(address, cancel) is { } found)
        {
            bytes = found;
        }

        return new(address, kept.Kind, kept.Length, bytes);
    }

    /// <inheritdoc />
    public async ValueTask<ScryDisclosureShape?> Shape(ScryDisclosureAddress address, Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        var fields = new List<ScryDisclosureField>();
        await using var connection = await OpenAsync(cancel);
        await using var command = Command(connection, $"SELECT f.[Source], f.[Member], f.[FieldUse], f.[Sensitive] FROM {sql.Field} f WHERE f.[Shape] = @shape ORDER BY f.[Position];");
        command.Parameters.Add(new("@shape", SqlDbType.Binary, 32) {Value = address.ToArray()});
        await using var reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            fields.Add(new(reader.GetString(0), reader.GetString(1), (ScryDisclosureFieldUse) reader.GetByte(2), reader.GetBoolean(3)));
        }

        if (fields.Count == 0)
        {
            return null;
        }

        return new(address, fields);
    }

    /// <inheritdoc />
    public async ValueTask<ScryDisclosureCatalog> Catalog(Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        var sources = new SortedDictionary<string, (bool Keyed, SortedDictionary<string, bool> Members)>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(cancel);
        await using var command = Command(
            connection,
            $"""
             SELECT s.[Source], s.[Keyed] FROM {sql.Source} s;
             SELECT f.[Source], f.[Member], MAX(CAST(f.[Sensitive] AS int)) FROM {sql.Field} f GROUP BY f.[Source], f.[Member];
             """);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            sources[reader.GetString(0)] = (reader.GetBoolean(1), new(StringComparer.Ordinal));
        }

        await reader.NextResultAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            var name = reader.GetString(0);
            if (!sources.TryGetValue(name, out var source))
            {
                sources[name] = source = (false, new(StringComparer.Ordinal));
            }

            source.Members[reader.GetString(1)] = reader.GetInt32(2) != 0;
        }

        return new(
        [
            .. sources.Select(source => new ScryDisclosureCatalogSource(
                source.Key,
                source.Value.Keyed,
                [.. source.Value.Members.Select(_ => new ScryDisclosureCatalogMember(_.Key, _.Value))]))
        ]);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScryDisclosureReview> Reviews(ScryDisclosureCursor? after = null, [EnumeratorCancellation] Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        var query =
            $"""
             SELECT TOP (@page) r.[ReviewId], r.[At], r.[Reviewer], r.[Question], r.[Parameters], r.[Results], r.[Node],
                    (SELECT STRING_AGG(CONVERT(nvarchar(max), v.[EventId]), N',') FROM {sql.ReviewEvent} v WHERE v.[ReviewId] = r.[ReviewId])
             FROM {sql.Review} r
             WHERE (@afterAt IS NULL OR r.[At] < @afterAt OR (r.[At] = @afterAt AND r.[ReviewId] < @afterId))
             ORDER BY r.[At] DESC, r.[ReviewId] DESC;
             """;
        while (true)
        {
            var read = new List<ScryDisclosureReview>(page);
            await using (var connection = await OpenAsync(cancel))
            await using (var command = Command(connection, query))
            {
                command.Parameters.AddWithValue("@page", page);
                After(command, after);
                await using var reader = await command.ExecuteReaderAsync(cancel);
                while (await reader.ReadAsync(cancel))
                {
                    IReadOnlyList<Guid> shown = [];
                    if (Text(reader, 7) is { } ids)
                    {
                        shown = [.. ids.Split(',').Select(Guid.Parse)];
                    }

                    read.Add(
                        new(reader.GetGuid(0), reader.GetDateTimeOffset(1), (ScryDisclosureQuestion) reader.GetByte(3))
                        {
                            Reviewer = Text(reader, 2),
                            Parameters = Address(reader, 4),
                            Results = reader.GetInt32(5),
                            Events = shown,
                            Node = Text(reader, 6)
                        });
                }
            }

            foreach (var review in read)
            {
                yield return review;
            }

            if (read.Count < page)
            {
                yield break;
            }

            after = ScryDisclosureCursor.After(read[^1]);
        }
    }
}
