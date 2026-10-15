/// <summary>
/// The tables the record is kept in, by name, within one schema. A segment of the record is one such
/// schema, holding every table of it, so that a segment is made whole and dropped whole. The store's
/// own schema holds a view of the same name for each, over every segment there is, and is what
/// everything that reads the record reads.
/// </summary>
sealed class DisclosureTables
{
    // Every table of a segment and both of its views, with the columns a view over all the segments
    // selects of each.
    static (string Name, string[] Columns)[] objects =
    [
        ("DisclosureBatch", ["EventId", "Sequence", "Position", "AcceptedAt", "Link"]),
        ("DisclosureEvent", ["EventId", "Sequence", "At", "Caller", "CallerHash", "Kind", "Subscribed", "Delivery", "Source", "Request", "Shape", "Sensitive", "Stamp", "Correlation", "Node", "ContentType", "Agent"]),
        ("DisclosureClose", ["EventId", "Sequence", "Outcome", "Units", "At", "Response"]),
        ("DisclosureUnit", ["EventId", "Ordinal", "Sequence", "Content"]),
        ("DisclosureEntity", ["EventId", "Ordinal", "Slot", "Sequence", "RowHash", "Source", "RowKey", "Via", "Version"]),
        ("DisclosureRun", ["Run", "Units"]),
        ("DisclosureRunUnit", ["Run", "Offset", "Content"]),
        ("DisclosureRunEntity", ["Run", "Offset", "Slot", "RowHash", "Source", "RowKey", "Via", "Version"]),
        ("DisclosureManifest", ["Manifest", "Units"]),
        ("DisclosureManifestRun", ["Manifest", "Start", "Run"]),
        ("DisclosureAnswer", ["EventId", "Sequence", "Start", "Manifest"]),
        ("DisclosureUnits", ["EventId", "Ordinal", "Sequence", "Content"]),
        ("DisclosureEntities", ["EventId", "Ordinal", "Slot", "Sequence", "RowHash", "Source", "RowKey", "Via", "Content", "Leading", "Version"]),
        ("DisclosureShape", ["Address"]),
        ("DisclosureField", ["Shape", "Position", "FieldHash", "FieldUse", "Source", "Member", "Sensitive"]),
        ("DisclosureContent", ["Address", "Kind", "Length", "Bytes", "Elsewhere", "Erased"]),
        ("DisclosureReview", ["ReviewId", "At", "Reviewer", "Question", "Parameters", "Results", "Node"]),
        ("DisclosureReviewEvent", ["ReviewId", "EventId"]),
        ("DisclosureErasure", ["Id", "At", "By", "RowHash", "Source", "RowKey", "Units"]),
        ("DisclosureSource", ["SourceHash", "Source", "Keyed"])
    ];

    public DisclosureTables(string schema)
    {
        Schema = schema;
        var s = $"[{schema}]";
        Batch = $"{s}.[DisclosureBatch]";
        Event = $"{s}.[DisclosureEvent]";
        Close = $"{s}.[DisclosureClose]";
        Unit = $"{s}.[DisclosureUnit]";
        Entity = $"{s}.[DisclosureEntity]";
        Run = $"{s}.[DisclosureRun]";
        RunUnit = $"{s}.[DisclosureRunUnit]";
        RunEntity = $"{s}.[DisclosureRunEntity]";
        Manifest = $"{s}.[DisclosureManifest]";
        ManifestRun = $"{s}.[DisclosureManifestRun]";
        Answer = $"{s}.[DisclosureAnswer]";
        Units = $"{s}.[DisclosureUnits]";
        Entities = $"{s}.[DisclosureEntities]";
        Shape = $"{s}.[DisclosureShape]";
        Field = $"{s}.[DisclosureField]";
        Content = $"{s}.[DisclosureContent]";
        Review = $"{s}.[DisclosureReview]";
        ReviewEvent = $"{s}.[DisclosureReviewEvent]";
        Erasure = $"{s}.[DisclosureErasure]";
        Source = $"{s}.[DisclosureSource]";
    }

    public string Schema { get; }

    /// <summary>Which batches the record has had, and the link of the chain each is.</summary>
    public string Batch { get; }

    public string Event { get; }

    public string Close { get; }

    public string Unit { get; }

    public string Entity { get; }

    /// <summary>A run of units, kept once however many lists it is part of.</summary>
    public string Run { get; }

    public string RunUnit { get; }

    public string RunEntity { get; }

    /// <summary>A list of units, kept once however many answers were made of it.</summary>
    public string Manifest { get; }

    /// <summary>The runs a list is made of, and where in it each starts.</summary>
    public string ManifestRun { get; }

    /// <summary>Which list each batch of an answer carried, and where in the answer the list starts.</summary>
    public string Answer { get; }

    /// <summary>
    /// Every unit of every event, whichever way it is kept: its own rows, or the rows of the runs of
    /// the list its batch names, each at the place the three starts add up to. What everything that
    /// reads units reads, so that none of it knows there are two.
    /// </summary>
    public string Units { get; }

    /// <summary>
    /// The same, for the rows units were read from, each with the content of its unit — so that
    /// going from a row to what was sent of it is a seek within a run and not a search of an answer.
    /// <c>Leading</c> is 1 for the first of a unit's rows that is that row, and 0 for a later one: a
    /// row joined to itself is in a unit twice, and was sent once.
    /// </summary>
    public string Entities { get; }

    public string Shape { get; }

    public string Field { get; }

    public string Content { get; }

    public string Review { get; }

    public string ReviewEvent { get; }

    public string Erasure { get; }

    public string Source { get; }

    /// <summary>
    /// Makes the schema and every table of it that is missing. Plain tables, whatever the server has:
    /// a segment has to be something that can be dropped, and a ledger table cannot be.
    /// </summary>
    public string Creating()
    {
        var s = $"[{Schema}]";
        var script = new StringBuilder();
        script.Append(
            $"""
                 IF SCHEMA_ID(N'{Schema}') IS NULL
                     EXEC(N'CREATE SCHEMA {s}');


             """);
        Table(script, s, "DisclosureContent",
            """
            [Address] binary(32) NOT NULL CONSTRAINT [PK_DisclosureContent] PRIMARY KEY CLUSTERED,
            [Kind] tinyint NOT NULL,
            [Length] int NOT NULL,
            [Bytes] varbinary(max) NULL,
            [Elsewhere] bit NOT NULL CONSTRAINT [DF_DisclosureContent_Elsewhere] DEFAULT 0,
            [Erased] bit NOT NULL CONSTRAINT [DF_DisclosureContent_Erased] DEFAULT 0
            """);
        Table(script, s, "DisclosureSource",
            """
            [SourceHash] binary(32) NOT NULL CONSTRAINT [PK_DisclosureSource] PRIMARY KEY CLUSTERED,
            [Source] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Keyed] bit NOT NULL
            """);
        Table(script, s, "DisclosureBatch",
            """
            [EventId] uniqueidentifier NOT NULL,
            [Sequence] int NOT NULL,
            [Position] bigint NOT NULL,
            [AcceptedAt] datetimeoffset(7) NOT NULL,
            [Link] bigint NOT NULL,
            CONSTRAINT [PK_DisclosureBatch] PRIMARY KEY CLUSTERED ([EventId], [Sequence])
            """);
        Index(script, s, "DisclosureBatch", "IX_DisclosureBatch_Link", "([Link])");
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
            [ContentType] nvarchar(max) NULL,
            [Agent] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL
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
            [Version] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
            CONSTRAINT [PK_DisclosureEntity] PRIMARY KEY CLUSTERED ([EventId], [Ordinal], [Slot])
            """);
        Index(script, s, "DisclosureEntity", "IX_DisclosureEntity_Row", "([RowHash]) INCLUDE ([EventId], [Ordinal])");

        // What a batch carries is a list of units, and the same rows asked for again are the same
        // list: it is kept once, by an address, and each batch names it. The same query asked a
        // thousand times is then a thousand events and one list.
        //
        // A list is in turn kept as the runs it is made of, each kept once by an address of its own.
        // Two lists that differ by a row are the same runs but for the one the row is in, so the
        // second adds that run and a line for each of the others, and not every unit over again.
        Table(script, s, "DisclosureRun",
            """
            [Run] binary(32) NOT NULL CONSTRAINT [PK_DisclosureRun] PRIMARY KEY CLUSTERED,
            [Units] int NOT NULL
            """);
        Table(script, s, "DisclosureRunUnit",
            """
            [Run] binary(32) NOT NULL,
            [Offset] int NOT NULL,
            [Content] binary(32) NOT NULL,
            CONSTRAINT [PK_DisclosureRunUnit] PRIMARY KEY CLUSTERED ([Run], [Offset])
            """);
        Table(script, s, "DisclosureRunEntity",
            """
            [Run] binary(32) NOT NULL,
            [Offset] int NOT NULL,
            [Slot] int NOT NULL,
            [RowHash] binary(32) NOT NULL,
            [Source] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [RowKey] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Via] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Version] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
            CONSTRAINT [PK_DisclosureRunEntity] PRIMARY KEY CLUSTERED ([Run], [Offset], [Slot])
            """);
        Index(script, s, "DisclosureRunEntity", "IX_DisclosureRunEntity_Row", "([RowHash])");
        Table(script, s, "DisclosureManifest",
            """
            [Manifest] binary(32) NOT NULL CONSTRAINT [PK_DisclosureManifest] PRIMARY KEY CLUSTERED,
            [Units] int NOT NULL
            """);
        Table(script, s, "DisclosureManifestRun",
            """
            [Manifest] binary(32) NOT NULL,
            [Start] int NOT NULL,
            [Run] binary(32) NOT NULL,
            CONSTRAINT [PK_DisclosureManifestRun] PRIMARY KEY CLUSTERED ([Manifest], [Start])
            """);
        Index(script, s, "DisclosureManifestRun", "IX_DisclosureManifestRun_Run", "([Run])");
        Table(script, s, "DisclosureAnswer",
            """
            [EventId] uniqueidentifier NOT NULL,
            [Sequence] int NOT NULL,
            [Start] int NOT NULL,
            [Manifest] binary(32) NOT NULL,
            CONSTRAINT [PK_DisclosureAnswer] PRIMARY KEY CLUSTERED ([EventId], [Sequence])
            """);
        Index(script, s, "DisclosureAnswer", "IX_DisclosureAnswer_Manifest", "([Manifest]) INCLUDE ([Start])");
        View(script, s, "DisclosureUnits",
            $"""
             SELECT u.[EventId], u.[Ordinal], u.[Sequence], u.[Content] FROM {s}.[DisclosureUnit] u
             UNION ALL
             SELECT a.[EventId], a.[Start] + l.[Start] + r.[Offset] AS [Ordinal], a.[Sequence], r.[Content] FROM {s}.[DisclosureAnswer] a
             JOIN {s}.[DisclosureManifestRun] l ON l.[Manifest] = a.[Manifest]
             JOIN {s}.[DisclosureRunUnit] r ON r.[Run] = l.[Run]
             """);
        View(script, s, "DisclosureEntities",
            $"""
             SELECT n.[EventId], n.[Ordinal], n.[Slot], n.[Sequence], n.[RowHash], n.[Source], n.[RowKey], n.[Via], u.[Content],
             CASE WHEN EXISTS (SELECT 1 FROM {s}.[DisclosureEntity] m WHERE m.[EventId] = n.[EventId] AND m.[Ordinal] = n.[Ordinal] AND m.[Slot] < n.[Slot] AND m.[RowHash] = n.[RowHash] AND m.[Source] = n.[Source] AND m.[RowKey] = n.[RowKey]) THEN 0 ELSE 1 END AS [Leading], n.[Version]
             FROM {s}.[DisclosureEntity] n
             JOIN {s}.[DisclosureUnit] u ON u.[EventId] = n.[EventId] AND u.[Ordinal] = n.[Ordinal]
             UNION ALL
             SELECT a.[EventId], a.[Start] + l.[Start] + r.[Offset] AS [Ordinal], r.[Slot], a.[Sequence], r.[RowHash], r.[Source], r.[RowKey], r.[Via], u.[Content],
             CASE WHEN EXISTS (SELECT 1 FROM {s}.[DisclosureRunEntity] m WHERE m.[Run] = r.[Run] AND m.[Offset] = r.[Offset] AND m.[Slot] < r.[Slot] AND m.[RowHash] = r.[RowHash] AND m.[Source] = r.[Source] AND m.[RowKey] = r.[RowKey]) THEN 0 ELSE 1 END AS [Leading], r.[Version]
             FROM {s}.[DisclosureAnswer] a
             JOIN {s}.[DisclosureManifestRun] l ON l.[Manifest] = a.[Manifest]
             JOIN {s}.[DisclosureRunEntity] r ON r.[Run] = l.[Run]
             JOIN {s}.[DisclosureRunUnit] u ON u.[Run] = r.[Run] AND u.[Offset] = r.[Offset]
             """);
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
        return script.ToString();
    }

    /// <summary>Drops every table of the schema, and the schema.</summary>
    public string Dropping()
    {
        var script = new StringBuilder();
        script.AppendLine($"DROP VIEW IF EXISTS {Units}, {Entities};");
        foreach (var (name, _) in objects)
        {
            if (name is "DisclosureUnits" or "DisclosureEntities")
            {
                continue;
            }

            script.AppendLine($"DROP TABLE IF EXISTS [{Schema}].[{name}];");
        }

        script.AppendLine($"IF SCHEMA_ID(N'{Schema}') IS NOT NULL EXEC(N'DROP SCHEMA [{Schema}]');");
        return script.ToString();
    }

    /// <summary>
    /// Makes, in this schema, a view of each table over the same table of every one of
    /// <paramref name="segments"/>, each row with the number of the segment it is in.
    /// </summary>
    public string Viewing(IReadOnlyList<(int Number, DisclosureTables Tables)> segments)
    {
        var script = new StringBuilder();
        foreach (var (name, columns) in objects)
        {
            var listed = string.Join(", ", columns.Select(_ => $"t.[{_}]"));
            var parts = segments.Select(_ => $"SELECT {_.Number} AS [Segment], {listed} FROM [{_.Tables.Schema}].[{name}] t");
            script.AppendLine($"    EXEC(N'CREATE OR ALTER VIEW [{Schema}].[{name}] AS {string.Join(" UNION ALL ", parts)}');");
        }

        return script.ToString();
    }

    static void Table(StringBuilder script, string schema, string name, string columns)
    {
        script.AppendLine($"    IF OBJECT_ID(N'{schema}.[{name}]', N'U') IS NULL");
        script.AppendLine($"        CREATE TABLE {schema}.[{name}] ({string.Join(' ', columns.Split('\n').Select(_ => _.Trim()))});");
        script.AppendLine();
    }

    // Made through EXEC because a view has to be the only statement of its batch.
    static void View(StringBuilder script, string schema, string name, string select)
    {
        script.AppendLine($"    IF OBJECT_ID(N'{schema}.[{name}]', N'V') IS NULL");
        script.AppendLine($"        EXEC(N'CREATE VIEW {schema}.[{name}] AS {string.Join(' ', select.Split('\n').Select(_ => _.Trim()))}');");
        script.AppendLine();
    }

    static void Index(StringBuilder script, string schema, string table, string name, string keys)
    {
        script.AppendLine($"    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'{name}' AND [object_id] = OBJECT_ID(N'{schema}.[{table}]'))");
        script.AppendLine($"        CREATE NONCLUSTERED INDEX [{name}] ON {schema}.[{table}] {keys};");
        script.AppendLine();
    }
}
