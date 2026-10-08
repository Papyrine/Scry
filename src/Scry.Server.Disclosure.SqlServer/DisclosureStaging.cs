/// <summary>
/// The rows a round of moving batches on is about to add, gathered from every batch of the round and
/// written in a few set-based statements rather than a statement per row. Each table's rows go to a
/// temporary table by bulk copy, and from there into the record with one <c>INSERT … SELECT</c>.
/// </summary>
/// <remarks>
/// Every insert leaves out what is already there. A batch is only ever moved once, so for most tables
/// that never applies — but a mover that stops on a duplicate stops for good, with everything behind
/// it, and the guard costs an index seek. For shapes and content it is the point: both are kept by
/// address, and most of what a batch carries of either is already held.
/// </remarks>
sealed class DisclosureStaging(DisclosureSql sql)
{
    DataTable events = Table(
        ("EventId", typeof(Guid)), ("Sequence", typeof(int)), ("At", typeof(DateTimeOffset)), ("Caller", typeof(string)),
        ("CallerHash", typeof(byte[])), ("Kind", typeof(byte)), ("Subscribed", typeof(bool)), ("Delivery", typeof(byte)),
        ("Source", typeof(string)), ("Request", typeof(byte[])), ("Shape", typeof(byte[])), ("Sensitive", typeof(bool)),
        ("Stamp", typeof(string)), ("Correlation", typeof(string)), ("Node", typeof(string)), ("ContentType", typeof(string)));
    DataTable closes = Table(
        ("EventId", typeof(Guid)), ("Sequence", typeof(int)), ("Outcome", typeof(byte)), ("Units", typeof(int)),
        ("At", typeof(DateTimeOffset)), ("Response", typeof(byte[])));
    DataTable units = Table(
        ("EventId", typeof(Guid)), ("Ordinal", typeof(int)), ("Sequence", typeof(int)), ("Content", typeof(byte[])));
    DataTable entities = Table(
        ("EventId", typeof(Guid)), ("Ordinal", typeof(int)), ("Slot", typeof(int)), ("Sequence", typeof(int)),
        ("RowHash", typeof(byte[])), ("Source", typeof(string)), ("RowKey", typeof(string)), ("Via", typeof(string)));
    DataTable manifests = Table(
        ("Manifest", typeof(byte[])), ("Units", typeof(int)));
    DataTable manifestUnits = Table(
        ("Manifest", typeof(byte[])), ("Ordinal", typeof(int)), ("Content", typeof(byte[])));
    DataTable manifestEntities = Table(
        ("Manifest", typeof(byte[])), ("Ordinal", typeof(int)), ("Slot", typeof(int)), ("RowHash", typeof(byte[])),
        ("Source", typeof(string)), ("RowKey", typeof(string)), ("Via", typeof(string)));
    DataTable answers = Table(
        ("EventId", typeof(Guid)), ("Sequence", typeof(int)), ("Manifest", typeof(byte[])));

    // The lists this round has already staged, so that one named by several of its answers is staged once.
    HashSet<string> listed = new(StringComparer.Ordinal);

    DataTable fields = Table(
        ("Shape", typeof(byte[])), ("Position", typeof(int)), ("FieldHash", typeof(byte[])), ("FieldUse", typeof(byte)),
        ("Source", typeof(string)), ("Member", typeof(string)), ("Sensitive", typeof(bool)));
    DataTable contents = Table(
        ("Address", typeof(byte[])), ("Kind", typeof(byte)), ("Length", typeof(int)));
    DataTable reviews = Table(
        ("ReviewId", typeof(Guid)), ("At", typeof(DateTimeOffset)), ("Reviewer", typeof(string)), ("Question", typeof(byte)),
        ("Parameters", typeof(byte[])), ("Results", typeof(int)), ("Node", typeof(string)));
    DataTable reviewed = Table(
        ("ReviewId", typeof(Guid)), ("EventId", typeof(Guid)));
    DataTable links = Table(
        ("Link", typeof(long)), ("EventId", typeof(Guid)), ("Sequence", typeof(int)), ("Digest", typeof(byte[])), ("Hash", typeof(byte[])));
    DataTable sources = Table(
        ("SourceHash", typeof(byte[])), ("Source", typeof(string)), ("Keyed", typeof(bool)));

    // The sources this round has anything of, and whether a row of each was named by key. Kept as a
    // summary because nothing else can say it without reading the largest tables end to end.
    Dictionary<string, bool> seen = new(StringComparer.Ordinal);

    // What this round has already staged of the two things kept by address, so that a shape or a
    // piece of content carried by several of its batches is staged once.
    HashSet<ScryDisclosureAddress> shapes = [];
    HashSet<ScryDisclosureAddress> named = [];

    // The bytes this round carries, by address. Held here until the record has said which of them
    // it lacks, since most of what a round carries it already holds.
    Dictionary<ScryDisclosureAddress, ReadOnlyMemory<byte>> carried = [];

    static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns)
        {
            table.Columns.Add(name, type);
        }

        return table;
    }

    static object Value(object? value) =>
        value ?? DBNull.Value;

    public void Add(ScryDisclosureBatch batch)
    {
        if (batch.Begin is {Source.Length: > 0} sourced)
        {
            seen.TryAdd(sourced.Source, false);
        }

        if (batch.Begin is { } begin)
        {
            events.Rows.Add(
                begin.Id,
                batch.Sequence,
                begin.At,
                Value(begin.Caller),
                Value(DisclosureSql.CallerHash(begin.Caller)),
                (byte) begin.Kind,
                begin.Subscribed,
                (byte) begin.Delivery,
                begin.Source,
                Value(begin.Request?.ToArray()),
                Value(begin.Shape?.ToArray()),
                begin.Sensitive,
                Value(begin.Stamp),
                Value(begin.Correlation),
                Value(begin.Node),
                Value(begin.ContentType));
        }

        if (batch.Shape is { } shape &&
            shapes.Add(shape.Address))
        {
            var address = shape.Address.ToArray();
            for (var index = 0; index < shape.Fields.Count; index++)
            {
                var field = shape.Fields[index];
                fields.Rows.Add(
                    address,
                    index,
                    DisclosureSql.FieldHash(field.Source, field.Member),
                    (byte) field.Use,
                    field.Source,
                    field.Member,
                    field.Sensitive);
            }
        }

        // An answer that arrived whole in one batch is kept as the list of units it was made of,
        // which is kept once however many answers were made of it. One that arrived in pieces — a
        // stream, a response sent as it was written — or was not handed on whole keeps its own rows:
        // the list is not known until its end, and its end may not be the whole of it.
        if (DisclosureManifest.Whole(batch))
        {
            var manifest = DisclosureManifest.Address(batch);
            answers.Rows.Add(batch.EventId, batch.Sequence, manifest);
            var fresh = listed.Add(Convert.ToHexString(manifest));
            if (fresh)
            {
                manifests.Rows.Add(manifest, batch.Units.Count);
                foreach (var unit in batch.Units)
                {
                    manifestUnits.Rows.Add(manifest, unit.Ordinal, unit.Content.ToArray());
                }
            }

            foreach (var entity in batch.Entities)
            {
                seen[entity.Source] = true;
                if (fresh)
                {
                    manifestEntities.Rows.Add(
                        manifest,
                        entity.Ordinal,
                        entity.Slot,
                        DisclosureSql.RowHash(entity.Source, entity.Key),
                        entity.Source,
                        entity.Key,
                        entity.Via);
                }
            }
        }
        else
        {
            foreach (var unit in batch.Units)
            {
                units.Rows.Add(batch.EventId, unit.Ordinal, batch.Sequence, unit.Content.ToArray());
            }

            foreach (var entity in batch.Entities)
            {
                seen[entity.Source] = true;
                entities.Rows.Add(
                    batch.EventId,
                    entity.Ordinal,
                    entity.Slot,
                    batch.Sequence,
                    DisclosureSql.RowHash(entity.Source, entity.Key),
                    entity.Source,
                    entity.Key,
                    entity.Via);
            }
        }

        foreach (var content in batch.Contents)
        {
            // A piece recorded by digest alone gives way to the same piece arriving with its bytes
            // later in the round, since holding it is the more that can be said of it.
            if (content.Held)
            {
                carried.TryAdd(content.Address, content.Bytes);
            }

            if (named.Add(content.Address))
            {
                contents.Rows.Add(content.Address.ToArray(), (byte) content.Kind, content.Length);
            }
        }

        if (batch.Close is { } close)
        {
            closes.Rows.Add(batch.EventId, batch.Sequence, (byte) close.Outcome, close.Units, close.At, Value(close.Response?.ToArray()));
        }

        if (batch.Review is { } review)
        {
            reviews.Rows.Add(
                review.Id,
                review.At,
                Value(review.Reviewer),
                (byte) review.Question,
                Value(review.Parameters?.ToArray()),
                review.Results,
                Value(review.Node));
            foreach (var shown in review.Events.Distinct())
            {
                reviewed.Rows.Add(review.Id, shown);
            }
        }
    }

    /// <summary>
    /// Adds a batch to the chain as its <paramref name="link"/>th: a digest of what the record keeps
    /// of it, and a hash over that and the link before. Returns the hash, which the next link covers.
    /// </summary>
    public byte[] Link(ScryDisclosureBatch batch, long link, byte[] previous)
    {
        var digest = DisclosureChain.Digest(batch);
        var hash = DisclosureChain.Hash(previous, digest);
        links.Rows.Add(link, batch.EventId, batch.Sequence, digest, hash);
        return hash;
    }

    public async Task Write(SqlConnection connection, SqlTransaction transaction, IScryDisclosureBlobStore? elsewhere, Cancel cancel)
    {
        foreach (var (source, keyed) in seen)
        {
            sources.Rows.Add(DisclosureSql.SourceHash(source), source, keyed);
        }

        await Apply(
            connection,
            transaction,
            sources,
            "#source",
            "[SourceHash] binary(32) NOT NULL, [Source] nvarchar(max) NOT NULL, [Keyed] bit NOT NULL",
            $"""
             UPDATE t SET t.[Keyed] = 1 FROM {sql.Source} t JOIN #source s ON s.[SourceHash] = t.[SourceHash] WHERE t.[Keyed] = 0 AND s.[Keyed] = 1;
             INSERT INTO {sql.Source} ([SourceHash], [Source], [Keyed])
             SELECT s.[SourceHash], s.[Source], s.[Keyed] FROM #source s WHERE NOT EXISTS (SELECT 1 FROM {sql.Source} t WHERE t.[SourceHash] = s.[SourceHash]);
             """,
            cancel);

        var e = sql.Event;
        await Apply(
            connection,
            transaction,
            events,
            "#event",
            "[EventId] uniqueidentifier NOT NULL, [Sequence] int NOT NULL, [At] datetimeoffset(7) NOT NULL, [Caller] nvarchar(max) NULL, [CallerHash] binary(32) NULL, [Kind] tinyint NOT NULL, [Subscribed] bit NOT NULL, [Delivery] tinyint NOT NULL, [Source] nvarchar(max) NOT NULL, [Request] binary(32) NULL, [Shape] binary(32) NULL, [Sensitive] bit NOT NULL, [Stamp] nvarchar(max) NULL, [Correlation] nvarchar(max) NULL, [Node] nvarchar(max) NULL, [ContentType] nvarchar(max) NULL",
            $"""
             INSERT INTO {e} ([EventId], [Sequence], [At], [Caller], [CallerHash], [Kind], [Subscribed], [Delivery], [Source], [Request], [Shape], [Sensitive], [Stamp], [Correlation], [Node], [ContentType])
             SELECT s.[EventId], s.[Sequence], s.[At], s.[Caller], s.[CallerHash], s.[Kind], s.[Subscribed], s.[Delivery], s.[Source], s.[Request], s.[Shape], s.[Sensitive], s.[Stamp], s.[Correlation], s.[Node], s.[ContentType]
             FROM #event s WHERE NOT EXISTS (SELECT 1 FROM {e} x WHERE x.[EventId] = s.[EventId]);
             """,
            cancel);
        await Apply(
            connection,
            transaction,
            closes,
            "#close",
            "[EventId] uniqueidentifier NOT NULL, [Sequence] int NOT NULL, [Outcome] tinyint NOT NULL, [Units] int NOT NULL, [At] datetimeoffset(7) NOT NULL, [Response] binary(32) NULL",
            $"""
             INSERT INTO {sql.Close} ([EventId], [Sequence], [Outcome], [Units], [At], [Response])
             SELECT s.[EventId], s.[Sequence], s.[Outcome], s.[Units], s.[At], s.[Response]
             FROM #close s WHERE NOT EXISTS (SELECT 1 FROM {sql.Close} x WHERE x.[EventId] = s.[EventId] AND x.[Sequence] = s.[Sequence]);
             """,
            cancel);
        await Apply(
            connection,
            transaction,
            units,
            "#unit",
            "[EventId] uniqueidentifier NOT NULL, [Ordinal] int NOT NULL, [Sequence] int NOT NULL, [Content] binary(32) NOT NULL",
            $"""
             INSERT INTO {sql.Unit} ([EventId], [Ordinal], [Sequence], [Content])
             SELECT s.[EventId], s.[Ordinal], s.[Sequence], s.[Content]
             FROM #unit s WHERE NOT EXISTS (SELECT 1 FROM {sql.Unit} x WHERE x.[EventId] = s.[EventId] AND x.[Ordinal] = s.[Ordinal]);
             """,
            cancel);
        await Apply(
            connection,
            transaction,
            entities,
            "#entity",
            "[EventId] uniqueidentifier NOT NULL, [Ordinal] int NOT NULL, [Slot] int NOT NULL, [Sequence] int NOT NULL, [RowHash] binary(32) NOT NULL, [Source] nvarchar(max) NOT NULL, [RowKey] nvarchar(max) NOT NULL, [Via] nvarchar(max) NOT NULL",
            $"""
             INSERT INTO {sql.Entity} ([EventId], [Ordinal], [Slot], [Sequence], [RowHash], [Source], [RowKey], [Via])
             SELECT s.[EventId], s.[Ordinal], s.[Slot], s.[Sequence], s.[RowHash], s.[Source], s.[RowKey], s.[Via]
             FROM #entity s WHERE NOT EXISTS (SELECT 1 FROM {sql.Entity} x WHERE x.[EventId] = s.[EventId] AND x.[Ordinal] = s.[Ordinal] AND x.[Slot] = s.[Slot]);
             """,
            cancel);

        await Listed(connection, transaction, cancel);

        // The fields before the shape they belong to, since it is the shape's being absent that
        // says its fields are.
        await Apply(
            connection,
            transaction,
            fields,
            "#field",
            "[Shape] binary(32) NOT NULL, [Position] int NOT NULL, [FieldHash] binary(32) NOT NULL, [FieldUse] tinyint NOT NULL, [Source] nvarchar(max) NOT NULL, [Member] nvarchar(max) NOT NULL, [Sensitive] bit NOT NULL",
            $"""
             INSERT INTO {sql.Field} ([Shape], [Position], [FieldHash], [FieldUse], [Source], [Member], [Sensitive])
             SELECT s.[Shape], s.[Position], s.[FieldHash], s.[FieldUse], s.[Source], s.[Member], s.[Sensitive]
             FROM #field s WHERE NOT EXISTS (SELECT 1 FROM {sql.Shape} x WHERE x.[Address] = s.[Shape]);
             INSERT INTO {sql.Shape} ([Address])
             SELECT DISTINCT s.[Shape] FROM #field s WHERE NOT EXISTS (SELECT 1 FROM {sql.Shape} x WHERE x.[Address] = s.[Shape]);
             """,
            cancel);
        await Keep(connection, transaction, elsewhere, cancel);
        await Apply(
            connection,
            transaction,
            reviews,
            "#review",
            "[ReviewId] uniqueidentifier NOT NULL, [At] datetimeoffset(7) NOT NULL, [Reviewer] nvarchar(max) NULL, [Question] tinyint NOT NULL, [Parameters] binary(32) NULL, [Results] int NOT NULL, [Node] nvarchar(max) NULL",
            $"""
             INSERT INTO {sql.Review} ([ReviewId], [At], [Reviewer], [Question], [Parameters], [Results], [Node])
             SELECT s.[ReviewId], s.[At], s.[Reviewer], s.[Question], s.[Parameters], s.[Results], s.[Node]
             FROM #review s WHERE NOT EXISTS (SELECT 1 FROM {sql.Review} x WHERE x.[ReviewId] = s.[ReviewId]);
             """,
            cancel);
        await Apply(
            connection,
            transaction,
            reviewed,
            "#reviewed",
            "[ReviewId] uniqueidentifier NOT NULL, [EventId] uniqueidentifier NOT NULL",
            $"""
             INSERT INTO {sql.ReviewEvent} ([ReviewId], [EventId])
             SELECT s.[ReviewId], s.[EventId]
             FROM #reviewed s WHERE NOT EXISTS (SELECT 1 FROM {sql.ReviewEvent} x WHERE x.[ReviewId] = s.[ReviewId] AND x.[EventId] = s.[EventId]);
             """,
            cancel);
        await Apply(
            connection,
            transaction,
            links,
            "#link",
            "[Link] bigint NOT NULL, [EventId] uniqueidentifier NOT NULL, [Sequence] int NOT NULL, [Digest] binary(32) NOT NULL, [Hash] binary(32) NOT NULL",
            $"""
             INSERT INTO {sql.Chain} ([Link], [EventId], [Sequence], [Digest], [Hash])
             SELECT s.[Link], s.[EventId], s.[Sequence], s.[Digest], s.[Hash] FROM #link s;
             """,
            cancel);
    }

    // The content, in two steps: what the round names goes up by address alone, the record says which
    // of it it does not hold the bytes of, and only those bytes follow. A row sent a thousand times is
    // the same content under the same address, so the bytes of most of what a round carries would
    // otherwise cross to the database only to be found already there.
    async Task Keep(SqlConnection connection, SqlTransaction transaction, IScryDisclosureBlobStore? elsewhere, Cancel cancel)
    {
        if (contents.Rows.Count == 0)
        {
            return;
        }

        await Stage(connection, transaction, contents, "#content", "[Address] binary(32) NOT NULL PRIMARY KEY, [Kind] tinyint NOT NULL, [Length] int NOT NULL", cancel);

        // Held nowhere: never seen, seen by digest alone, or erased since. Erased and now sent again
        // is a new disclosure, and is kept as one.
        var lacking = new List<ScryDisclosureAddress>();
        await using (var lacked = new SqlCommand(
                         $"SELECT s.[Address] FROM #content s WHERE NOT EXISTS (SELECT 1 FROM {sql.Content} c WHERE c.[Address] = s.[Address] AND (c.[Bytes] IS NOT NULL OR c.[Elsewhere] = 1));",
                         connection,
                         transaction))
        await using (var reader = await lacked.ExecuteReaderAsync(cancel))
        {
            while (await reader.ReadAsync(cancel))
            {
                lacking.Add(ScryDisclosureAddress.From((byte[]) reader[0]));
            }
        }

        var arriving = Table(("Address", typeof(byte[])), ("Bytes", typeof(byte[])), ("Elsewhere", typeof(bool)));
        foreach (var address in lacking)
        {
            if (!carried.TryGetValue(address, out var bytes))
            {
                continue;
            }

            if (elsewhere is null)
            {
                arriving.Rows.Add(address.ToArray(), Exact(bytes), false);
                continue;
            }

            // Kept elsewhere before the record that names it is committed: content with no record is
            // only clutter, and a record with no content would be a reconstruction that fails.
            await elsewhere.PutAsync(address, bytes, cancel);
            arriving.Rows.Add(address.ToArray(), DBNull.Value, true);
        }

        await Stage(connection, transaction, arriving, "#bytes", "[Address] binary(32) NOT NULL PRIMARY KEY, [Bytes] varbinary(max) NULL, [Elsewhere] bit NOT NULL", cancel);
        await Run(
            connection,
            transaction,
            $"""
             UPDATE c SET c.[Bytes] = b.[Bytes], c.[Elsewhere] = b.[Elsewhere], c.[Erased] = 0
             FROM {sql.Content} c JOIN #bytes b ON b.[Address] = c.[Address];
             INSERT INTO {sql.Content} ([Address], [Kind], [Length], [Bytes], [Elsewhere], [Erased])
             SELECT s.[Address], s.[Kind], s.[Length], b.[Bytes], ISNULL(b.[Elsewhere], 0), 0
             FROM #content s LEFT JOIN #bytes b ON b.[Address] = s.[Address]
             WHERE NOT EXISTS (SELECT 1 FROM {sql.Content} c WHERE c.[Address] = s.[Address]);
             DROP TABLE #content;
             DROP TABLE #bytes;
             """,
            cancel);
    }

    // The bytes as an array of exactly their length, which is what a bulk copy takes: the array they
    // already are where they fill one, and a copy where they are a part of something larger.
    static byte[] Exact(ReadOnlyMemory<byte> bytes)
    {
        if (MemoryMarshal.TryGetArray(bytes, out var segment) &&
            segment is {Array: { } array, Offset: 0} &&
            segment.Count == array.Length)
        {
            return array;
        }

        return bytes.ToArray();
    }

    // One table's rows: to a temporary table by bulk copy, and from there into the record.
    // The lists the round's whole answers were made of, and which answer names which. A list's
    // rows are written only where the record has no such list, which is decided once for all three
    // of its tables: a list is whole or it is not there.
    async Task Listed(SqlConnection connection, SqlTransaction transaction, Cancel cancel)
    {
        if (answers.Rows.Count == 0)
        {
            return;
        }

        await Stage(connection, transaction, manifests, "#manifest", "[Manifest] binary(32) NOT NULL PRIMARY KEY, [Units] int NOT NULL", cancel);
        await Stage(connection, transaction, manifestUnits, "#munit", "[Manifest] binary(32) NOT NULL, [Ordinal] int NOT NULL, [Content] binary(32) NOT NULL", cancel);
        await Stage(
            connection,
            transaction,
            manifestEntities,
            "#mentity",
            "[Manifest] binary(32) NOT NULL, [Ordinal] int NOT NULL, [Slot] int NOT NULL, [RowHash] binary(32) NOT NULL, [Source] nvarchar(max) NOT NULL, [RowKey] nvarchar(max) NOT NULL, [Via] nvarchar(max) NOT NULL",
            cancel);
        await Stage(connection, transaction, answers, "#answer", "[EventId] uniqueidentifier NOT NULL, [Sequence] int NOT NULL, [Manifest] binary(32) NOT NULL", cancel);
        await Run(
            connection,
            transaction,
            $"""
             SELECT s.[Manifest], s.[Units] INTO #fresh FROM #manifest s
             WHERE NOT EXISTS (SELECT 1 FROM {sql.Manifest} x WHERE x.[Manifest] = s.[Manifest]);
             INSERT INTO {sql.ManifestUnit} ([Manifest], [Ordinal], [Content])
             SELECT s.[Manifest], s.[Ordinal], s.[Content] FROM #munit s JOIN #fresh f ON f.[Manifest] = s.[Manifest];
             INSERT INTO {sql.ManifestEntity} ([Manifest], [Ordinal], [Slot], [RowHash], [Source], [RowKey], [Via])
             SELECT s.[Manifest], s.[Ordinal], s.[Slot], s.[RowHash], s.[Source], s.[RowKey], s.[Via] FROM #mentity s JOIN #fresh f ON f.[Manifest] = s.[Manifest];
             INSERT INTO {sql.Manifest} ([Manifest], [Units]) SELECT f.[Manifest], f.[Units] FROM #fresh f;
             INSERT INTO {sql.Answer} ([EventId], [Sequence], [Manifest])
             SELECT s.[EventId], s.[Sequence], s.[Manifest] FROM #answer s
             WHERE NOT EXISTS (SELECT 1 FROM {sql.Answer} x WHERE x.[EventId] = s.[EventId]);
             DROP TABLE #fresh, #manifest, #munit, #mentity, #answer;
             """,
            cancel);
    }

    static async Task Apply(
        SqlConnection connection,
        SqlTransaction transaction,
        DataTable rows,
        string temporary,
        string columns,
        string insert,
        Cancel cancel)
    {
        if (rows.Rows.Count == 0)
        {
            return;
        }

        await Stage(connection, transaction, rows, temporary, columns, cancel);
        await Run(connection, transaction, $"{insert}\nDROP TABLE {temporary};", cancel);
    }

    static async Task Stage(
        SqlConnection connection,
        SqlTransaction transaction,
        DataTable rows,
        string temporary,
        string columns,
        Cancel cancel)
    {
        await Run(connection, transaction, $"CREATE TABLE {temporary} ({columns});", cancel);
        if (rows.Rows.Count == 0)
        {
            return;
        }

        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
        copy.DestinationTableName = temporary;
        copy.BulkCopyTimeout = 0;
        await copy.WriteToServerAsync(rows, cancel);
    }

    // Not held to an answer's wait: this is the mover, a step behind every answer, and a round of it
    // is as long as what the round carries.
    static async Task Run(SqlConnection connection, SqlTransaction transaction, string statements, Cancel cancel)
    {
        await using var command = new SqlCommand(statements, connection, transaction);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancel);
    }
}

/// <summary>
/// The hash chain's arithmetic, shared by what writes a link and what checks one.
/// </summary>
static class DisclosureChain
{
    /// <summary>
    /// A digest of what the record keeps of a batch: its event, its units and the rows they were
    /// read from, its shape, its close, its review. Not its content, which is kept apart, can be
    /// erased, and is covered already — a unit's address is a hash of what it carried.
    /// </summary>
    /// <remarks>
    /// Taken over the batch as every sink writes it, with the content and the shape's members left out
    /// — the event names its shape by an address that is a hash of them — and everything that has no
    /// order of its own put in one. So the same digest can be made again from the tables alone.
    /// </remarks>
    public static byte[] Digest(ScryDisclosureBatch batch)
    {
        var review = batch.Review;
        if (review is not null)
        {
            review = review with
            {
                Events = [.. review.Events.Distinct().Order()]
            };
        }

        var kept = new ScryDisclosureBatch
        {
            EventId = batch.EventId,
            Sequence = batch.Sequence,
            Begin = batch.Begin,
            Units = [.. batch.Units.OrderBy(_ => _.Ordinal)],
            Entities = [.. batch.Entities.OrderBy(_ => _.Ordinal).ThenBy(_ => _.Slot)],
            Close = batch.Close,
            Review = review
        };
        var bytes = new ArrayBufferWriter<byte>();
        kept.Serialize(bytes);
        return SHA256.HashData(bytes.WrittenSpan);
    }

    /// <summary>A link's hash: over the link before it and its own digest.</summary>
    public static byte[] Hash(byte[] previous, byte[] digest)
    {
        Span<byte> joined = stackalloc byte[previous.Length + digest.Length];
        previous.CopyTo(joined);
        digest.CopyTo(joined[previous.Length..]);
        return SHA256.HashData(joined);
    }
}
