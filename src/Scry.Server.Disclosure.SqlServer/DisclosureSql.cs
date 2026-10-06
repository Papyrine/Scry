/// <summary>
/// The statements the store runs, built once over the schema it was given. Everything a caller or a
/// row can influence travels as a parameter; the schema is the one name spliced into the text, so it
/// is held to what an identifier may be before any statement is made from it.
/// </summary>
/// <remarks>
/// Two kinds of table. The ones a reader asks questions of are only ever added to, and are made as
/// append-only ledger tables where the server has them. The outbox and the content are not: a batch
/// leaves the outbox once it has been moved, and content is what an erasure removes.
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
    public DisclosureSql(string schema, bool? ledger)
    {
        if (!IsIdentifier(schema))
        {
            throw new ArgumentException($"{nameof(ScrySqlServerDisclosureOptions)}.{nameof(ScrySqlServerDisclosureOptions.Schema)} must be a plain identifier — letters, digits and underscores, not starting with a digit — and '{schema}' is not. It is written into every statement the store runs.");
        }

        Schema = schema;
        var s = $"[{schema}]";
        Outbox = $"{s}.[DisclosureOutbox]";
        Batch = $"{s}.[DisclosureBatch]";
        Event = $"{s}.[DisclosureEvent]";
        Close = $"{s}.[DisclosureClose]";
        Unit = $"{s}.[DisclosureUnit]";
        Entity = $"{s}.[DisclosureEntity]";
        Shape = $"{s}.[DisclosureShape]";
        Field = $"{s}.[DisclosureField]";
        Content = $"{s}.[DisclosureContent]";
        Review = $"{s}.[DisclosureReview]";
        ReviewEvent = $"{s}.[DisclosureReviewEvent]";
        Erasure = $"{s}.[DisclosureErasure]";
        Chain = $"{s}.[DisclosureChain]";
        Source = $"{s}.[DisclosureSource]";

        // A duplicate is not an error: the unique index is made to drop it, so a batch handed over
        // twice is kept once in a single statement and without a race between asking and inserting.
        Accept = $"INSERT INTO {Outbox} ([EventId], [Sequence], [Batch]) VALUES (@event, @sequence, @batch);";
        Create = Creating(schema, ledger);

        // How an event ended is the close written last: an answer accepted whole and then not sent
        // is closed a second time.
        Ended = $"OUTER APPLY (SELECT TOP (1) x.[Outcome], x.[Units], x.[At] AS [ClosedAt], x.[Response] FROM {Close} x WHERE x.[EventId] = e.[EventId] ORDER BY x.[Sequence] DESC) c";
        Columns = "e.[EventId], e.[At], e.[Caller], e.[Kind], e.[Subscribed], e.[Delivery], e.[Source], e.[Request], e.[Shape], e.[Sensitive], e.[Stamp], e.[Correlation], e.[Node], e.[ContentType], c.[Outcome], c.[Units], c.[ClosedAt], c.[Response]";
    }

    public string Schema { get; }

    public string Outbox { get; }

    public string Batch { get; }

    public string Event { get; }

    public string Close { get; }

    public string Unit { get; }

    public string Entity { get; }

    public string Shape { get; }

    public string Field { get; }

    public string Content { get; }

    public string Review { get; }

    public string ReviewEvent { get; }

    public string Erasure { get; }

    public string Chain { get; }

    public string Source { get; }

    /// <summary>Accepts one batch: <c>@event</c>, <c>@sequence</c>, <c>@batch</c>.</summary>
    public string Accept { get; }

    /// <summary>Makes whatever of the schema and its tables is not there yet.</summary>
    public string Create { get; }

    /// <summary>Joins an event, aliased <c>e</c>, to how it ended, aliased <c>c</c>.</summary>
    public string Ended { get; }

    /// <summary>The columns of an event and how it ended, in the order they are read back in.</summary>
    public string Columns { get; }

    // Under an application lock, since two nodes starting together would otherwise both find nothing
    // there and both try to make it. Each object is made only where it is missing, so the whole of it
    // can be run again — by a deployment that reruns its scripts, or after a version that adds a table.
    static string Creating(string schema, bool? ledger)
    {
        // 1 makes the append-only tables ledger tables, 0 never does, and 2 does where the server
        // has them: SQL Server 2022 on, and Azure SQL.
        var mode = ledger switch
        {
            true => 1,
            false => 0,
            null => 2
        };

        var s = $"[{schema}]";
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

                 IF SCHEMA_ID(N'{schema}') IS NULL
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

                 IF OBJECT_ID(N'{s}.[DisclosureContent]', N'U') IS NULL
                     CREATE TABLE {s}.[DisclosureContent]
                     (
                         [Address] binary(32) NOT NULL CONSTRAINT [PK_DisclosureContent] PRIMARY KEY CLUSTERED,
                         [Kind] tinyint NOT NULL,
                         [Length] int NOT NULL,
                         [Bytes] varbinary(max) NULL,
                         [Elsewhere] bit NOT NULL CONSTRAINT [DF_DisclosureContent_Elsewhere] DEFAULT 0,
                         [Erased] bit NOT NULL CONSTRAINT [DF_DisclosureContent_Erased] DEFAULT 0
                     );

                 IF OBJECT_ID(N'{s}.[DisclosureSource]', N'U') IS NULL
                     CREATE TABLE {s}.[DisclosureSource]
                     (
                         [SourceHash] binary(32) NOT NULL CONSTRAINT [PK_DisclosureSource] PRIMARY KEY CLUSTERED,
                         [Source] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                         [Keyed] bit NOT NULL
                     );

             """);

        Table(script, s, "DisclosureBatch",
            """
            [EventId] uniqueidentifier NOT NULL,
            [Sequence] int NOT NULL,
            [Position] bigint NOT NULL,
            [AcceptedAt] datetimeoffset(7) NOT NULL,
            CONSTRAINT [PK_DisclosureBatch] PRIMARY KEY CLUSTERED ([EventId], [Sequence])
            """);
        Table(script, s, "DisclosureEvent",
            """
            [EventId] uniqueidentifier NOT NULL CONSTRAINT [PK_DisclosureEvent] PRIMARY KEY CLUSTERED,
            [Sequence] int NOT NULL,
            [At] datetimeoffset(7) NOT NULL,
            [Caller] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
            [CallerHash] binary(32) NULL,
            [Kind] tinyint NOT NULL,
            [Subscribed] bit NOT NULL,
            [Delivery] tinyint NOT NULL,
            [Source] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Request] binary(32) NULL,
            [Shape] binary(32) NULL,
            [Sensitive] bit NOT NULL,
            [Stamp] nvarchar(max) NULL,
            [Correlation] nvarchar(max) NULL,
            [Node] nvarchar(max) NULL,
            [ContentType] nvarchar(max) NULL
            """);
        Index(script, s, "DisclosureEvent", "IX_DisclosureEvent_Caller", "([CallerHash], [At] DESC, [EventId] DESC)");
        Table(script, s, "DisclosureClose",
            """
            [EventId] uniqueidentifier NOT NULL,
            [Sequence] int NOT NULL,
            [Outcome] tinyint NOT NULL,
            [Units] int NOT NULL,
            [At] datetimeoffset(7) NOT NULL,
            [Response] binary(32) NULL,
            CONSTRAINT [PK_DisclosureClose] PRIMARY KEY CLUSTERED ([EventId], [Sequence])
            """);
        Table(script, s, "DisclosureUnit",
            """
            [EventId] uniqueidentifier NOT NULL,
            [Ordinal] int NOT NULL,
            [Sequence] int NOT NULL,
            [Content] binary(32) NOT NULL,
            CONSTRAINT [PK_DisclosureUnit] PRIMARY KEY CLUSTERED ([EventId], [Ordinal])
            """);
        Table(script, s, "DisclosureEntity",
            """
            [EventId] uniqueidentifier NOT NULL,
            [Ordinal] int NOT NULL,
            [Slot] int NOT NULL,
            [Sequence] int NOT NULL,
            [RowHash] binary(32) NOT NULL,
            [Source] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [RowKey] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Via] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CONSTRAINT [PK_DisclosureEntity] PRIMARY KEY CLUSTERED ([EventId], [Ordinal], [Slot])
            """);
        Index(script, s, "DisclosureEntity", "IX_DisclosureEntity_Row", "([RowHash]) INCLUDE ([EventId], [Ordinal])");
        Table(script, s, "DisclosureShape",
            """
            [Address] binary(32) NOT NULL CONSTRAINT [PK_DisclosureShape] PRIMARY KEY CLUSTERED
            """);
        Table(script, s, "DisclosureField",
            """
            [Shape] binary(32) NOT NULL,
            [Position] int NOT NULL,
            [FieldHash] binary(32) NOT NULL,
            [FieldUse] tinyint NOT NULL,
            [Source] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Member] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Sensitive] bit NOT NULL,
            CONSTRAINT [PK_DisclosureField] PRIMARY KEY CLUSTERED ([Shape], [Position])
            """);
        Index(script, s, "DisclosureField", "IX_DisclosureField_Member", "([FieldHash], [FieldUse]) INCLUDE ([Shape])");
        Table(script, s, "DisclosureReview",
            """
            [ReviewId] uniqueidentifier NOT NULL CONSTRAINT [PK_DisclosureReview] PRIMARY KEY CLUSTERED,
            [At] datetimeoffset(7) NOT NULL,
            [Reviewer] nvarchar(max) NULL,
            [Question] tinyint NOT NULL,
            [Parameters] binary(32) NULL,
            [Results] int NOT NULL,
            [Node] nvarchar(max) NULL
            """);
        Index(script, s, "DisclosureReview", "IX_DisclosureReview_At", "([At] DESC, [ReviewId] DESC)");
        Table(script, s, "DisclosureReviewEvent",
            """
            [ReviewId] uniqueidentifier NOT NULL,
            [EventId] uniqueidentifier NOT NULL,
            CONSTRAINT [PK_DisclosureReviewEvent] PRIMARY KEY CLUSTERED ([ReviewId], [EventId])
            """);
        Index(script, s, "DisclosureReviewEvent", "IX_DisclosureReviewEvent_Event", "([EventId])");
        Table(script, s, "DisclosureErasure",
            """
            [Id] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_DisclosureErasure] PRIMARY KEY CLUSTERED,
            [At] datetimeoffset(7) NOT NULL,
            [By] nvarchar(max) NULL,
            [RowHash] binary(32) NOT NULL,
            [Source] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [RowKey] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Units] int NOT NULL
            """);
        Table(script, s, "DisclosureChain",
            """
            [Link] bigint NOT NULL CONSTRAINT [PK_DisclosureChain] PRIMARY KEY CLUSTERED,
            [EventId] uniqueidentifier NOT NULL,
            [Sequence] int NOT NULL,
            [Digest] binary(32) NOT NULL,
            [Hash] binary(32) NOT NULL
            """);

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

    // One of the tables that is only ever added to. Made through EXEC so that whether it is a ledger
    // table can be decided by the server the script is run on.
    static void Table(StringBuilder script, string schema, string name, string columns)
    {
        script.AppendLine($"    IF OBJECT_ID(N'{schema}.[{name}]', N'U') IS NULL");
        script.AppendLine($"        EXEC(N'CREATE TABLE {schema}.[{name}] ({string.Join(' ', columns.Split('\n').Select(_ => _.Trim()))})' + @ledger);");
        script.AppendLine();
    }

    static void Index(StringBuilder script, string schema, string table, string name, string keys)
    {
        script.AppendLine($"    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'{name}' AND [object_id] = OBJECT_ID(N'{schema}.[{table}]'))");
        script.AppendLine($"        CREATE NONCLUSTERED INDEX [{name}] ON {schema}.[{table}] {keys};");
        script.AppendLine();
    }

    // What SQL Server calls a regular identifier, narrowed to ASCII: enough for any schema name
    // somebody would choose, and nothing that needs escaping inside brackets or a string literal.
    static bool IsIdentifier(string name)
    {
        if (name.Length is 0 or > 128 ||
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
