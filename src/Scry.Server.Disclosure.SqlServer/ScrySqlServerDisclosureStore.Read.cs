namespace Scry;

public sealed partial class ScrySqlServerDisclosureStore
{
    // How many rows a listing asks the database for at a time. A caller reads as far as it wants;
    // this only bounds what is fetched ahead of it. Settable so that a test can make a page end
    // inside what it recorded.
    internal int Page { get; init; } = 200;

    // Where a listing of a row's receivers has got to, as the record orders it: the time, the id of
    // the event or the review, the unit — and, for a reviewer's showing, which event it was of, since
    // one review may have shown the row at the same unit of several events, and the three before it
    // then tell those showings apart no further. Null where the place is a caller's cursor, which
    // names no event: everything of that time, id and unit is then taken as read.
    readonly record struct Place(DateTimeOffset At, Guid Id, int Ordinal, Guid? Event);

    // The order the record lists a row's receivers in, which is SQL Server's: newest first, then by
    // id as a uniqueidentifier sorts — by its last bytes first — then by unit, then by the event a
    // showing was of, the same way.
    static Comparer<Place> listing = Comparer<Place>.Create(Compare);

    static int Compare(Place one, Place other)
    {
        var by = other.At.CompareTo(one.At);
        if (by != 0)
        {
            return by;
        }

        by = new SqlGuid(other.Id).CompareTo(new SqlGuid(one.Id));
        if (by != 0)
        {
            return by;
        }

        by = one.Ordinal.CompareTo(other.Ordinal);
        if (by != 0)
        {
            return by;
        }

        return new SqlGuid(other.Event ?? other.Id).CompareTo(new SqlGuid(one.Event ?? one.Id));
    }

    static Place Of(ScryRowDisclosure row) =>
        new(row.Review?.At ?? row.Event.At, row.Review?.Id ?? row.Event.Id, row.Ordinal, row.Event.Id);

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

        // A caller's cursor names no event, so everything of the time, id and unit it names is taken
        // as read; the places this listing makes for itself name one.
        Place? place = null;
        if (after is { } given)
        {
            place = new(given.At, given.Id, given.Ordinal, null);
        }

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

        string Seek(string at, string id, string ordinal, string? of = null)
        {
            if (place is null)
            {
                return "";
            }

            var within = $"{ordinal} > @afterOrdinal";
            if (of is not null)
            {
                // Of one review at one unit, the showings of events after the one the place names —
                // and none where the place is a caller's, which names no event and compares as no
                // row does.
                within = $"({within} OR ({ordinal} = @afterOrdinal AND {of} < @afterEvent))";
            }

            return $" AND {at} <= @afterAt AND ({at} < @afterAt OR ({at} = @afterAt AND ({id} < @afterId OR ({id} = @afterId AND {within}))))";
        }

        void Parameters(SqlCommand command)
        {
            command.Parameters.AddWithValue("@page", Page);
            Row(command, row, source, text);
            command.Parameters.Add(new("@from", SqlDbType.DateTimeOffset) {Value = Value(from)});
            command.Parameters.Add(new("@to", SqlDbType.DateTimeOffset) {Value = Value(to)});
            command.Parameters.Add(new("@afterAt", SqlDbType.DateTimeOffset) {Value = Value(place?.At)});
            command.Parameters.Add(new("@afterId", SqlDbType.UniqueIdentifier) {Value = Value(place?.Id)});
            command.Parameters.Add(new("@afterOrdinal", SqlDbType.Int) {Value = Value(place?.Ordinal)});
            command.Parameters.Add(new("@afterEvent", SqlDbType.UniqueIdentifier) {Value = Value(place?.Event)});
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
            var until = Earlier(to, place?.At);
            var releases = await Within("t", from, until, cancel);
            var showings = await Within("r", from, until, cancel);
            var receipts = new List<(int Segment, DateTimeOffset At, Guid EventId, int Ordinal)>(Page);
            var shown = new List<ScryRowDisclosure>();

            // Where a list was cut at a page, nothing listed after its last row can go out yet: a
            // row the page did not reach may belong between. So the cut is the earlier of the two
            // lists' last rows, where either list filled its page, and nothing past it goes out.
            Place? cut = null;
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

                if (receipts.Count == Page)
                {
                    var (_, at, id, ordinal) = receipts[^1];
                    cut = new(at, id, ordinal, id);
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
                                  WHERE (c.[Units] IS NULL OR t.[Ordinal] < c.[Units]){showings}{Span("r")}{Seek("r.[At]", "r.[ReviewId]", "t.[Ordinal]", "e.[EventId]")}
                                  ORDER BY r.[At] DESC, r.[ReviewId] DESC, t.[Ordinal], e.[EventId] DESC;
                                  """))
                {
                    Parameters(command);
                    await using var reader = await command.ExecuteReaderAsync(cancel);
                    while (await reader.ReadAsync(cancel))
                    {
                        var carried = Carried(reader);
                        shown.Add(
                            carried with
                            {
                                Review = new(reader.GetGuid(23), reader.GetDateTimeOffset(24), (ScryDisclosureQuestion) reader.GetByte(26))
                                {
                                    Reviewer = Text(reader, 25),
                                    Parameters = Address(reader, 27),
                                    Results = reader.GetInt32(28),
                                    Events = [carried.Event.Id],
                                    Node = Text(reader, 29)
                                }
                            });
                    }
                }

                if (shown.Count == Page)
                {
                    var last = Of(shown[^1]);
                    if (cut is not { } other ||
                        listing.Compare(last, other) < 0)
                    {
                        cut = last;
                    }
                }

                shown = await Ended(connection, shown, cancel);
            }

            var released = await Released(receipts, row, source, text, cancel);

            // The two merged in the order the record lists them, and cut where the lists were:
            // whatever lies past the cut is read again, after it, next time round.
            var merged = released
                .Concat(shown)
                .OrderBy(Of, listing)
                .ToList();
            IEnumerable<ScryRowDisclosure> before = merged;
            if (cut is { } bound)
            {
                before = merged.TakeWhile(_ => listing.Compare(Of(_), bound) <= 0);
            }

            var read = before.Take(Page).ToList();
            foreach (var each in read)
            {
                yield return each;
            }

            // Done once neither list was cut and everything merged went out.
            if (cut is null &&
                merged.Count <= Page)
            {
                yield break;
            }

            // On from the last that went out — or, where nothing could, every row read lying past the
            // cut, as a page of receipts none of which was handed on does, from the cut itself.
            if (read.Count > 0)
            {
                place = Of(read[^1]);
                continue;
            }

            place = cut;
        }
    }

    // A row's release as the listings select it: the event's columns, then the unit, what it
    // carried, how it reached the row, and the version the row was at.
    static ScryRowDisclosure Carried(SqlDataReader reader) =>
        new(Event(reader), reader.GetInt32(19), ScryDisclosureAddress.From((byte[]) reader[20]), reader.GetString(21))
        {
            Close = Closed(reader),
            Version = Text(reader, 22)
        };

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

        // A receipt whose event is not in its segment is of a later part of an answer whose segment
        // was being retired when the part arrived, which went to the segment then being written to.
        // Those are gathered as each segment is read, and asked of the views afterwards.
        var strayed = new List<(int Segment, Guid EventId, int Ordinal)>();
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
                 SELECT DISTINCT w.[EventId] FROM (VALUES {wanted}) w ([EventId], [Ordinal])
                 WHERE NOT EXISTS (SELECT 1 FROM {tables.Event} e WHERE e.[EventId] = w.[EventId]);
                 """);
            Row(command, row, source, text);
            await using var reader = await command.ExecuteReaderAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                found.Add(Carried(reader));
            }

            await reader.NextResultAsync(cancel);
            var missing = new HashSet<Guid>();
            while (await reader.ReadAsync(cancel))
            {
                missing.Add(reader.GetGuid(0));
            }

            strayed.AddRange(segment.Where(_ => missing.Contains(_.EventId)).Select(_ => (_.Segment, _.EventId, _.Ordinal)));
        }

        if (strayed.Count > 0)
        {
            await Strayed(connection, strayed, row, source, text, found, cancel);
        }

        return await Ended(connection, found, cancel);
    }

    // What was sent in the receipts whose event is not in their segment: read through the views,
    // the event from the segment it is still in, its close from whichever it reached, and the unit
    // from the receipt's own. Nothing is found for one whose event's segment has since been dropped:
    // what a retired segment held can no longer be asked.
    async Task Strayed(
        SqlConnection connection,
        List<(int Segment, Guid EventId, int Ordinal)> strayed,
        byte[] row,
        string source,
        string text,
        List<ScryRowDisclosure> found,
        Cancel cancel)
    {
        var wanted = string.Join(", ", strayed.Select(_ => $"(CAST('{_.EventId:D}' AS uniqueidentifier), {_.Ordinal.ToString(CultureInfo.InvariantCulture)}, {_.Segment.ToString(CultureInfo.InvariantCulture)})"));
        await using var command = Command(
            connection,
            $"""
             SELECT {sql.Columns}, w.[Ordinal], n.[Content], n.[Via], n.[Version]
             FROM (VALUES {wanted}) w ([EventId], [Ordinal], [Segment])
             JOIN {sql.Event} e ON e.[EventId] = w.[EventId]
             {sql.EndedAnywhere}
             {Unit("w.[EventId]", "w.[Ordinal]", "@row", sql.Entity, sql.Unit, sql.Answer, sql.ManifestRun, sql.RunEntity, sql.RunUnit, "w.[Segment]")}
             WHERE c.[Units] IS NULL OR w.[Ordinal] < c.[Units];
             """);
        Row(command, row, source, text);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            found.Add(Carried(reader));
        }
    }

    // Rows released by an event read as never closed, asked again for a close put in another segment
    // than the event: with one, that is the row's close, and a unit past what it counts never left.
    async Task<List<ScryRowDisclosure>> Ended(SqlConnection connection, List<ScryRowDisclosure> rows, Cancel cancel)
    {
        var closes = await ClosedElsewhere(connection, rows.Where(_ => _.Close is null).Select(_ => _.Event.Id), cancel);
        if (closes.Count == 0)
        {
            return rows;
        }

        var ended = new List<ScryRowDisclosure>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Close is not null ||
                !closes.TryGetValue(row.Event.Id, out var close))
            {
                ended.Add(row);
                continue;
            }

            if (row.Ordinal < close.Units)
            {
                ended.Add(
                    row with
                    {
                        Close = close
                    });
            }
        }

        return ended;
    }

    // The closes of events a page read as never closed, from every segment. An answer's close is put
    // in its event's segment, but for a later part of one whose segment was being retired when the
    // part arrived, which went to the segment then being written to and so is not where the
    // segment-held join looks. Events that never closed are few and such closes fewer, so the asking
    // is rare — and of every segment, which is what that join exists to spare the rest.
    async Task<Dictionary<Guid, ScryDisclosureClose>> ClosedElsewhere(SqlConnection connection, IEnumerable<Guid> events, Cancel cancel)
    {
        var closes = new Dictionary<Guid, ScryDisclosureClose>();
        var open = events.Distinct().ToList();
        if (open.Count == 0)
        {
            return closes;
        }

        var wanted = string.Join(", ", open.Select(_ => $"(CAST('{_:D}' AS uniqueidentifier))"));
        await using var command = Command(
            connection,
            $"""
             SELECT w.[EventId], c.[Outcome], c.[Units], c.[At], c.[Response]
             FROM (VALUES {wanted}) w ([EventId])
             CROSS APPLY (SELECT TOP (1) x.[Outcome], x.[Units], x.[At], x.[Response] FROM {sql.Close} x WHERE x.[EventId] = w.[EventId] ORDER BY x.[Sequence] DESC) c;
             """);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            closes[reader.GetGuid(0)] = new((ScryDisclosureOutcome) reader.GetByte(1), reader.GetInt32(2))
            {
                At = reader.GetDateTimeOffset(3),
                Response = Address(reader, 4)
            };
        }

        return closes;
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
            var read = new List<ScryDisclosureEntry>(Page);
            List<ScryDisclosureEntry> ended;
            await using (var connection = await OpenAsync(cancel))
            {
                await using (var command = Command(connection, query))
                {
                    command.Parameters.AddWithValue("@page", Page);
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

                ended = await Ended(connection, read, cancel);
            }

            foreach (var entry in ended)
            {
                yield return entry;
            }

            // The page is the page as the database cut it, whatever was then left out of it.
            if (read.Count < Page)
            {
                yield break;
            }

            after = ScryDisclosureCursor.After(read[^1]);
        }
    }

    // A page of events, those read as never closed asked again for a close put in another segment
    // than the event: with one, that is how the event ended, and one withdrawn before anything of it
    // left is left out as the statement would have left it out.
    async Task<List<ScryDisclosureEntry>> Ended(SqlConnection connection, List<ScryDisclosureEntry> read, Cancel cancel)
    {
        var closes = await ClosedElsewhere(connection, read.Where(_ => _.Close is null).Select(_ => _.Event.Id), cancel);
        if (closes.Count == 0)
        {
            return read;
        }

        var ended = new List<ScryDisclosureEntry>(read.Count);
        foreach (var entry in read)
        {
            if (entry.Close is not null ||
                !closes.TryGetValue(entry.Event.Id, out var close))
            {
                ended.Add(entry);
                continue;
            }

            if (close.Outcome != ScryDisclosureOutcome.Retracted)
            {
                ended.Add(new(entry.Event, close));
            }
        }

        return ended;
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

        // Which segment the answer's batches went to is asked first, of every segment, and everything
        // after is asked of that one: an answer is in one segment, however many are kept. But for one
        // whose segment was being retired when a later part of it arrived, which went to the segment
        // then being written to: such an answer is read through the views over all of them, which is
        // the one way to have its units and its close together.
        ScryDisclosureEvent disclosed;
        ScryDisclosureClose? closed;
        var fields = new List<ScryDisclosureField>();
        var units = new List<(int Ordinal, ScryDisclosureAddress Address, Kept? Kept)>();
        var entities = new List<ScryDisclosureEntity>();
        Dictionary<ScryDisclosureAddress, Kept> copies;
        await using (var connection = await OpenAsync(cancel))
        {
            var segments = new List<int>();
            await using (var find = Command(connection, $"SELECT DISTINCT b.[Segment] FROM {sql.Batch} b WHERE b.[EventId] = @event;"))
            {
                find.Parameters.AddWithValue("@event", eventId);
                await using var reader = await find.ExecuteReaderAsync(cancel);
                while (await reader.ReadAsync(cancel))
                {
                    segments.Add(reader.GetInt32(0));
                }
            }

            if (segments.Count == 0)
            {
                return null;
            }

            var read = sql.Anywhere;
            if (segments.Count == 1)
            {
                read = sql.Reading(segments[0]);
            }

            await using (var command = Command(
                             connection,
                             $"""
                              SELECT {sql.Columns} FROM {read.Event} e {read.Ended} WHERE e.[EventId] = @event;
                              SELECT f.[Source], f.[Member], f.[FieldUse], f.[Sensitive] FROM {read.Field} f JOIN {read.Event} e ON e.[Shape] = f.[Shape] AND e.[Segment] = f.[Segment] WHERE e.[EventId] = @event ORDER BY f.[Position];
                              SELECT u.[Ordinal], u.[Content], k.[Kind], k.[Length], k.[Bytes], k.[Elsewhere], k.[Erased] FROM {read.Units} u LEFT JOIN {read.Content} k ON k.[Address] = u.[Content] AND k.[Segment] = u.[Segment] WHERE u.[EventId] = @event ORDER BY u.[Ordinal];
                              SELECT n.[Ordinal], n.[Slot], n.[Source], n.[RowKey], n.[Via], n.[Version] FROM {read.Entities} n WHERE n.[EventId] = @event ORDER BY n.[Ordinal], n.[Slot];
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
                    entities.Add(
                        new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))
                        {
                            Version = Text(reader, 5)
                        });
                }
            }

            // What the segment holds no bytes of is asked of every segment, as asking for a piece by
            // its address is: a piece erased and since sent again is kept again where it was sent, and
            // one recorded by digest takes the bytes a later answer carried, in whichever segment.
            copies = await HeldElsewhere(connection, [.. units.Where(_ => Lacks(_.Kept)).Select(_ => _.Address).Distinct()], cancel);
        }

        ReadOnlyMemory<byte>? request = null;
        if (disclosed.Request is { } asked &&
            await Content(asked, cancel) is {Held: true} body)
        {
            request = body.Bytes;
        }

        // A shape always names at least one member, so one with none is one the record never held —
        // or one the event's own segment never held, as for a confirmation recorded after the record
        // rolled on from the answer it confirms. That one is asked for by its address, which finds it
        // wherever it was kept.
        ScryDisclosureShape? shape = null;
        if (disclosed.Shape is { } described)
        {
            if (fields.Count > 0)
            {
                shape = new(described, fields);
            }
            else
            {
                shape = await Shape(described, cancel);
            }
        }

        var rebuilt = new List<ScryDisclosedUnit>(units.Count);
        foreach (var (ordinal, address, kept) in units)
        {
            var from = entities.Where(_ => _.Ordinal == ordinal).ToList();
            var held = kept;
            if (copies.TryGetValue(address, out var copy))
            {
                held = copy;
            }

            if (held is not { } known)
            {
                rebuilt.Add(new(ordinal, new(address, ScryDisclosureContentKind.Row, 0, default), from, Erased: false));
                continue;
            }

            rebuilt.Add(new(ordinal, await Content(address, known, cancel), from, known.Erased));
        }

        return new(disclosed, closed, request, shape, rebuilt);
    }

    // Whether what a segment holds of a piece is its address alone: nothing there, or a row with no
    // bytes in the table and none kept elsewhere — recorded by digest, or erased.
    static bool Lacks(Kept? kept) =>
        kept is null or {Bytes: null, Elsewhere: false};

    // The bytes of pieces a segment holds none of, from whichever segment holds them: for each, the
    // copy with bytes from the newest segment, which is what asking for the piece by its address finds.
    async Task<Dictionary<ScryDisclosureAddress, Kept>> HeldElsewhere(SqlConnection connection, List<ScryDisclosureAddress> addresses, Cancel cancel)
    {
        var found = new Dictionary<ScryDisclosureAddress, Kept>();

        // A few hundred at a time: a list of values has a bound, and such pieces are usually few.
        foreach (var chunk in addresses.Chunk(500))
        {
            var wanted = string.Join(", ", chunk.Select(_ => $"(CAST(0x{Convert.ToHexString(_.ToArray())} AS binary(32)))"));
            await using var command = Command(
                connection,
                $"""
                 SELECT w.[Address], k.[Kind], k.[Length], k.[Bytes], k.[Elsewhere], k.[Erased]
                 FROM (VALUES {wanted}) w ([Address])
                 CROSS APPLY (SELECT TOP (1) x.[Kind], x.[Length], x.[Bytes], x.[Elsewhere], x.[Erased] FROM {sql.Content} x WHERE x.[Address] = w.[Address] AND (x.[Bytes] IS NOT NULL OR x.[Elsewhere] = 1) ORDER BY x.[Segment] DESC) k;
                 """);
            await using var reader = await command.ExecuteReaderAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                found[ScryDisclosureAddress.From((byte[]) reader[0])] = Held(reader, 1);
            }
        }

        return found;
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
            var read = new List<ScryDisclosureReview>(Page);
            await using (var connection = await OpenAsync(cancel))
            await using (var command = Command(connection, query))
            {
                command.Parameters.AddWithValue("@page", Page);
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

            if (read.Count < Page)
            {
                yield break;
            }

            after = ScryDisclosureCursor.After(read[^1]);
        }
    }
}
