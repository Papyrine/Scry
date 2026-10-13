/// <summary>
/// The statements the store runs, built once over the schema it was given. Everything a caller or a
/// row can influence travels as a parameter; the schema is the one name spliced into the text, so it
/// is held to what an identifier may be before any statement is made from it.
/// </summary>
/// <remarks>
/// The record is kept in segments, each a schema of its own holding every table of it, so that the
/// oldest can be dropped whole when its time is up. They are plain tables. What says that none of
/// them was changed is the chain: a hash for every batch, each covering the one before, kept in a
/// table of the store's own schema that is only ever added to and is made an append-only ledger
/// table where the server has them. That table is small enough to keep for good, which a ledger
/// table has to be: nothing but dropping the database takes a row out of one.
/// <para>
/// The store's schema also holds a view of each table over every segment, and those are what the
/// record is read through, so that a question is asked of the whole of it.
/// </para>
/// <para>
/// Nothing a caller chose is ever an index key. A caller's name, a source, a member and a row's key
/// are kept as text of any length and found by a hash of them, so no value is too long to record and
/// none is cut short to fit.
/// </para>
/// <para>
/// Those names are also compared as the characters they are, whatever the database's own collation
/// says of case and accents: a source is the source of that exact name, as it is to the server.
/// </para>
/// </remarks>
sealed class DisclosureSql
{
    // How a segment's schema is told from any other: the store's own, an underscore, six digits.
    const string numbered = "000000";

    DisclosureTables read;

    public DisclosureSql(string schema, bool? ledger)
    {
        if (!IsIdentifier(schema))
        {
            throw new ArgumentException($"{nameof(ScrySqlServerDisclosureOptions)}.{nameof(ScrySqlServerDisclosureOptions.Schema)} must be a plain identifier — letters, digits and underscores, not starting with a digit, and no longer than {128 - numbered.Length - 1} characters — and '{schema}' is not. It is written into every statement the store runs.");
        }

        Schema = schema;
        read = new(schema);
        var s = $"[{schema}]";
        Outbox = $"{s}.[DisclosureOutbox]";
        Chain = $"{s}.[DisclosureChain]";
        Segments = $"{s}.[DisclosureSegment]";

        // A duplicate is not an error: the unique index is made to drop it, so a batch handed over
        // twice is kept once in a single statement and without a race between asking and inserting.
        Accept = $"INSERT INTO {Outbox} ([EventId], [Sequence], [Batch]) VALUES (@event, @sequence, @batch);";
        Create = Creating(ledger);

        // How an event ended is the close written last: an answer accepted whole and then not sent
        // is closed a second time.
        Ended = $"OUTER APPLY (SELECT TOP (1) x.[Outcome], x.[Units], x.[At] AS [ClosedAt], x.[Response] FROM {Close} x WHERE x.[EventId] = e.[EventId] ORDER BY x.[Sequence] DESC) c";
        Columns = "e.[EventId], e.[At], e.[Caller], e.[Kind], e.[Subscribed], e.[Delivery], e.[Source], e.[Request], e.[Shape], e.[Sensitive], e.[Stamp], e.[Correlation], e.[Node], e.[ContentType], c.[Outcome], c.[Units], c.[ClosedAt], c.[Response]";
    }

    public string Schema { get; }

    /// <summary>What was accepted and has not yet been moved on.</summary>
    public string Outbox { get; }

    /// <summary>
    /// The chain: a hash for every batch the record has had, each covering the one before. The one
    /// part of the record kept for good, and a ledger table where the server has them.
    /// </summary>
    public string Chain { get; }

    /// <summary>What has happened to each segment: opened, rolled away from, being retired, retired.</summary>
    public string Segments { get; }

    // What follows is the record as it is read: a view of each table over every segment, each row
    // with the [Segment] it is in.

    public string Batch => read.Batch;

    public string Event => read.Event;

    public string Close => read.Close;

    public string Units => read.Units;

    public string Entities => read.Entities;

    public string Shape => read.Shape;

    public string Field => read.Field;

    public string Content => read.Content;

    public string Review => read.Review;

    public string ReviewEvent => read.ReviewEvent;

    public string Erasure => read.Erasure;

    public string Source => read.Source;

    /// <summary>Accepts one batch: <c>@event</c>, <c>@sequence</c>, <c>@batch</c>.</summary>
    public string Accept { get; }

    /// <summary>Makes whatever of the schema and its tables is not there yet.</summary>
    public string Create { get; }

    /// <summary>Joins an event, aliased <c>e</c>, to how it ended, aliased <c>c</c>.</summary>
    public string Ended { get; }

    /// <summary>The columns of an event and how it ended, in the order they are read back in.</summary>
    public string Columns { get; }

    /// <summary>The tables of one segment.</summary>
    public DisclosureTables Segment(int number) =>
        new($"{Schema}_{number.ToString(numbered, CultureInfo.InvariantCulture)}");

    /// <summary>Makes the views the record is read through again, over these segments.</summary>
    public string Viewing(IEnumerable<int> segments) =>
        read.Viewing([.. segments.Select(_ => (_, Segment(_)))]);

    // Under an application lock, since two nodes starting together would otherwise both find nothing
    // there and both try to make it. Each object is made only where it is missing, so the whole of it
    // can be run again — by a deployment that reruns its scripts, or after a version that adds a table.
    string Creating(bool? ledger)
    {
        // 1 makes the chain's tables ledger tables, 0 never does, and 2 does where the server has
        // them: SQL Server 2022 on, and Azure SQL.
        var mode = ledger switch
        {
            true => 1,
            false => 0,
            null => 2
        };

        var s = $"[{Schema}]";
        var script = new StringBuilder();
        script.Append(
            $"""
             DECLARE @held int;
             EXEC @held = sp_getapplock @Resource = N'Scry.Disclosure.Schema', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 60000;
             IF @held < 0
             BEGIN
                 RAISERROR(N'The disclosure store''s tables are being created by another session, which did not finish.', 16, 1);
                 RETURN;
             END;

             BEGIN TRY
                 DECLARE @ledger nvarchar(100) = N'';
                 IF {mode} = 1 OR ({mode} = 2 AND (CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 16 OR CAST(SERVERPROPERTY('EngineEdition') AS int) IN (5, 8)))
                     SET @ledger = N' WITH (LEDGER = ON (APPEND_ONLY = ON))';

                 IF SCHEMA_ID(N'{Schema}') IS NULL
                     EXEC(N'CREATE SCHEMA {s}');

                 IF OBJECT_ID(N'{s}.[DisclosureOutbox]', N'U') IS NULL
                     CREATE TABLE {s}.[DisclosureOutbox]
                     (
                         [Position] bigint IDENTITY(1, 1) NOT NULL,
                         [EventId] uniqueidentifier NOT NULL,
                         [Sequence] int NOT NULL,
                         [AcceptedAt] datetimeoffset(7) NOT NULL CONSTRAINT [DF_DisclosureOutbox_AcceptedAt] DEFAULT SYSDATETIMEOFFSET(),
                         [Batch] varbinary(max) NOT NULL,
                         [Unreadable] bit NOT NULL CONSTRAINT [DF_DisclosureOutbox_Unreadable] DEFAULT 0,
                         CONSTRAINT [PK_DisclosureOutbox] PRIMARY KEY CLUSTERED ([Position]),
                         CONSTRAINT [UQ_DisclosureOutbox_Batch] UNIQUE ([EventId], [Sequence]) WITH (IGNORE_DUP_KEY = ON)
                     );


             """);

        // A link says nothing of what its batch held: its place, which segment the batch went to,
        // how the digest was made, and the two hashes. So it can outlast the segment, and does.
        Ledger(script, s, "DisclosureChain",
            """
            [Link] bigint NOT NULL CONSTRAINT [PK_DisclosureChain] PRIMARY KEY CLUSTERED,
            [Segment] int NOT NULL,
            [Version] tinyint NOT NULL,
            [Digest] binary(32) NOT NULL,
            [Hash] binary(32) NOT NULL
            """);

        // Added to and never changed, like the chain: a line for each thing that happens to a
        // segment. What a segment held when it was retired is said in counts and a span of time,
        // and in nothing that names a caller or a row.
        Ledger(script, s, "DisclosureSegment",
            """
            [Entry] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_DisclosureSegment] PRIMARY KEY CLUSTERED,
            [Segment] int NOT NULL,
            [What] tinyint NOT NULL,
            [At] datetimeoffset(7) NOT NULL,
            [Link] bigint NULL,
            [Head] binary(32) NULL,
            [Batches] bigint NULL,
            [Events] bigint NULL,
            [Bytes] bigint NULL,
            [From] datetimeoffset(7) NULL,
            [To] datetimeoffset(7) NULL,
            [HandedOn] bit NULL
            """);

        // The first segment, and the views over it. Later segments are made, and the views made
        // again, by whichever node rolls the record on.
        script.AppendLine($"    IF NOT EXISTS (SELECT 1 FROM {Segments})");
        script.AppendLine("    BEGIN");
        script.Append(Segment(1).Creating());
        script.AppendLine($"    INSERT INTO {Segments} ([Segment], [What], [At]) VALUES (1, {(byte) DisclosureSegmentEntry.Opened}, SYSDATETIMEOFFSET());");
        script.AppendLine("    END;");
        script.AppendLine();
        script.AppendLine($"    IF OBJECT_ID(N'{Event}', N'V') IS NULL");
        script.AppendLine("    BEGIN");
        script.Append(Viewing([1]));
        script.AppendLine("    END;");

        script.Append(
            """
            END TRY
            BEGIN CATCH
                EXEC sp_releaseapplock @Resource = N'Scry.Disclosure.Schema', @LockOwner = N'Session';
                THROW;
            END CATCH;

            EXEC sp_releaseapplock @Resource = N'Scry.Disclosure.Schema', @LockOwner = N'Session';
            """);
        return script.ToString();
    }

    // One of the two tables that is only ever added to and is kept for good. Made through EXEC so
    // that whether it is a ledger table can be decided by the server the script is run on.
    static void Ledger(StringBuilder script, string schema, string name, string columns)
    {
        script.AppendLine($"    IF OBJECT_ID(N'{schema}.[{name}]', N'U') IS NULL");
        script.AppendLine($"        EXEC(N'CREATE TABLE {schema}.[{name}] ({string.Join(' ', columns.Split('\n').Select(_ => _.Trim()))})' + @ledger);");
        script.AppendLine();
    }

    // What SQL Server calls a regular identifier, narrowed to ASCII: enough for any schema name
    // somebody would choose, and nothing that needs escaping inside brackets or a string literal.
    static bool IsIdentifier(string name)
    {
        if (name.Length == 0 ||
            name.Length > 128 - numbered.Length - 1 ||
            char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        return name.All(_ => char.IsAsciiLetterOrDigit(_) || _ == '_');
    }

    /// <summary>
    /// What a row is found by: a hash of its source and its key together. A key is text a row chose,
    /// of any length, so it is never itself an index key.
    /// </summary>
    public static byte[] RowHash(string source, string key) =>
        Hash(source, key);

    /// <summary>What a member is found by: a hash of the source it belongs to and its path.</summary>
    public static byte[] FieldHash(string source, string member) =>
        Hash(source, member);

    /// <summary>What a source's summary is found by.</summary>
    public static byte[] SourceHash(string source) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(source));

    /// <summary>What a caller's events are found by, or null for the answers recorded against nobody.</summary>
    public static byte[]? CallerHash(string? caller)
    {
        if (caller is null)
        {
            return null;
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(caller));
    }

    // The two parts apart by a byte neither can contain, so that no two pairs read as one.
    static byte[] Hash(string first, string second)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(first) + 1 + Encoding.UTF8.GetByteCount(second)];
        var written = Encoding.UTF8.GetBytes(first, bytes);
        bytes[written] = 0;
        Encoding.UTF8.GetBytes(second, bytes.AsSpan(written + 1));
        return SHA256.HashData(bytes);
    }
}
