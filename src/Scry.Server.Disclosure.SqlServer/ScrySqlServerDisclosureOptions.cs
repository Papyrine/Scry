namespace Scry;

/// <summary>How the SQL Server disclosure store reaches its database, and what it does there.</summary>
// begin-snippet: sqlServerDisclosureOptions
public sealed class ScrySqlServerDisclosureOptions
{
    /// <summary>
    /// The database the record is kept in. The application's own is the usual choice: an answer is
    /// then accepted by a database the request already depends on, and nothing new can be down.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The schema the store's tables live in. Default <c>scry</c>.</summary>
    public string Schema { get; set; } = "scry";

    /// <summary>
    /// Whether the store makes its schema and tables where they are missing, the first time it is
    /// used. On by default. Off, they are a deployment's to create, from
    /// <see cref="ScrySqlServerDisclosureStore.Script"/>, and the login the server runs as needs no
    /// right to create anything.
    /// </summary>
    public bool CreateTables { get; set; } = true;

    /// <summary>
    /// How long an answer waits for its record to be accepted before it fails instead. Default
    /// fifteen seconds. Opening the connection is bounded by the connection string's own timeout.
    /// </summary>
    public TimeSpan AcceptTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long any other statement may run: a question asked of the record, the store's status, a
    /// round of moving batches on. Default a minute. Apart from <see cref="AcceptTimeout"/>, which is
    /// what an answer waits on and is kept short for that reason; a read of a large record on a busy
    /// server is not something to fail for taking longer.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether the chain is kept in append-only ledger tables, which the database itself then refuses
    /// to update or delete from. Null, the default, makes them so where the server has ledger tables
    /// — SQL Server 2022 and Azure SQL — and plain where it does not. True refuses to create them
    /// plain; false always does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chain is a hash for every batch the record has had, each covering the one before, and is
    /// what shows a change to anything recorded. It is the one part of the record kept for good. The
    /// rest is in plain tables, so that the oldest of it can be dropped: a row put into a ledger
    /// table is there until the database is dropped.
    /// </para>
    /// <para>
    /// Read when the tables are created, and never after: a table is what it was made as.
    /// </para>
    /// </remarks>
    public bool? LedgerTables { get; set; }

    /// <summary>
    /// What is kept in ledger tables: the chain of hashes alone, the default, or the whole record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ScryDisclosureLedgerScope.Hashes"/> keeps the record in plain tables, so that the
    /// oldest of it can be dropped, and the chain is what shows a change to it. The database does
    /// not refuse one.
    /// </para>
    /// <para>
    /// <see cref="ScryDisclosureLedgerScope.Record"/> keeps every table the record is only ever added
    /// to in ledger tables, and the database refuses any change to them. Nothing but dropping the
    /// database removes a row of them, so such a record is never rolled or retired, and the
    /// settings for that are refused. Content stays in a plain table, so an erasure still removes it.
    /// Needs ledger tables: SQL Server 2022 on, or Azure SQL.
    /// </para>
    /// <para>
    /// Decided by whichever node makes the record, and held by it: a node that moves the record on
    /// and was told otherwise refuses to.
    /// </para>
    /// </remarks>
    public ScryDisclosureLedgerScope LedgerScope { get; set; }

    /// <summary>
    /// How long a segment of the record is written to before a new one is started. Null, the
    /// default, never starts one for its age.
    /// </summary>
    /// <remarks>
    /// The record is kept in segments, as a log is kept in files: one is written to, and the rest are
    /// as they were left. A new one is started when something is about to be recorded and the one
    /// being written to is this old, or <see cref="RollAtBytes"/> large.
    /// <see cref="ScrySqlServerDisclosureStore.RollAsync"/> starts one when asked. Starting one makes
    /// tables, whatever <see cref="CreateTables"/> says.
    /// </remarks>
    public TimeSpan? RollAfter { get; set; }

    /// <summary>
    /// How large a segment of the record grows, in bytes of the database, before a new one is
    /// started. Null, the default, never starts one for its size.
    /// </summary>
    public long? RollAtBytes { get; set; }

    /// <summary>
    /// How many segments are kept. When there are more, the oldest is retired: handed to
    /// <see cref="Retire"/> where that is set, and then dropped. Null, the default, keeps every one.
    /// </summary>
    /// <remarks>
    /// What a retired segment held is gone from the record: who was sent what in it can no longer be
    /// asked. What stays is its links in the chain, which say that it was there and is what a copy
    /// of it can be checked against, and a line saying when it was retired and how much it held.
    /// </remarks>
    public int? Keep { get; set; }

    /// <summary>
    /// How long a segment is kept once nothing more is being written to it, by when the last answer
    /// in it was given. Past it the segment is retired, as one past <see cref="Keep"/> is. Null, the
    /// default, keeps a segment for its age.
    /// </summary>
    /// <remarks>
    /// Retirement is of whole segments, so an answer is kept for at least this long and at most this
    /// long and one segment's span more: set <see cref="RollAfter"/> to a fraction of it to keep the
    /// difference small. The segment being written to is never retired.
    /// </remarks>
    public TimeSpan? KeepFor { get; set; }

    /// <summary>
    /// What is done with a segment before it is dropped: written somewhere else, usually. Null, the
    /// default, drops it and keeps nothing.
    /// </summary>
    /// <remarks>
    /// Called with the segment still in the record and no longer written to. It is dropped only once
    /// this returns. If it throws, the segment stays, and it is called again the next time the record
    /// is moved on, so it may be called for one segment more than once.
    /// </remarks>
    public Func<ScryDisclosureArchive, Cancel, Task>? Retire { get; set; }

    /// <summary>
    /// How often accepted batches are moved from the outbox into the tables a reader reads, where
    /// nothing has prompted it sooner. Default one second. Null leaves it to the host, which calls
    /// <see cref="ScrySqlServerDisclosureStore.DrainAsync"/> itself.
    /// </summary>
    /// <remarks>
    /// A node that accepts a batch moves it at once, so this is how long a batch another node
    /// accepted may wait, and how soon a failed move is tried again.
    /// </remarks>
    public TimeSpan? DrainInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether a question waits, before it is answered, until everything accepted before it was asked
    /// has been moved into the record. Off by default: a question is answered from what has been
    /// moved on, which trails what was accepted by the time one move takes.
    /// </summary>
    /// <remarks>
    /// On, a question asked straight after an answer was given finds it, at the cost of every
    /// question waiting on the mover, for as long as <see cref="AcceptTimeout"/> at most.
    /// <see cref="ScrySqlServerDisclosureStore.CatchUpAsync"/> does the same for one question.
    /// </remarks>
    public bool CatchUpBeforeReading { get; set; }

    /// <summary>How many batches are moved in one transaction. Default 200.</summary>
    public int DrainBatches { get; set; } = 200;

    /// <summary>
    /// Where content is kept instead of in the database: object storage, a file share. Null, the
    /// default, keeps it in a table of its own beside the record.
    /// </summary>
    /// <remarks>
    /// Content is the bulk of the record and the part an erasure removes. Kept elsewhere, the
    /// database holds only who was sent what, by address.
    /// </remarks>
    public IScryDisclosureBlobStore? Content { get; set; }

    /// <summary>The clock erasures and checks are timed by. The system's by default.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
}
// end-snippet
