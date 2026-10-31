namespace Scry;

public sealed partial class ScrySqlServerDisclosureStore
{
    Lock starting = new();
    SemaphoreSlim prompted = new(0, 1);
    CancelSource? stopping;
    Task? loop;
    int waiting;
    bool stopped;
    volatile string? problem;

    // An answer was accepted on this node, so it is moved on now rather than at the next interval.
    // Several accepted together prompt one move, which takes them all.
    void Accepted()
    {
        if (options.DrainInterval is null)
        {
            return;
        }

        Start();
        if (Interlocked.Exchange(ref waiting, 1) == 0)
        {
            prompted.Release();
        }
    }

    /// <summary>
    /// Starts moving accepted batches on in the background, where
    /// <see cref="ScrySqlServerDisclosureOptions.DrainInterval"/> says to. Started by the first answer
    /// this node accepts; called at startup, it also moves on what other nodes accepted, and what
    /// this one left behind when it last stopped.
    /// </summary>
    public void Start()
    {
        if (loop is not null ||
            options.DrainInterval is not { } interval)
        {
            return;
        }

        lock (starting)
        {
            if (loop is not null ||
                stopped)
            {
                return;
            }

            stopping = new();
            var stop = stopping.Token;
            loop = Task.Run(() => Loop(interval, stop), Cancel.None);
        }
    }

    async Task Loop(TimeSpan interval, Cancel stop)
    {
        // Once at the start, before waiting to be prompted: a node that has just come up moves on
        // what was left behind when it last stopped — and makes the tables, where they are its to
        // make — without waiting for its first answer or its first interval.
        var started = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (started &&
                    await prompted.WaitAsync(interval, stop))
                {
                    Interlocked.Exchange(ref waiting, 0);
                }

                started = true;
                await DrainAsync(stop);
                problem = null;
            }
            catch (OperationCanceledException)
                when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Nothing is lost: what could not be moved is still in the outbox, and is tried again
                // at the next interval. What went wrong is what the store's status says.
                problem = exception.Message;
            }
        }
    }

    /// <summary>
    /// Moves what has been accepted out of the outbox and into the tables the record is read from,
    /// until the outbox is empty. Returns how many batches it moved.
    /// </summary>
    /// <remarks>
    /// One mover at a time, across every node: a second one finds the first at work and leaves it to
    /// it. A batch is moved and removed from the outbox in one transaction, so it is moved exactly
    /// once; one that arrives a second time — a journal shipping again — is recognised and dropped.
    /// </remarks>
    public async Task<int> DrainAsync(Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        var total = 0;
        while (true)
        {
            var moved = await DrainOnce(cancel);
            total += moved;
            if (moved < options.DrainBatches)
            {
                break;
            }
        }

        // After what was moved is committed, and outside the lock it was moved under: handing a
        // segment on may take as long as the segment is large.
        await RetireAsync(cancel);
        return total;
    }

    /// <summary>
    /// Waits until everything accepted before it was called has been moved into the record, so that
    /// what is read next includes it. Moves it on itself where this node moves the record, and
    /// otherwise waits for the node that does.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// It was not all moved within <see cref="ScrySqlServerDisclosureOptions.AcceptTimeout"/>.
    /// </exception>
    public async Task CatchUpAsync(Cancel cancel = default)
    {
        await EnsureAsync(cancel);
        var started = Stopwatch.GetTimestamp();
        long? last;
        await using (var connection = await OpenAsync(cancel))
        await using (var command = Command(connection, $"SELECT MAX(o.[Position]) FROM {sql.Outbox} o WHERE o.[Unreadable] = 0;"))
        {
            last = await command.ExecuteScalarAsync(cancel) as long?;
        }

        if (last is not { } position)
        {
            return;
        }

        while (true)
        {
            if (!ReadsOnly)
            {
                await DrainAsync(cancel);
            }

            await using (var connection = await OpenAsync(cancel))
            await using (var command = Command(connection, $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {sql.Outbox} o WHERE o.[Unreadable] = 0 AND o.[Position] <= @last) THEN 1 ELSE 0 END;"))
            {
                command.Parameters.AddWithValue("@last", position);
                if ((int) (await command.ExecuteScalarAsync(cancel))! == 0)
                {
                    return;
                }
            }

            if (Stopwatch.GetElapsedTime(started) > options.AcceptTimeout)
            {
                throw new TimeoutException($"What was accepted before this question was not all moved into the disclosure record within {options.AcceptTimeout}, so it was not answered. The store's status says how much is waiting.");
            }

            await Task.Delay(20, cancel);
        }
    }

    // Set for a host that only reads the record, which never moves it on and waits for the serving
    // nodes to.
    internal bool ReadsOnly { get; init; }

    ValueTask Caught(Cancel cancel)
    {
        if (!options.CatchUpBeforeReading)
        {
            return ValueTask.CompletedTask;
        }

        return new(CatchUpAsync(cancel));
    }

    async Task<int> DrainOnce(Cancel cancel)
    {
        await using var connection = await OpenAsync(cancel);
        await using var transaction = (SqlTransaction) await connection.BeginTransactionAsync(cancel);

        // Not waited for: whoever holds it is doing this.
        if (!await Writing(connection, transaction, TimeSpan.Zero, cancel))
        {
            return 0;
        }

        var moved = await Move(connection, transaction, cancel);
        await transaction.CommitAsync(cancel);
        return moved;
    }

    // The one lock everything that changes the record takes, held to the end of the transaction: a
    // round of moving batches on, and an erasure. So the record has one writer at a time across every
    // node, the chain is written in one order, and an erasure never runs beside a move that is
    // bringing back what it removes.
    async Task<bool> Writing(SqlConnection connection, SqlTransaction transaction, TimeSpan wait, Cancel cancel)
    {
        await using var locking = Command(
            connection,
            "DECLARE @held int; EXEC @held = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @wait; SELECT @held;",
            transaction);
        locking.Parameters.AddWithValue("@resource", $"Scry.Disclosure.Record.{sql.Schema}");
        locking.Parameters.AddWithValue("@wait", (int) wait.TotalMilliseconds);

        // The wait is the lock's to time out of, not the command's.
        locking.CommandTimeout += (int) Math.Ceiling(wait.TotalSeconds);
        return (int) (await locking.ExecuteScalarAsync(cancel))! >= 0;
    }

    // One round, under the lock: the oldest of what is waiting, into the tables the record is read
    // from and out of the outbox. Returns how many batches the round took.
    async Task<int> Move(SqlConnection connection, SqlTransaction transaction, Cancel cancel)
    {
        var taken = await Take(connection, transaction, cancel);
        if (taken.Count == 0)
        {
            return 0;
        }

        var unseen = await Unseen(connection, transaction, taken, cancel);
        if (unseen.Count > 0)
        {
            await Record(connection, transaction, unseen, cancel);
        }

        // Exactly the rows that were read, by position. A batch committed a moment later can hold a
        // lower position than one taken here, so "everything up to the last" would remove a batch
        // nobody had moved.
        await using var remove = Command(
            connection,
            $"""
             DELETE o FROM {sql.Outbox} o JOIN #batch b ON b.[Position] = o.[Position] WHERE b.[Readable] = 1;
             DROP TABLE #batch;
             """,
            transaction);
        await remove.ExecuteNonQueryAsync(cancel);
        return taken.Count;
    }

    // What a round adds to the record: each batch into the segment it belongs in, and a link of the
    // chain for each.
    async Task Record(
        SqlConnection connection,
        SqlTransaction transaction,
        List<(ScryDisclosureBatch Batch, long Position, DateTimeOffset Accepted)> unseen,
        Cancel cancel)
    {
        // Rolled on, where it is due, before anything of the round is written: like a log file, the
        // record rolls when something is about to be added to it, and not on a timer of its own.
        var segments = await Registry(connection, transaction, cancel);
        Agrees(segments);
        var now = options.Clock.GetUtcNow();

        // By the record's settings, which this node writes its own into the first time it moves it.
        // A host that only reads starts no segment, whatever moves the record on there.
        var retention = await Settle(connection, transaction, cancel);
        if (!ReadsOnly &&
            await Due(connection, transaction, Current(segments), now, retention, cancel))
        {
            await Roll(connection, transaction, segments, now, cancel);
            segments = await Registry(connection, transaction, cancel);
        }

        var current = Current(segments).Number;
        var homes = await Homes(connection, transaction, segments, cancel);
        var staged = new SortedDictionary<int, DisclosureStaging>();
        var links = new DataTable();
        links.Columns.Add("Link", typeof(long));
        links.Columns.Add("Segment", typeof(int));
        links.Columns.Add("Version", typeof(byte));
        links.Columns.Add("Digest", typeof(byte[]));
        links.Columns.Add("Hash", typeof(byte[]));

        // Each link covering the one before, in the order the batches are moved on: so the chain is
        // written under the same lock that makes this the only mover. Numbered by the chain itself
        // rather than by where a batch sat in the outbox, since two batches accepted at once can
        // commit in either order and the later-numbered one be moved first.
        var (link, hash) = await Head(connection, transaction, cancel);
        foreach (var (batch, position, accepted) in unseen)
        {
            // A later part of an answer goes where the answer began, so that an answer is in one
            // segment and leaves with it. Anything else goes to the segment being written to.
            var segment = current;
            if (batch.Begin is null &&
                homes.TryGetValue(batch.EventId, out var home))
            {
                segment = home;
            }

            if (!staged.TryGetValue(segment, out var staging))
            {
                staged[segment] = staging = new(sql.Segment(segment));
            }

            link++;
            var digest = DisclosureChain.Digest(batch);
            hash = DisclosureChain.Hash(hash, digest);
            links.Rows.Add(link, segment, DisclosureChain.Version, digest, hash);
            staging.Add(batch, link, position, accepted);
        }

        foreach (var staging in staged.Values)
        {
            await staging.Write(connection, transaction, options.Content, cancel);
        }

        await DisclosureStaging.Apply(
            connection,
            transaction,
            links,
            "#link",
            "[Link] bigint NOT NULL, [Segment] int NOT NULL, [Version] tinyint NOT NULL, [Digest] binary(32) NOT NULL, [Hash] binary(32) NOT NULL",
            $"""
             INSERT INTO {sql.Chain} ([Link], [Segment], [Version], [Digest], [Hash])
             SELECT s.[Link], s.[Segment], s.[Version], s.[Digest], s.[Hash] FROM #link s;
             """,
            cancel);
    }

    // Which segment each answer the round adds to was begun in, for the answers begun before it and
    // still in a segment that is being kept.
    async Task<Dictionary<Guid, int>> Homes(SqlConnection connection, SqlTransaction transaction, List<ScryDisclosureSegment> segments, Cancel cancel)
    {
        var homes = new Dictionary<Guid, int>();
        var kept = segments.Where(_ => _ is {Retired: null, Retiring: false}).Select(_ => _.Number).ToHashSet();
        await using var command = Command(
            connection,
            $"SELECT DISTINCT b.[EventId], e.[Segment] FROM #batch b JOIN {sql.Event} e ON e.[EventId] = b.[EventId] WHERE b.[Readable] = 1;",
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            if (kept.Contains(reader.GetInt32(1)))
            {
                homes[reader.GetGuid(0)] = reader.GetInt32(1);
            }
        }

        return homes;
    }

    // The oldest of what is waiting, in the order it was accepted.
    async Task<List<(long Position, DateTimeOffset Accepted, Guid EventId, int Sequence, byte[] Batch)>> Take(SqlConnection connection, SqlTransaction transaction, Cancel cancel)
    {
        var taken = new List<(long, DateTimeOffset, Guid, int, byte[])>();
        await using var command = Command(
            connection,
            $"SELECT TOP (@count) [Position], [AcceptedAt], [EventId], [Sequence], [Batch] FROM {sql.Outbox} WHERE [Unreadable] = 0 ORDER BY [Position];",
            transaction);
        command.Parameters.AddWithValue("@count", options.DrainBatches);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            taken.Add((reader.GetInt64(0), reader.GetDateTimeOffset(1), reader.GetGuid(2), reader.GetInt32(3), (byte[]) reader[4]));
        }

        return taken;
    }

    // Those of the taken batches the record has not already had, read back as batches, each with
    // where and when it was accepted. One that cannot be read is set aside rather than left at the head of the outbox, where
    // it would stop everything behind it for good. Leaves #batch behind it, naming what was taken.
    async Task<List<(ScryDisclosureBatch Batch, long Position, DateTimeOffset Accepted)>> Unseen(
        SqlConnection connection,
        SqlTransaction transaction,
        List<(long Position, DateTimeOffset Accepted, Guid EventId, int Sequence, byte[] Batch)> taken,
        Cancel cancel)
    {
        var read = new Dictionary<long, ScryDisclosureBatch>(taken.Count);
        var marks = new DataTable();
        marks.Columns.Add("Position", typeof(long));
        marks.Columns.Add("EventId", typeof(Guid));
        marks.Columns.Add("Sequence", typeof(int));
        marks.Columns.Add("AcceptedAt", typeof(DateTimeOffset));
        marks.Columns.Add("Readable", typeof(bool));
        foreach (var (position, accepted, id, sequence, bytes) in taken)
        {
            if (Read(bytes, id, sequence) is { } batch)
            {
                read[position] = batch;
            }

            marks.Rows.Add(position, id, sequence, accepted, read.ContainsKey(position));
        }

        await using (var create = Command(connection, "CREATE TABLE #batch ([Position] bigint NOT NULL PRIMARY KEY, [EventId] uniqueidentifier NOT NULL, [Sequence] int NOT NULL, [AcceptedAt] datetimeoffset(7) NOT NULL, [Readable] bit NOT NULL);", transaction))
        {
            await create.ExecuteNonQueryAsync(cancel);
        }

        using (var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) {DestinationTableName = "#batch"})
        {
            await copy.WriteToServerAsync(marks, cancel);
        }

        // Asked and then marked in two commands, each read to its end: a reader closed before its
        // last statement has run does not say that the statement failed. Asked of every segment
        // there is: a batch that arrives a second time is one the record has had wherever it went.
        await using (var known = Command(
                         connection,
                         $"SELECT b.[Position] FROM #batch b WHERE b.[Readable] = 1 AND EXISTS (SELECT 1 FROM {sql.Batch} x WHERE x.[EventId] = b.[EventId] AND x.[Sequence] = b.[Sequence]);",
                         transaction))
        await using (var reader = await known.ExecuteReaderAsync(cancel))
        {
            while (await reader.ReadAsync(cancel))
            {
                read.Remove(reader.GetInt64(0));
            }
        }

        await using (var mark = Command(
                         connection,
                         $"UPDATE o SET o.[Unreadable] = 1 FROM {sql.Outbox} o JOIN #batch b ON b.[Position] = o.[Position] WHERE b.[Readable] = 0;",
                         transaction))
        {
            await mark.ExecuteNonQueryAsync(cancel);
        }

        // In the order they were accepted, which is the order the chain links them in.
        var when = taken.ToDictionary(_ => _.Position, _ => _.Accepted);
        return [.. read.OrderBy(_ => _.Key).Select(_ => (_.Value, _.Key, when[_.Key]))];
    }

    // A batch read back from what the outbox holds, or null for one that will not read — or that
    // reads as some other batch than the row says it is, which is no more to be trusted.
    static ScryDisclosureBatch? Read(byte[] bytes, Guid id, int sequence)
    {
        try
        {
            var batch = ScryDisclosureBatch.Deserialize(bytes);
            if (batch.EventId == id &&
                batch.Sequence == sequence)
            {
                return batch;
            }

            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Where the chain ends: the number of its last link and that link's hash — nought and thirty-two
    // zeros before there is one.
    async Task<(long Link, byte[] Hash)> Head(SqlConnection connection, SqlTransaction transaction, Cancel cancel)
    {
        await using var command = Command(connection, $"SELECT TOP (1) [Link], [Hash] FROM {sql.Chain} ORDER BY [Link] DESC;", transaction);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        if (await reader.ReadAsync(cancel))
        {
            return (reader.GetInt64(0), (byte[]) reader[1]);
        }

        return (0, new byte[ScryDisclosureAddress.Size]);
    }

    /// <inheritdoc />
    public void Dispose() =>
        DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Stops moving batches on in the background. What is in the outbox stays there, and is moved on
    /// by the next node to start. The store goes on accepting: an answer given while the host stops
    /// is still recorded.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (starting)
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            running = loop;
        }

        if (stopping is not null)
        {
            await stopping.CancelAsync();
        }

        if (running is not null)
        {
            await running;
        }

        stopping?.Dispose();
    }
}
