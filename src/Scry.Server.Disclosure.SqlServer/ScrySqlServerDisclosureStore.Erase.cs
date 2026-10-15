namespace Scry;

public sealed partial class ScrySqlServerDisclosureStore
{
    ScryDisclosureChainCheck? verified;

    /// <inheritdoc />
    /// <remarks>
    /// What is still in the outbox is moved on first, so that an answer accepted a moment ago is
    /// erased with the rest. What stays is everything but the content: the events, the addresses and
    /// the key are in tables that are only ever added to, and say that something was sent long after
    /// what it was is gone.
    /// </remarks>
    /// <exception cref="TimeoutException">
    /// The record was being written to for longer than
    /// <see cref="ScrySqlServerDisclosureOptions.AcceptTimeout"/>. Nothing was erased.
    /// </exception>
    public async ValueTask<ScryDisclosureErasure> EraseAsync(string source, IReadOnlyList<object?> key, string? by, Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        var text = ScryDisclosureEntity.KeyOf(key);
        var row = DisclosureSql.RowHash(source, text);
        var at = options.Clock.GetUtcNow();

        await using var connection = await OpenAsync(cancel);
        await using var transaction = (SqlTransaction) await connection.BeginTransactionAsync(cancel);

        // Waited for, unlike a round of moving batches on: this was asked for by somebody.
        if (!await Writing(connection, transaction, options.AcceptTimeout, cancel))
        {
            throw new TimeoutException($"The disclosure record was being written to for longer than {options.AcceptTimeout}, so nothing was erased. Try again.");
        }

        // Under that lock, so that nothing accepted before this is moved on after it: an answer
        // sitting in the outbox would otherwise bring back what is about to be removed.
        while (await Move(connection, transaction, cancel) == options.DrainBatches)
        {
        }

        // Every piece of content a unit read from this row carried, in whichever segment. Erasure wins
        // over reconstruction: a piece another row happened to be sent as goes too.
        // Made by a statement with no parameters, which is what leaves a temporary table behind it
        // for the statements after: one made by a statement that has them goes when it ends.
        await using (var make = Command(connection, "CREATE TABLE #erase ([Address] binary(32) NOT NULL PRIMARY KEY);", transaction))
        {
            await make.ExecuteNonQueryAsync(cancel);
        }

        await using (var find = Command(
                         connection,
                         $"INSERT INTO #erase ([Address]) SELECT DISTINCT n.[Content] FROM {sql.Entities} n WHERE n.[RowHash] = @row AND n.[Source] = @source AND n.[RowKey] = @key;",
                         transaction))
        {
            Row(find, row, source, text);
            await find.ExecuteNonQueryAsync(cancel);
        }

        // What is kept outside the database goes first. Said to be erased, it has to be: a record
        // that claimed so while the bytes sat in a bucket would be the one thing worse than neither.
        if (options.Content is { } elsewhere)
        {
            var kept = new List<ScryDisclosureAddress>();
            await using (var find = Command(connection, $"SELECT DISTINCT k.[Address] FROM {sql.Content} k JOIN #erase x ON x.[Address] = k.[Address] WHERE k.[Erased] = 0 AND k.[Elsewhere] = 1;", transaction))
            await using (var reader = await find.ExecuteReaderAsync(cancel))
            {
                while (await reader.ReadAsync(cancel))
                {
                    kept.Add(ScryDisclosureAddress.From((byte[]) reader[0]));
                }
            }

            foreach (var address in kept)
            {
                await elsewhere.DeleteAsync(address, cancel);
            }
        }

        // A piece is kept in each segment it was sent in, and goes from every one of them. Counted
        // once however many that is. The erasure itself is written to the segment being written to,
        // and is retired with it: it names the row.
        var segments = await Registry(connection, transaction, cancel);
        var erasing = new StringBuilder();
        erasing.AppendLine($"DECLARE @units int = (SELECT COUNT(*) FROM #erase x WHERE EXISTS (SELECT 1 FROM {sql.Content} k WHERE k.[Address] = x.[Address] AND k.[Erased] = 0));");
        foreach (var segment in segments.Where(_ => _.Retired is null))
        {
            erasing.AppendLine($"UPDATE k SET k.[Bytes] = NULL, k.[Elsewhere] = 0, k.[Erased] = 1 FROM {sql.Segment(segment.Number).Content} k JOIN #erase x ON x.[Address] = k.[Address] WHERE k.[Erased] = 0;");
        }

        erasing.AppendLine($"INSERT INTO {sql.Segment(Current(segments).Number).Erasure} ([At], [By], [RowHash], [Source], [RowKey], [Units]) VALUES (@at, @by, @row, @source, @key, @units);");
        erasing.AppendLine("DROP TABLE #erase;");
        erasing.AppendLine("SELECT @units;");
        int units;
        await using (var erase = Command(connection, erasing.ToString(), transaction))
        {
            Row(erase, row, source, text);
            erase.Parameters.Add(new("@at", SqlDbType.DateTimeOffset) {Value = at});
            erase.Parameters.Add(new("@by", SqlDbType.NVarChar, -1) {Value = Value(by)});
            units = (int) (await erase.ExecuteScalarAsync(cancel))!;
        }

        await transaction.CommitAsync(cancel);
        return new(at, by, source, text, units);
    }

    static void Row(SqlCommand command, byte[] row, string source, string key)
    {
        command.Parameters.Add(new("@row", SqlDbType.Binary, 32) {Value = row});
        command.Parameters.Add(new("@source", SqlDbType.NVarChar, -1) {Value = source});
        command.Parameters.Add(new("@key", SqlDbType.NVarChar, -1) {Value = key});
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScryDisclosureErasure> Erasures([EnumeratorCancellation] Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        var read = new List<ScryDisclosureErasure>();
        await using (var connection = await OpenAsync(cancel))
        await using (var command = Command(connection, $"SELECT x.[At], x.[By], x.[Source], x.[RowKey], x.[Units] FROM {sql.Erasure} x ORDER BY x.[At] DESC, x.[Segment] DESC, x.[Id] DESC;"))
        await using (var reader = await command.ExecuteReaderAsync(cancel))
        {
            while (await reader.ReadAsync(cancel))
            {
                read.Add(new(reader.GetDateTimeOffset(0), Text(reader, 1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
            }
        }

        foreach (var erasure in read)
        {
            yield return erasure;
        }
    }

    /// <inheritdoc />
    public async ValueTask<ScryDisclosureStoreStatus> Status(Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        await using var connection = await OpenAsync(cancel);

        // The count of events is each segment's table's own row count rather than a count of its
        // rows: one is a number the database already has, the other a scan of the largest tables
        // there are.
        await using var command = Command(
            connection,
            $"""
             SELECT COUNT_BIG(*), ISNULL(SUM(CAST(DATALENGTH(o.[Batch]) AS bigint)), 0), MIN(o.[AcceptedAt]), ISNULL(SUM(CAST(o.[Unreadable] AS bigint)), 0) FROM {sql.Outbox} o;
             SELECT ISNULL(SUM(p.[rows]), 0) FROM sys.partitions p JOIN sys.objects o ON o.[object_id] = p.[object_id] JOIN sys.schemas s ON s.[schema_id] = o.[schema_id]
             WHERE o.[name] = N'DisclosureEvent' AND o.[type] = 'U' AND s.[name] LIKE @segments AND p.[index_id] IN (0, 1);
             SELECT TOP (1) l.[Hash] FROM {sql.Chain} l ORDER BY l.[Link] DESC;
             """);
        command.Parameters.AddWithValue("@segments", sql.Schema.Replace("_", "[_]") + "[_][0-9][0-9][0-9][0-9][0-9][0-9]");
        await using var reader = await command.ExecuteReaderAsync(cancel);
        await reader.ReadAsync(cancel);
        var pending = reader.GetInt64(0);
        var bytes = reader.GetInt64(1);
        DateTimeOffset? oldest = null;
        if (!reader.IsDBNull(2))
        {
            oldest = reader.GetDateTimeOffset(2);
        }

        var unreadable = reader.GetInt64(3);
        await reader.NextResultAsync(cancel);
        await reader.ReadAsync(cancel);
        var events = reader.GetInt64(0);
        await reader.NextResultAsync(cancel);
        ScryDisclosureAddress? head = null;
        if (await reader.ReadAsync(cancel))
        {
            head = ScryDisclosureAddress.From((byte[]) reader[0]);
        }

        await reader.CloseAsync();
        return new(pending, bytes, oldest, events)
        {
            ChainHead = head,
            LastVerification = verified,
            Problem = Problem(unreadable),
            Segments = await Registry(connection, null, cancel),
            Retention = await Retention(connection, null, cancel)
        };
    }

    // What is in the way of the record being whole, where anything is: batches that will not read
    // back, which are kept where they are for somebody to look at, or a move that keeps failing.
    string? Problem(long unreadable)
    {
        if (unreadable > 0)
        {
            return $"{unreadable} accepted batch(es) could not be read back and are held in {sql.Outbox} with [Unreadable] set. They were accepted, so what they record was sent.";
        }

        return problem;
    }

    /// <summary>
    /// Checks a stretch of the hash chain: that each link's hash covers the one before it, and that
    /// what each link was made from is still what the record holds.
    /// </summary>
    /// <param name="from">The link to start at, or null for the first.</param>
    /// <param name="count">How many links to check.</param>
    /// <param name="cancel">Stops the check.</param>
    /// <remarks>
    /// <para>
    /// The second half is what makes the chain worth having without ledger tables: every link holds a
    /// digest of the rows its batch added, and the check makes that digest again from the rows as
    /// they now are. A row changed, added to or removed under a batch shows as the link that no
    /// longer matches, and a link taken out shows as the one after it. Content is not part of it — it
    /// is kept apart so that it can be erased — but a unit's address is, and an address is a hash of
    /// the content it names.
    /// </para>
    /// <para>
    /// What the chain cannot show by itself is its own end being cut off, or the whole of it written
    /// again from some link on by somebody able to write every table: both leave a chain that holds.
    /// For those, keep <see cref="ScryDisclosureStoreStatus.ChainHead"/> somewhere that somebody
    /// cannot write, and ask <see cref="FindLinkAsync"/> whether the chain still has it.
    /// </para>
    /// </remarks>
    public async Task<ScryDisclosureChainCheck> VerifyChainAsync(long? from = null, int count = 10_000, Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        var links = new List<(long Link, int Segment, byte[] Digest, byte[] Hash)>();
        var batches = new Dictionary<long, (Guid EventId, int Sequence)>();
        var previous = new byte[ScryDisclosureAddress.Size];
        await using var connection = await OpenAsync(cancel);
        var kept = (await Registry(connection, null, cancel)).Where(_ => _.Retired is null).Select(_ => _.Number).ToHashSet();
        await using (var command = Command(
                         connection,
                         $"""
                          SELECT TOP (1) l.[Hash] FROM {sql.Chain} l WHERE @from IS NOT NULL AND l.[Link] < @from ORDER BY l.[Link] DESC;
                          SELECT TOP (@count) l.[Link], l.[Segment], l.[Digest], l.[Hash] FROM {sql.Chain} l WHERE @from IS NULL OR l.[Link] >= @from ORDER BY l.[Link];
                          SELECT b.[Link], b.[EventId], b.[Sequence] FROM {sql.Batch} b
                          WHERE b.[Link] IN (SELECT TOP (@count) l.[Link] FROM {sql.Chain} l WHERE @from IS NULL OR l.[Link] >= @from ORDER BY l.[Link]);
                          """))
        {
            command.Parameters.Add(new("@from", SqlDbType.BigInt) {Value = Value(from)});
            command.Parameters.AddWithValue("@count", count);
            await using var reader = await command.ExecuteReaderAsync(cancel);
            if (await reader.ReadAsync(cancel))
            {
                previous = (byte[]) reader[0];
            }

            await reader.NextResultAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                links.Add((reader.GetInt64(0), reader.GetInt32(1), (byte[]) reader[2], (byte[]) reader[3]));
            }

            await reader.NextResultAsync(cancel);
            while (await reader.ReadAsync(cancel))
            {
                batches[reader.GetInt64(0)] = (reader.GetGuid(1), reader.GetInt32(2));
            }
        }

        long? broken = null;
        foreach (var (link, segment, digest, hash) in links)
        {
            if (!DisclosureChain.Hash(previous, digest).AsSpan().SequenceEqual(hash) ||
                !await Holds(connection, kept, batches, link, segment, digest, cancel))
            {
                broken = link;
                break;
            }

            previous = hash;
        }

        verified = new(options.Clock.GetUtcNow(), links.Count, broken is null)
        {
            BrokenAt = broken
        };
        return verified;
    }

    // Whether what a link was made from is still what the record holds. A link whose segment was
    // retired has nothing left to be made from again, and holds by its place in the chain alone: its
    // digest is what an archive of the segment is checked against. A link whose segment is still
    // kept has to have its batch, and the batch has to make the digest again.
    async Task<bool> Holds(
        SqlConnection connection,
        HashSet<int> kept,
        Dictionary<long, (Guid EventId, int Sequence)> batches,
        long link,
        int segment,
        byte[] digest,
        Cancel cancel)
    {
        if (!kept.Contains(segment))
        {
            return true;
        }

        if (!batches.TryGetValue(link, out var batch))
        {
            return false;
        }

        var recorded = await Recorded(connection, segment, batch.EventId, batch.Sequence, cancel);
        return DisclosureChain.Digest(recorded).AsSpan().SequenceEqual(digest);
    }

    /// <summary>
    /// Checks the whole chain, from its first link to its last, a stretch at a time: each stretch is
    /// a bounded read, so a chain of any length is checked without one read holding the database for
    /// the length of it. For a background job rather than a request: it takes as long as the chain is
    /// long, which <paramref name="progress"/> reports as it goes, a link count at a time.
    /// </summary>
    /// <param name="progress">Told how many links have been checked so far, after each stretch.</param>
    /// <param name="stretch">How many links are read and checked at a time.</param>
    /// <param name="cancel">Stops the check where it is.</param>
    /// <remarks>
    /// What it finds is what <see cref="ScryDisclosureStoreStatus.LastVerification"/> says afterwards:
    /// every link checked, or the first one that did not hold.
    /// </remarks>
    public async Task<ScryDisclosureChainCheck> VerifyWholeChainAsync(IProgress<long>? progress = null, int stretch = 10_000, Cancel cancel = default)
    {
        if (stretch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stretch), "A stretch is at least one link.");
        }

        long? from = null;
        long checkedSoFar = 0;
        while (true)
        {
            var part = await VerifyChainAsync(from, stretch, cancel);
            checkedSoFar += part.Records;
            progress?.Report(checkedSoFar);
            if (!part.Intact ||
                part.Records < stretch)
            {
                verified = part with
                {
                    Records = checkedSoFar
                };
                return verified;
            }

            // Links are numbered one after another by the chain, so the next stretch starts where
            // this one ended. A link taken out of the middle is a break the stretch it was in finds.
            from = (from ?? await First(cancel)) + part.Records;
        }
    }

    // The number of the chain's first link.
    async Task<long> First(Cancel cancel)
    {
        await using var connection = await OpenAsync(cancel);
        await using var command = Command(connection, $"SELECT ISNULL(MIN(l.[Link]), 1) FROM {sql.Chain} l;");
        return (long) (await command.ExecuteScalarAsync(cancel))!;
    }

    async ValueTask<ScryDisclosureChainCheck> IScryDisclosureVerifier.Verify(long? from, int count, Cancel cancel) =>
        await VerifyChainAsync(from, count, cancel);

    /// <summary>
    /// Which link of the chain has this hash, or null where none does. How a chain head written down
    /// outside the database is checked: a chain that no longer has it was cut short, or written
    /// again, after it was taken.
    /// </summary>
    public async Task<long?> FindLinkAsync(ScryDisclosureAddress hash, Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        await using var connection = await OpenAsync(cancel);
        await using var command = Command(connection, $"SELECT TOP (1) l.[Link] FROM {sql.Chain} l WHERE l.[Hash] = @hash ORDER BY l.[Link];");
        command.Parameters.Add(new("@hash", SqlDbType.Binary, 32) {Value = hash.ToArray()});
        if (await command.ExecuteScalarAsync(cancel) is long link)
        {
            return link;
        }

        return null;
    }

    // One batch as the record now holds it: the rows that batch added, read back into the shape they
    // arrived in. What the chain's digest of it is made from again. Asked of the segment the batch
    // went to, which its link says.
    async Task<ScryDisclosureBatch> Recorded(SqlConnection connection, int segment, Guid id, int sequence, Cancel cancel)
    {
        var read = sql.Reading(segment);
        await using var command = Command(
            connection,
            $"""
             SELECT {sql.Columns} FROM {read.Event} e {read.Ended} WHERE e.[EventId] = @event AND e.[Sequence] = @sequence AND e.[Segment] = @segment;
             SELECT u.[Ordinal], u.[Content] FROM {read.Units} u WHERE u.[EventId] = @event AND u.[Sequence] = @sequence AND u.[Segment] = @segment ORDER BY u.[Ordinal];
             SELECT n.[Ordinal], n.[Slot], n.[Source], n.[RowKey], n.[Via], n.[Version] FROM {read.Entities} n WHERE n.[EventId] = @event AND n.[Sequence] = @sequence AND n.[Segment] = @segment ORDER BY n.[Ordinal], n.[Slot];
             SELECT x.[Outcome], x.[Units], x.[At], x.[Response] FROM {read.Close} x WHERE x.[EventId] = @event AND x.[Sequence] = @sequence AND x.[Segment] = @segment;
             SELECT r.[ReviewId], r.[At], r.[Reviewer], r.[Question], r.[Parameters], r.[Results], r.[Node] FROM {read.Review} r WHERE r.[ReviewId] = @event AND @sequence = 0 AND r.[Segment] = @segment;
             SELECT v.[EventId] FROM {read.ReviewEvent} v WHERE v.[ReviewId] = @event AND @sequence = 0 AND v.[Segment] = @segment;
             """);
        command.Parameters.AddWithValue("@event", id);
        command.Parameters.AddWithValue("@sequence", sequence);
        command.Parameters.AddWithValue("@segment", segment);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        ScryDisclosureEvent? begin = null;
        if (await reader.ReadAsync(cancel))
        {
            begin = Event(reader);
        }

        var units = new List<ScryDisclosureUnit>();
        await reader.NextResultAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            units.Add(new(reader.GetInt32(0), ScryDisclosureAddress.From((byte[]) reader[1])));
        }

        var entities = new List<ScryDisclosureEntity>();
        await reader.NextResultAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            entities.Add(
                new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))
                {
                    Version = Text(reader, 5)
                });
        }

        ScryDisclosureClose? close = null;
        await reader.NextResultAsync(cancel);
        if (await reader.ReadAsync(cancel))
        {
            close = new((ScryDisclosureOutcome) reader.GetByte(0), reader.GetInt32(1))
            {
                At = reader.GetDateTimeOffset(2),
                Response = Address(reader, 3)
            };
        }

        ScryDisclosureReview? review = null;
        await reader.NextResultAsync(cancel);
        if (await reader.ReadAsync(cancel))
        {
            review = new(reader.GetGuid(0), reader.GetDateTimeOffset(1), (ScryDisclosureQuestion) reader.GetByte(3))
            {
                Reviewer = Text(reader, 2),
                Parameters = Address(reader, 4),
                Results = reader.GetInt32(5),
                Node = Text(reader, 6)
            };
        }

        var shown = new List<Guid>();
        await reader.NextResultAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            shown.Add(reader.GetGuid(0));
        }

        if (review is not null)
        {
            review = review with
            {
                Events = shown
            };
        }

        return new()
        {
            EventId = id,
            Sequence = sequence,
            Begin = begin,
            Units = units,
            Entities = entities,
            Close = close,
            Review = review
        };
    }
}
