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

        // Every piece of content a unit read from this row carried. Erasure wins over reconstruction:
        // a piece another row happened to be sent as goes too.
        var carried =
            $"""
             SELECT n.[Content] FROM {sql.Entities} n
             WHERE n.[RowHash] = @row AND n.[Source] = @source AND n.[RowKey] = @key
             """;

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

        // What is kept outside the database goes first. Said to be erased, it has to be: a record
        // that claimed so while the bytes sat in a bucket would be the one thing worse than neither.
        if (options.Content is { } elsewhere)
        {
            var kept = new List<ScryDisclosureAddress>();
            await using (var find = Command(connection, $"SELECT k.[Address] FROM {sql.Content} k WITH (UPDLOCK) WHERE k.[Erased] = 0 AND k.[Elsewhere] = 1 AND k.[Address] IN ({carried});", transaction))
            {
                Row(find, row, source, text);
                await using var reader = await find.ExecuteReaderAsync(cancel);
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

        int units;
        await using (var erase = Command(
                         connection,
                         $"""
                          UPDATE k SET k.[Bytes] = NULL, k.[Elsewhere] = 0, k.[Erased] = 1
                          FROM {sql.Content} k
                          WHERE k.[Erased] = 0 AND k.[Address] IN ({carried});
                          DECLARE @units int = @@ROWCOUNT;
                          INSERT INTO {sql.Erasure} ([At], [By], [RowHash], [Source], [RowKey], [Units]) VALUES (@at, @by, @row, @source, @key, @units);
                          SELECT @units;
                          """,
                         transaction))
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
        await using (var command = Command(connection, $"SELECT x.[At], x.[By], x.[Source], x.[RowKey], x.[Units] FROM {sql.Erasure} x ORDER BY x.[Id] DESC;"))
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

        // The count of events is the table's own row count rather than a count of its rows: one is a
        // number the database already has, the other a scan of the largest table there is.
        await using var command = Command(
            connection,
            $"""
             SELECT COUNT_BIG(*), ISNULL(SUM(CAST(DATALENGTH(o.[Batch]) AS bigint)), 0), MIN(o.[AcceptedAt]), ISNULL(SUM(CAST(o.[Unreadable] AS bigint)), 0) FROM {sql.Outbox} o;
             SELECT ISNULL(SUM(p.[rows]), 0) FROM sys.partitions p WHERE p.[object_id] = OBJECT_ID(@events) AND p.[index_id] IN (0, 1);
             SELECT TOP (1) l.[Hash] FROM {sql.Chain} l ORDER BY l.[Link] DESC;
             """);
        command.Parameters.AddWithValue("@events", sql.Event);
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

        return new(pending, bytes, oldest, events)
        {
            ChainHead = head,
            LastVerification = verified,
            Problem = Problem(unreadable)
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
        var links = new List<(long Link, Guid EventId, int Sequence, byte[] Digest, byte[] Hash)>();
        var previous = new byte[ScryDisclosureAddress.Size];
        await using var connection = await OpenAsync(cancel);
        await using (var command = Command(
                         connection,
                         $"""
                          SELECT TOP (1) l.[Hash] FROM {sql.Chain} l WHERE @from IS NOT NULL AND l.[Link] < @from ORDER BY l.[Link] DESC;
                          SELECT TOP (@count) l.[Link], l.[EventId], l.[Sequence], l.[Digest], l.[Hash] FROM {sql.Chain} l WHERE @from IS NULL OR l.[Link] >= @from ORDER BY l.[Link];
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
                links.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2), (byte[]) reader[3], (byte[]) reader[4]));
            }
        }

        long? broken = null;
        foreach (var (link, id, sequence, digest, hash) in links)
        {
            var kept = await Recorded(connection, id, sequence, cancel);
            if (!DisclosureChain.Digest(kept).AsSpan().SequenceEqual(digest) ||
                !DisclosureChain.Hash(previous, digest).AsSpan().SequenceEqual(hash))
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
    // arrived in. What the chain's digest of it is made from again.
    async Task<ScryDisclosureBatch> Recorded(SqlConnection connection, Guid id, int sequence, Cancel cancel)
    {
        await using var command = Command(
            connection,
            $"""
             SELECT {sql.Columns} FROM {sql.Event} e {sql.Ended} WHERE e.[EventId] = @event AND e.[Sequence] = @sequence;
             SELECT u.[Ordinal], u.[Content] FROM {sql.Units} u WHERE u.[EventId] = @event AND u.[Sequence] = @sequence ORDER BY u.[Ordinal];
             SELECT n.[Ordinal], n.[Slot], n.[Source], n.[RowKey], n.[Via] FROM {sql.Entities} n WHERE n.[EventId] = @event AND n.[Sequence] = @sequence ORDER BY n.[Ordinal], n.[Slot];
             SELECT x.[Outcome], x.[Units], x.[At], x.[Response] FROM {sql.Close} x WHERE x.[EventId] = @event AND x.[Sequence] = @sequence;
             SELECT r.[ReviewId], r.[At], r.[Reviewer], r.[Question], r.[Parameters], r.[Results], r.[Node] FROM {sql.Review} r WHERE r.[ReviewId] = @event AND @sequence = 0;
             SELECT v.[EventId] FROM {sql.ReviewEvent} v WHERE v.[ReviewId] = @event AND @sequence = 0;
             """);
        command.Parameters.AddWithValue("@event", id);
        command.Parameters.AddWithValue("@sequence", sequence);
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
            entities.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
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
