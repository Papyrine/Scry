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
        await Caught(cancel);
        var text = ScryDisclosureEntity.KeyOf(key);
        var row = DisclosureSql.RowHash(source, text);

        // Where the question names a span or a page goes on from the last, said as conditions on the
        // order the rows are listed in, and only then: a condition that may say nothing is one the
        // database checks of every row rather than a range it seeks to.
        string Span(string alias)
        {
            var span = "";
            if (from is not null)
            {
                span += $" AND {alias}.[At] >= @from";
            }

            if (to is not null)
            {
                span += $" AND {alias}.[At] <= @to";
            }

            return span;
        }

        string Seek(string at, string id, string ordinal)
        {
            if (after is null)
            {
                return "";
            }

            return $" AND {at} <= @afterAt AND ({at} < @afterAt OR ({at} = @afterAt AND ({id} < @afterId OR ({id} = @afterId AND {ordinal} > @afterOrdinal))))";
        }

        void Parameters(SqlCommand command)
        {
            command.Parameters.AddWithValue("@page", page);
            command.Parameters.Add(new("@row", SqlDbType.Binary, 32) {Value = row});
            command.Parameters.Add(new("@source", SqlDbType.NVarChar, -1) {Value = source});
            command.Parameters.Add(new("@key", SqlDbType.NVarChar, -1) {Value = text});
            command.Parameters.Add(new("@from", SqlDbType.DateTimeOffset) {Value = Value(from)});
            command.Parameters.Add(new("@to", SqlDbType.DateTimeOffset) {Value = Value(to)});
            After(command, after);
        }

        while (true)
        {
            // A release of the row, and — for every reviewer since shown an event that released it —
            // the showing. Both cut at how far the event really got: a unit accepted and never handed
            // on is not a disclosure, to its caller or to anybody shown the event afterwards.
            //
            // Releases in two steps. First the page of the row's receipts, newest first, from the
            // receipts alone: a seek that stops at a page, whatever the number of segments. Then,
            // for each segment the page reaches, what was sent in those receipts, from that
            // segment's own tables named in the statement: asked of the views, every receipt's
            // event, close and unit would be looked for in every segment there is.
            var until = Earlier(to, after?.At);
            var releases = await Within("t", from, until, cancel);
            var showings = await Within("r", from, until, cancel);
            var receipts = new List<(int Segment, DateTimeOffset At, Guid EventId, int Ordinal)>(page);
            var shown = new List<ScryRowDisclosure>();
            await using (var connection = await OpenAsync(cancel))
            {
                await using (var command = Command(
                                 connection,
                                 $"""
                                  SELECT TOP (@page) t.[Segment], t.[At], t.[EventId], t.[Ordinal] FROM {sql.Receipt} t
                                  WHERE t.[RowHash] = @row{releases}{Span("t")}{Seek("t.[At]", "t.[EventId]", "t.[Ordinal]")}
                                  ORDER BY t.[At] DESC, t.[EventId] DESC, t.[Ordinal];
                                  """))
                {
                    Parameters(command);
                    await using var reader = await command.ExecuteReaderAsync(cancel);
                    while (await reader.ReadAsync(cancel))
                    {
                        receipts.Add((reader.GetInt32(0), reader.GetDateTimeOffset(1), reader.GetGuid(2), reader.GetInt32(3)));
                    }
                }

                // A showing from the reviews, newest first, kept where it showed an answer the row is
                // a receipt of. Reviews are few beside answers, so this is asked of the views whole.
                await using (var command = Command(
                                 connection,
                                 $"""
                                  SELECT TOP (@page) {sql.Columns}, t.[Ordinal], n.[Content], n.[Via], n.[Version],
                                         r.[ReviewId], r.[At], r.[Reviewer], r.[Question], r.[Parameters], r.[Results], r.[Node]
                                  FROM {sql.Review} r
                                  JOIN {sql.ReviewEvent} v ON v.[ReviewId] = r.[ReviewId] AND v.[Segment] = r.[Segment]
                                  JOIN {sql.Event} e ON e.[EventId] = v.[EventId]
                                  {sql.Ended}
                                  JOIN {sql.Receipt} t ON t.[RowHash] = @row AND t.[At] = e.[At] AND t.[EventId] = e.[EventId] AND t.[Segment] = e.[Segment]
                                  {Unit("t.[EventId]", "t.[Ordinal]", "t.[RowHash]", sql.Entity, sql.Unit, sql.Answer, sql.ManifestRun, sql.RunEntity, sql.RunUnit, "t.[Segment]")}
                                  WHERE (c.[Units] IS NULL OR t.[Ordinal] < c.[Units]){showings}{Span("r")}{Seek("r.[At]", "r.[ReviewId]", "t.[Ordinal]")}
                                  ORDER BY r.[At] DESC, r.[ReviewId] DESC, t.[Ordinal];
                                  """))
                {
                    Parameters(command);
                    await using var reader = await command.ExecuteReaderAsync(cancel);
                    while (await reader.ReadAsync(cancel))
                    {
                        var disclosed = Event(reader);
                        shown.Add(
                            new(disclosed, reader.GetInt32(19), ScryDisclosureAddress.From((byte[]) reader[20]), reader.GetString(21))
                            {
                                Close = Closed(reader),
                                Version = Text(reader, 22),
                                Review = new(reader.GetGuid(23), reader.GetDateTimeOffset(24), (ScryDisclosureQuestion) reader.GetByte(26))
                                {
                                    Reviewer = Text(reader, 25),
                                    Parameters = Address(reader, 27),
                                    Results = reader.GetInt32(28),
                                    Events = [disclosed.Id],
                                    Node = Text(reader, 29)
                                }
                            });
                    }
                }
            }

            var released = await Released(receipts, row, source, text, cancel);

            // The two merged in the order the record lists them, which is SQL Server's: a
            // uniqueidentifier sorts by its last bytes first.
            var merged = released
                .Concat(shown)
                .OrderByDescending(Listed)
                .ThenByDescending(_ => new SqlGuid(_.Review?.Id ?? _.Event.Id))
                .ThenBy(_ => _.Ordinal)
                .ToList();
            var read = merged.Take(page).ToList();
            foreach (var each in read)
            {
                yield return each;
            }

            // More where either list was cut at a page, or the merge was.
            if (receipts.Count < page &&
                shown.Count < page &&
                merged.Count <= page)
            {
                yield break;
            }

            if (read.Count > 0)
            {
                after = ScryDisclosureCursor.After(read[^1]);
                continue;
            }

            // A page of receipts none of which was handed on: on from the last of them.
            var (_, at, id, ordinal) = receipts[^1];
            after = new(at, id, ordinal);
        }
    }

    static DateTimeOffset Listed(ScryRowDisclosure row) =>
        row.Review?.At ?? row.Event.At;

    // What was sent in each of a page of a row's receipts: its event, how the event ended, and the
    // unit. One statement for each segment the page reaches, of that segment's own tables, with the
    // receipts it holds written into it. A receipt whose unit was never handed on is left out.
    async Task<List<ScryRowDisclosure>> Released(
        List<(int Segment, DateTimeOffset At, Guid EventId, int Ordinal)> receipts,
        byte[] row,
        string source,
        string text,
        Cancel cancel)
    {
        var found = new List<ScryRowDisclosure>(receipts.Count);
        if (receipts.Count == 0)
        {
            return found;
        }

        await using var connection = await OpenAsync(cancel);
        foreach (var segment in receipts.GroupBy(_ => _.Segment))
        {
            var tables = sql.Segment(segment.Key);
            var wanted = string.Join(", ", segment.Select(_ => $"(CAST('{_.EventId:D}' AS uniqueidentifier), {_.Ordinal.ToString(CultureInfo.InvariantCulture)})"));
            await using var command = Command(
                connection,
                $"""
                 SELECT {sql.Columns}, w.[Ordinal], n.[Content], n.[Via], n.[Version]
                 FROM (VALUES {wanted}) w ([EventId], [Ordinal])
                 JOIN {tables.Event} e ON e.[EventId] = w.[EventId]
                 OUTER APPLY (SELECT TOP (1) x.[Outcome], x.[Units], x.[At] AS [ClosedAt], x.[Response] FROM {tables.Close} x WHERE x.[EventId] = e.[EventId] ORDER BY x.[Sequence] DESC) c
                 {Unit("w.[EventId]", "w.[Ordinal]", "@row", tables.Entity, tables.Unit, tables.Answer, tables.ManifestRun, tables.RunEntity, tables.RunUnit, null)}
                 WHERE c.[Units] IS NULL OR w.[Ordinal] < c.[Units];
                 """);
            command.Parameters.Add(new("@row", SqlDbType.Binary, 32) {Value = row});
            command.Parameters.Add(new("@source", SqlDbType.NVarChar, -1) {Value = source});
            command.Parameters.Add(new("@key", SqlDbType.NVarChar, -1) {Value = text});
            await using var reader = await command.ExecuteReaderAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                found.Add(
                    new(Event(reader), reader.GetInt32(19), ScryDisclosureAddress.From((byte[]) reader[20]), reader.GetString(21))
                    {
                        Close = Closed(reader),
                        Version = Text(reader, 22)
                    });
            }
        }

        return found;
    }

    // The unit a receipt names, held to the row it is a receipt of: the unit's first slot that is this
    // row, which a row joined to itself is in twice and was sent in once. Gone to by seeks alone. A
    // unit kept as its batch's own is found by its place. One kept in a run is found by going to the
    // part of the answer it is in, the run of that part's list it is in, and its offset there, each
    // the last that starts at or before it, since a unit's place in an answer is the three starts
    // added up and no index can seek that. Of one segment's tables where segment is null, and of the
    // views over every segment, held to that segment, where it names one.
    static string Unit(
        string eventId,
        string ordinal,
        string rowHash,
        string entity,
        string unit,
        string answer,
        string manifestRun,
        string runEntity,
        string runUnit,
        string? segment)
    {
        string In(string alias)
        {
            if (segment is null)
            {
                return "";
            }

            return $" AND {alias}.[Segment] = {segment}";
        }

        return $"""
                CROSS APPLY (SELECT TOP (1) m.[Content], m.[Via], m.[Version] FROM
                (
                    SELECT k.[Slot], u.[Content], k.[Via], k.[Version] FROM {entity} k
                    JOIN {unit} u ON u.[EventId] = k.[EventId] AND u.[Ordinal] = k.[Ordinal]{In("u")}
                    WHERE k.[EventId] = {eventId}{In("k")} AND k.[Ordinal] = {ordinal}
                      AND k.[RowHash] = {rowHash} AND k.[Source] = @source AND k.[RowKey] = @key
                    UNION ALL
                    SELECT r.[Slot], u.[Content], r.[Via], r.[Version]
                    FROM (SELECT TOP (1) a.[Manifest], a.[Start] FROM {answer} a
                          WHERE a.[EventId] = {eventId}{In("a")} AND a.[Start] <= {ordinal}
                          ORDER BY a.[Start] DESC) a
                    CROSS APPLY (SELECT TOP (1) l.[Run], l.[Start] FROM {manifestRun} l
                                 WHERE l.[Manifest] = a.[Manifest]{In("l")} AND l.[Start] <= {ordinal} - a.[Start]
                                 ORDER BY l.[Start] DESC) l
                    JOIN {runEntity} r ON r.[Run] = l.[Run]{In("r")} AND r.[Offset] = {ordinal} - a.[Start] - l.[Start]
                    JOIN {runUnit} u ON u.[Run] = r.[Run] AND u.[Offset] = r.[Offset]{In("u")}
                    WHERE r.[RowHash] = {rowHash} AND r.[Source] = @source AND r.[RowKey] = @key
                ) m ORDER BY m.[Slot]) n
                """;
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
            from,
            to,
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
            null,
            null,
            // The event's shape says the member was returned, and something of that source left: a
            // row named by key, or — for a source with no key to name one by — any unit at all. Each
            // looked for in the segment the event is in, where its shape and its units are.
            $"""
             AND EXISTS (SELECT 1 FROM {sql.Field} f WHERE f.[Shape] = e.[Shape] AND f.[Segment] = e.[Segment] AND f.[FieldHash] = @field AND f.[FieldUse] = @returned AND f.[Source] = @source AND f.[Member] = @member)
             AND (
                 EXISTS (SELECT 1 FROM {sql.Entities} n WHERE n.[EventId] = e.[EventId] AND n.[Segment] = e.[Segment] AND n.[Source] = @source AND n.[Ordinal] < ISNULL(c.[Units], 2147483647))
                 OR (
                     NOT EXISTS (SELECT 1 FROM {sql.Entities} n WHERE n.[EventId] = e.[EventId] AND n.[Segment] = e.[Segment] AND n.[Source] = @source)
                     AND EXISTS (SELECT 1 FROM {sql.Units} u WHERE u.[EventId] = e.[EventId] AND u.[Segment] = e.[Segment] AND u.[Ordinal] < ISNULL(c.[Units], 2147483647))
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
        DateTimeOffset? from,
        DateTimeOffset? to,
        string narrowing,
        Action<SqlCommand> bind,
        ScryDisclosureCursor? after,
        [EnumeratorCancellation] Cancel cancel)
    {
        await EnsureAsync(cancel);
        await Caught(cancel);

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
                   {rest}{await Within("e", from, Earlier(to, after?.At), cancel)}
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
            ContentType = Text(reader, 13),
            Agent = Text(reader, 18)
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
        await Caught(cancel);

        // Which segment the answer is in is asked first, of every segment, and everything after is
        // asked of that one: an answer is in one segment, however many are kept.
        ScryDisclosureEvent disclosed;
        ScryDisclosureClose? closed;
        var fields = new List<ScryDisclosureField>();
        var units = new List<(int Ordinal, ScryDisclosureAddress Address, Kept? Kept)>();
        var entities = new List<ScryDisclosureEntity>();
        await using (var connection = await OpenAsync(cancel))
        {
            DisclosureReading read;
            await using (var find = Command(connection, $"SELECT TOP (1) e.[Segment] FROM {sql.Event} e WHERE e.[EventId] = @event;"))
            {
                find.Parameters.AddWithValue("@event", eventId);
                if (await find.ExecuteScalarAsync(cancel) is not int segment)
                {
                    return null;
                }

                read = sql.Reading(segment);
            }

            await using var command = Command(
                connection,
                $"""
                 SELECT {sql.Columns} FROM {read.Event} e {read.Ended} WHERE e.[EventId] = @event;
                 SELECT f.[Source], f.[Member], f.[FieldUse], f.[Sensitive] FROM {read.Field} f JOIN {read.Event} e ON e.[Shape] = f.[Shape] AND e.[Segment] = f.[Segment] WHERE e.[EventId] = @event ORDER BY f.[Position];
                 SELECT u.[Ordinal], u.[Content], k.[Kind], k.[Length], k.[Bytes], k.[Elsewhere], k.[Erased] FROM {read.Units} u LEFT JOIN {read.Content} k ON k.[Address] = u.[Content] AND k.[Segment] = u.[Segment] WHERE u.[EventId] = @event ORDER BY u.[Ordinal];
                 SELECT n.[Ordinal], n.[Slot], n.[Source], n.[RowKey], n.[Via], n.[Version] FROM {read.Entities} n WHERE n.[EventId] = @event ORDER BY n.[Ordinal], n.[Slot];
                 """);
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
                entities.Add(
                    new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))
                    {
                        Version = Text(reader, 5)
                    });
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
    /// <remarks>
    /// A piece is kept once in each segment it was sent in. Asked for by its address alone, it is
    /// the one that still has its bytes, where any has.
    /// </remarks>
    public async ValueTask<ScryDisclosureContent?> Content(ScryDisclosureAddress address, Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        Kept kept;
        await using (var connection = await OpenAsync(cancel))
        await using (var command = Command(connection, $"SELECT TOP (1) k.[Kind], k.[Length], k.[Bytes], k.[Elsewhere], k.[Erased] FROM {sql.Content} k WHERE k.[Address] = @address ORDER BY CASE WHEN k.[Bytes] IS NOT NULL OR k.[Elsewhere] = 1 THEN 0 ELSE 1 END, k.[Segment] DESC;"))
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
        await using var command = Command(connection, $"SELECT f.[Source], f.[Member], f.[FieldUse], f.[Sensitive] FROM {sql.Field} f WHERE f.[Shape] = @shape AND f.[Segment] = (SELECT MAX(x.[Segment]) FROM {sql.Field} x WHERE x.[Shape] = @shape) ORDER BY f.[Position];");
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
             SELECT s.[Source], MAX(CAST(s.[Keyed] AS int)) FROM {sql.Source} s GROUP BY s.[Source];
             SELECT f.[Source], f.[Member], MAX(CAST(f.[Sensitive] AS int)) FROM {sql.Field} f GROUP BY f.[Source], f.[Member];
             """);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            sources[reader.GetString(0)] = (reader.GetInt32(1) != 0, new(StringComparer.Ordinal));
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
                    (SELECT STRING_AGG(CONVERT(nvarchar(max), v.[EventId]), N',') FROM {sql.ReviewEvent} v WHERE v.[ReviewId] = r.[ReviewId] AND v.[Segment] = r.[Segment])
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
