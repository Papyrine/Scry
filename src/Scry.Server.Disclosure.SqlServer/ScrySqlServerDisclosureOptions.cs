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
    /// Whether the tables that are only ever added to are made as append-only ledger tables, which
    /// the database itself then refuses to update or delete from. Null, the default, makes them so
    /// where the server has ledger tables — SQL Server 2022 and Azure SQL — and plain where it does
    /// not. True refuses to create them plain; false always does.
    /// </summary>
    /// <remarks>
    /// Read when the tables are created, and never after: a table is what it was made as.
    /// </remarks>
    public bool? LedgerTables { get; set; }

    /// <summary>
    /// Whether every batch is also linked into a hash chain, each record's hash covering the one
    /// before it, so that a change to what was recorded shows as a break. Off by default. For a
    /// server without ledger tables, which have the same property and are checked by the database.
    /// </summary>
    public bool HashChain { get; set; }

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
