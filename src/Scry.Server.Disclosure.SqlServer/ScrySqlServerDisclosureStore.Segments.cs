namespace Scry;

public sealed partial class ScrySqlServerDisclosureStore
{
    /// <summary>
    /// Every segment the record has had, oldest first: the ones being kept, and for each one retired
    /// when that was and how much it held.
    /// </summary>
    public async Task<IReadOnlyList<ScryDisclosureSegment>> SegmentsAsync(Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        await using var connection = await OpenAsync(cancel);
        return await Registry(connection, null, cancel);
    }

    /// <summary>
    /// Starts a new segment of the record now, whatever
    /// <see cref="ScrySqlServerDisclosureOptions.RollAfter"/> and
    /// <see cref="ScrySqlServerDisclosureOptions.RollAtBytes"/> say, and retires the oldest where
    /// there are then more than <see cref="ScrySqlServerDisclosureOptions.Keep"/>. Returns the
    /// segment started.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The record was being written to for longer than
    /// <see cref="ScrySqlServerDisclosureOptions.AcceptTimeout"/>. No segment was started.
    /// </exception>
    public async Task<ScryDisclosureSegment> RollAsync(Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        ScryDisclosureSegment started;
        await using (var connection = await OpenAsync(cancel))
        await using (var transaction = (SqlTransaction) await connection.BeginTransactionAsync(cancel))
        {
            if (!await Writing(connection, transaction, options.AcceptTimeout, cancel))
            {
                throw new TimeoutException($"The disclosure record was being written to for longer than {options.AcceptTimeout}, so no segment was started. Try again.");
            }

            var segments = await Registry(connection, transaction, cancel);
            await Roll(connection, transaction, segments, options.Clock.GetUtcNow(), cancel);
            started = Current(await Registry(connection, transaction, cancel));
            await transaction.CommitAsync(cancel);
        }

        await RetireAsync(cancel);
        return started;
    }

    /// <summary>
    /// Retires the oldest segments while there are more than
    /// <see cref="ScrySqlServerDisclosureOptions.Keep"/>: each is handed to
    /// <see cref="ScrySqlServerDisclosureOptions.Retire"/> where that is set, and then dropped.
    /// Returns how many it retired. Done after every move of batches on; a host that moves nothing
    /// for long stretches can call it itself.
    /// </summary>
    /// <remarks>
    /// One node at a time: a second finds the first at it and leaves it to it. A segment is marked
    /// before it is handed on, and from then nothing more is written to it, so what is handed on is
    /// the whole of it.
    /// </remarks>
    public async Task<int> RetireAsync(Cancel cancel = default)
    {
        if (options.Keep is not { } keep)
        {
            return 0;
        }

        await EnsureAsync(cancel);
        var resource = $"Scry.Disclosure.Retire.{sql.Schema}";
        await using var guard = await OpenAsync(cancel);
        await using (var locking = Command(guard, "DECLARE @held int; EXEC @held = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @held;"))
        {
            locking.Parameters.AddWithValue("@resource", resource);
            if ((int) (await locking.ExecuteScalarAsync(cancel))! < 0)
            {
                return 0;
            }
        }

        try
        {
            var retired = 0;
            while (await Oldest(keep, cancel) is { } oldest)
            {
                var handedOn = false;
                if (options.Retire is { } retire)
                {
                    await retire(new(this, oldest), cancel);
                    handedOn = true;
                }

                await Drop(oldest, handedOn, cancel);
                retired++;
            }

            return retired;
        }
        finally
        {
            await using var release = Command(guard, "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';");
            release.Parameters.AddWithValue("@resource", resource);
            await release.ExecuteNonQueryAsync(Cancel.None);
        }
    }

    // The oldest segment, where there are more than are to be kept: marked as being retired, so that
    // nothing more is written to it, and described as it then stands. Never the one being written
    // to, since at least one is always kept.
    async Task<ScryDisclosureSegment?> Oldest(int keep, Cancel cancel)
    {
        await using var connection = await OpenAsync(cancel);
        ScryDisclosureSegment oldest;
        await using (var transaction = (SqlTransaction) await connection.BeginTransactionAsync(cancel))
        {
            if (!await Writing(connection, transaction, options.AcceptTimeout, cancel))
            {
                throw new TimeoutException($"The disclosure record was being written to for longer than {options.AcceptTimeout}, so no segment was retired.");
            }

            var kept = (await Registry(connection, transaction, cancel)).Where(_ => _.Retired is null).ToList();
            if (kept.Count <= keep)
            {
                return null;
            }

            oldest = kept[0];
            if (!oldest.Retiring)
            {
                await Enter(connection, transaction, oldest.Number, DisclosureSegmentEntry.Retiring, options.Clock.GetUtcNow(), cancel);
            }

            await transaction.CommitAsync(cancel);
        }

        var tables = sql.Segment(oldest.Number);
        await using var command = Command(
            connection,
            $"""
             SELECT (SELECT COUNT_BIG(*) FROM {tables.Batch}), (SELECT COUNT_BIG(*) FROM {tables.Event}), (SELECT MIN(e.[At]) FROM {tables.Event} e), (SELECT MAX(e.[At]) FROM {tables.Event} e),
                    ({Size});
             """);
        command.Parameters.AddWithValue("@schema", tables.Schema);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        await reader.ReadAsync(cancel);
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        if (!reader.IsDBNull(2))
        {
            from = reader.GetDateTimeOffset(2);
            to = reader.GetDateTimeOffset(3);
        }

        return oldest with
        {
            Retiring = true,
            Batches = reader.GetInt64(0),
            Events = reader.GetInt64(1),
            From = oldest.From ?? from,
            To = oldest.To ?? to,
            Bytes = reader.GetInt64(4)
        };
    }

    // How much of the database a segment takes: every page of every table of its schema.
    const string Size =
        "SELECT ISNULL(SUM(a.[total_pages]), 0) * 8192 FROM sys.objects o JOIN sys.partitions p ON p.[object_id] = o.[object_id] JOIN sys.allocation_units a ON a.[container_id] IN (p.[hobt_id], p.[partition_id]) WHERE o.[schema_id] = SCHEMA_ID(@schema) AND o.[type] = 'U'";

    // The end of a segment: out of the views the record is read through, its tables dropped, and a
    // line saying when and how much it held. What it kept outside the database goes too, where no
    // other segment was sent the same.
    async Task Drop(ScryDisclosureSegment segment, bool handedOn, Cancel cancel)
    {
        var tables = sql.Segment(segment.Number);
        await using var connection = await OpenAsync(cancel);
        await using var transaction = (SqlTransaction) await connection.BeginTransactionAsync(cancel);
        if (!await Writing(connection, transaction, options.AcceptTimeout, cancel))
        {
            throw new TimeoutException($"The disclosure record was being written to for longer than {options.AcceptTimeout}, so segment {segment.Number} was not retired. It is tried again the next time the record is moved on.");
        }

        if (options.Content is { } elsewhere)
        {
            var only = new List<ScryDisclosureAddress>();
            await using (var find = Command(
                             connection,
                             $"SELECT k.[Address] FROM {tables.Content} k WHERE k.[Elsewhere] = 1 AND NOT EXISTS (SELECT 1 FROM {sql.Content} x WHERE x.[Address] = k.[Address] AND x.[Segment] <> @segment AND x.[Elsewhere] = 1);",
                             transaction))
            {
                find.Parameters.AddWithValue("@segment", segment.Number);
                await using var reader = await find.ExecuteReaderAsync(cancel);
                while (await reader.ReadAsync(cancel))
                {
                    only.Add(ScryDisclosureAddress.From((byte[]) reader[0]));
                }
            }

            foreach (var address in only)
            {
                await elsewhere.DeleteAsync(address, cancel);
            }
        }

        var kept = (await Registry(connection, transaction, cancel))
            .Where(_ => _.Retired is null && _.Number != segment.Number)
            .Select(_ => _.Number);
        await using (var drop = Command(connection, $"{sql.Viewing(kept)}\n{tables.Dropping()}", transaction))
        {
            drop.CommandTimeout = 0;
            await drop.ExecuteNonQueryAsync(cancel);
        }

        await using (var enter = Command(
                         connection,
                         $"INSERT INTO {sql.Segments} ([Segment], [What], [At], [Batches], [Events], [Bytes], [From], [To], [HandedOn]) VALUES (@segment, @what, @at, @batches, @events, @bytes, @from, @to, @handedOn);",
                         transaction))
        {
            enter.Parameters.AddWithValue("@segment", segment.Number);
            enter.Parameters.AddWithValue("@what", (byte) DisclosureSegmentEntry.Retired);
            enter.Parameters.Add(new("@at", SqlDbType.DateTimeOffset) {Value = options.Clock.GetUtcNow()});
            enter.Parameters.Add(new("@batches", SqlDbType.BigInt) {Value = Value(segment.Batches)});
            enter.Parameters.Add(new("@events", SqlDbType.BigInt) {Value = Value(segment.Events)});
            enter.Parameters.Add(new("@bytes", SqlDbType.BigInt) {Value = Value(segment.Bytes)});
            enter.Parameters.Add(new("@from", SqlDbType.DateTimeOffset) {Value = Value(segment.From)});
            enter.Parameters.Add(new("@to", SqlDbType.DateTimeOffset) {Value = Value(segment.To)});
            enter.Parameters.AddWithValue("@handedOn", handedOn);
            await enter.ExecuteNonQueryAsync(cancel);
        }

        await transaction.CommitAsync(cancel);
    }

    // Whether the segment being written to has had its time or reached its size.
    async Task<bool> Due(SqlConnection connection, SqlTransaction transaction, ScryDisclosureSegment current, DateTimeOffset now, Cancel cancel)
    {
        if (options.RollAfter is { } after &&
            now - current.Opened >= after)
        {
            return true;
        }

        if (options.RollAtBytes is not { } bytes)
        {
            return false;
        }

        await using var command = Command(connection, Size, transaction);
        command.Parameters.AddWithValue("@schema", sql.Segment(current.Number).Schema);
        return (long) (await command.ExecuteScalarAsync(cancel))! >= bytes;
    }

    // A new segment: its tables made, the one before it marked with where the chain then stood, and
    // the views the record is read through made again over all of them.
    async Task Roll(SqlConnection connection, SqlTransaction transaction, List<ScryDisclosureSegment> segments, DateTimeOffset now, Cancel cancel)
    {
        var current = Current(segments);
        var next = segments.Max(_ => _.Number) + 1;
        var (link, hash) = await Head(connection, transaction, cancel);
        var kept = segments.Where(_ => _.Retired is null).Select(_ => _.Number).Append(next);

        // The span of time the segment covers, by when its answers were given and its reviewers
        // shown them: said once, now that nothing more will be begun in it, and what lets a question
        // about some other time leave it out.
        var tables = sql.Segment(current.Number);
        object from = DBNull.Value;
        object to = DBNull.Value;
        await using (var span = Command(
                         connection,
                         $"SELECT MIN(x.[At]), MAX(x.[At]) FROM (SELECT e.[At] FROM {tables.Event} e UNION ALL SELECT r.[At] FROM {tables.Review} r) x;",
                         transaction))
        {
            span.CommandTimeout = 0;
            await using var reader = await span.ExecuteReaderAsync(cancel);
            await reader.ReadAsync(cancel);
            if (!reader.IsDBNull(0))
            {
                from = reader.GetDateTimeOffset(0);
                to = reader.GetDateTimeOffset(1);
            }
        }

        await using (var make = Command(connection, $"{sql.Segment(next).Creating()}\n{sql.Viewing(kept)}", transaction))
        {
            make.CommandTimeout = 120;
            await make.ExecuteNonQueryAsync(cancel);
        }

        await using (var enter = Command(
                         connection,
                         $"INSERT INTO {sql.Segments} ([Segment], [What], [At], [Link], [Head], [From], [To]) VALUES (@segment, @what, @at, @link, @head, @from, @to);",
                         transaction))
        {
            enter.Parameters.AddWithValue("@segment", current.Number);
            enter.Parameters.AddWithValue("@what", (byte) DisclosureSegmentEntry.Sealed);
            enter.Parameters.Add(new("@at", SqlDbType.DateTimeOffset) {Value = now});
            enter.Parameters.AddWithValue("@link", link);
            enter.Parameters.Add(new("@head", SqlDbType.Binary, 32) {Value = hash});
            enter.Parameters.Add(new("@from", SqlDbType.DateTimeOffset) {Value = from});
            enter.Parameters.Add(new("@to", SqlDbType.DateTimeOffset) {Value = to});
            await enter.ExecuteNonQueryAsync(cancel);
        }

        await Enter(connection, transaction, next, DisclosureSegmentEntry.Opened, now, cancel);

        // Asked again by the next question that has a span of time: there is now one more to leave out.
        spans = null;
    }

    async Task Enter(SqlConnection connection, SqlTransaction transaction, int segment, DisclosureSegmentEntry what, DateTimeOffset at, Cancel cancel)
    {
        await using var enter = Command(connection, $"INSERT INTO {sql.Segments} ([Segment], [What], [At]) VALUES (@segment, @what, @at);", transaction);
        enter.Parameters.AddWithValue("@segment", segment);
        enter.Parameters.AddWithValue("@what", (byte) what);
        enter.Parameters.Add(new("@at", SqlDbType.DateTimeOffset) {Value = at});
        await enter.ExecuteNonQueryAsync(cancel);
    }

    // The segments that have been rolled away from and are still kept, with the span of time each
    // covers, as they stood when last asked. Asked again once a minute at most: a segment's span is
    // said once and never changes, so all a stale answer costs is a segment looked in that need not
    // have been.
    (long Asked, int Newest, List<ScryDisclosureSegment> Sealed)? spans;

    /// <summary>
    /// What narrows a question to the segments its span of time can reach, as a condition on the
    /// segment of the rows aliased <paramref name="alias"/>: empty where it reaches all of them.
    /// </summary>
    /// <remarks>
    /// Every segment newer than the newest known to have been rolled away from is always among them:
    /// the one being written to has no span yet, and one this node has not heard of may hold
    /// anything. So nothing is ever left out for being new, only for being known not to cover the
    /// time asked about.
    /// </remarks>
    async ValueTask<string> Within(string alias, DateTimeOffset? from, DateTimeOffset? to, Cancel cancel)
    {
        if (from is null &&
            to is null)
        {
            return "";
        }

        var known = spans;
        if (known is null ||
            Environment.TickCount64 - known.Value.Asked > 60_000)
        {
            await using var connection = await OpenAsync(cancel);
            var closed = (await Registry(connection, null, cancel)).Where(_ => _ is {Sealed: not null, Retired: null}).ToList();
            var newest = 0;
            if (closed.Count > 0)
            {
                newest = closed[^1].Number;
            }

            spans = known = (Environment.TickCount64, newest, closed);
        }

        var (_, last, rolled) = known.Value;
        var reached = rolled
            .Where(_ => _.From is { } first && _.To is { } final && (to is null || first <= to) && (from is null || final >= from))
            .Select(_ => _.Number.ToString(CultureInfo.InvariantCulture))
            .ToList();
        if (reached.Count == rolled.Count)
        {
            return "";
        }

        if (reached.Count == 0)
        {
            return $" AND {alias}.[Segment] > {last}";
        }

        return $" AND ({alias}.[Segment] > {last} OR {alias}.[Segment] IN ({string.Join(", ", reached)}))";
    }

    // The earlier of two times, either of which may not have been said.
    static DateTimeOffset? Earlier(DateTimeOffset? one, DateTimeOffset? other)
    {
        if (one is null)
        {
            return other;
        }

        if (other is null ||
            one <= other)
        {
            return one;
        }

        return other;
    }

    // The segment being written to: the newest there is, which is never retired.
    static ScryDisclosureSegment Current(List<ScryDisclosureSegment> segments) =>
        segments[^1];

    // Every segment there has been, oldest first, as the lines written about it add up to.
    async Task<List<ScryDisclosureSegment>> Registry(SqlConnection connection, SqlTransaction? transaction, Cancel cancel)
    {
        var segments = new SortedDictionary<int, ScryDisclosureSegment>();
        await using var command = Command(
            connection,
            $"SELECT s.[Segment], s.[What], s.[At], s.[Link], s.[Head], s.[Batches], s.[Events], s.[Bytes], s.[From], s.[To], s.[HandedOn] FROM {sql.Segments} s ORDER BY s.[Entry];",
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            var number = reader.GetInt32(0);
            var at = reader.GetDateTimeOffset(2);
            switch ((DisclosureSegmentEntry) reader.GetByte(1))
            {
                case DisclosureSegmentEntry.Opened:
                    segments[number] = new(number, sql.Segment(number).Schema, at);
                    break;
                case DisclosureSegmentEntry.Sealed:
                    segments[number] = segments[number] with
                    {
                        Sealed = at,
                        Link = reader.GetInt64(3),
                        Head = Address(reader, 4),
                        From = When(reader, 8),
                        To = When(reader, 9)
                    };
                    break;
                case DisclosureSegmentEntry.Retiring:
                    segments[number] = segments[number] with
                    {
                        Retiring = true
                    };
                    break;
                case DisclosureSegmentEntry.Retired:
                    segments[number] = segments[number] with
                    {
                        Retired = at,
                        Batches = Number(reader, 5),
                        Events = Number(reader, 6),
                        Bytes = Number(reader, 7),
                        HandedOn = reader.GetBoolean(10)
                    };
                    break;
            }
        }

        return [.. segments.Values];
    }

    static long? Number(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetInt64(ordinal);
    }

    static DateTimeOffset? When(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetDateTimeOffset(ordinal);
    }

    // Every batch of a segment, in the order the chain links them, each as it arrived: with the
    // shape it named and the content it carried, so that it can be handed to any sink again.
    internal async IAsyncEnumerable<ScryDisclosureBatch> Batches(ScryDisclosureSegment segment, [EnumeratorCancellation] Cancel cancel)
    {
        var tables = sql.Segment(segment.Number);
        var had = new List<(Guid EventId, int Sequence)>();
        await using var connection = await OpenAsync(cancel);
        await using (var command = Command(connection, $"SELECT b.[EventId], b.[Sequence] FROM {tables.Batch} b ORDER BY b.[Link];"))
        {
            command.CommandTimeout = 0;
            await using var reader = await command.ExecuteReaderAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                had.Add((reader.GetGuid(0), reader.GetInt32(1)));
            }
        }

        foreach (var (id, sequence) in had)
        {
            yield return await Whole(connection, tables, await Recorded(connection, segment.Number, id, sequence, cancel), cancel);
        }
    }

    // A batch read back from the tables, with what the tables keep apart from it put back: the
    // members of its shape, and each piece of content it named.
    async Task<ScryDisclosureBatch> Whole(SqlConnection connection, DisclosureTables tables, ScryDisclosureBatch batch, Cancel cancel)
    {
        var fields = new List<ScryDisclosureField>();
        var kept = new List<(ScryDisclosureAddress Address, Kept Kept)>();
        await using (var command = Command(
                         connection,
                         $"""
                          SELECT f.[Source], f.[Member], f.[FieldUse], f.[Sensitive] FROM {tables.Field} f WHERE f.[Shape] = @shape ORDER BY f.[Position];
                          SELECT k.[Address], k.[Kind], k.[Length], k.[Bytes], k.[Elsewhere], k.[Erased] FROM {tables.Content} k
                          WHERE k.[Address] IN (SELECT u.[Content] FROM {tables.Units} u WHERE u.[EventId] = @event AND u.[Sequence] = @sequence) OR k.[Address] = @request OR k.[Address] = @parameters;
                          """))
        {
            command.Parameters.Add(new("@shape", SqlDbType.Binary, 32) {Value = Value(Bytes(batch.Begin?.Shape))});
            command.Parameters.Add(new("@request", SqlDbType.Binary, 32) {Value = Value(Bytes(batch.Begin?.Request))});
            command.Parameters.Add(new("@parameters", SqlDbType.Binary, 32) {Value = Value(Bytes(batch.Review?.Parameters))});
            command.Parameters.AddWithValue("@event", batch.EventId);
            command.Parameters.AddWithValue("@sequence", batch.Sequence);
            await using var reader = await command.ExecuteReaderAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                fields.Add(new(reader.GetString(0), reader.GetString(1), (ScryDisclosureFieldUse) reader.GetByte(2), reader.GetBoolean(3)));
            }

            await reader.NextResultAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                kept.Add((ScryDisclosureAddress.From((byte[]) reader[0]), Held(reader, 1)));
            }
        }

        ScryDisclosureShape? shape = null;
        if (batch.Begin?.Shape is { } described &&
            fields.Count > 0)
        {
            shape = new(described, fields);
        }

        var contents = new List<ScryDisclosureContent>(kept.Count);
        foreach (var (address, held) in kept)
        {
            contents.Add(await Content(address, held, cancel));
        }

        return new()
        {
            EventId = batch.EventId,
            Sequence = batch.Sequence,
            Begin = batch.Begin,
            Shape = shape,
            Units = batch.Units,
            Entities = batch.Entities,
            Contents = contents,
            Close = batch.Close,
            Review = batch.Review
        };
    }
}

// What a line about a segment says happened to it.
enum DisclosureSegmentEntry : byte
{
    Opened = 1,
    Sealed = 2,
    Retiring = 3,
    Retired = 4
}
