namespace Scry;

/// <summary>
/// The disclosure audit kept in SQL Server. Accepting an answer is one committed <c>INSERT</c> into
/// an outbox table, so it costs the request a round trip to a database it usually already depends
/// on, and survives the loss of the node that accepted it. What was accepted is then moved into
/// tables that are only ever added to, which are what the record is read from.
/// </summary>
/// <remarks>
/// <para>
/// Pointed at the application's own database, the accept adds nothing that can be down to the path
/// of an answer. Moving a batch on, reading the record and erasing from it are all a step behind the
/// outbox, so none of them is on that path at all.
/// </para>
/// <para>
/// The tables hold who was sent what and never the content: that is kept apart, by address, in a
/// table an erasure can remove from — or outside the database, with
/// <see cref="ScrySqlServerDisclosureOptions.Content"/>.
/// </para>
/// </remarks>
public sealed partial class ScrySqlServerDisclosureStore :
    IScryDisclosureSink,
    IScryDisclosureReader,
    IScryDisclosureEraser,
    IScryDisclosureStatus,
    IScryDisclosureVerifier,
    IDisposable,
    IAsyncDisposable
{
    ScrySqlServerDisclosureOptions options;
    DisclosureSql sql;
    SemaphoreSlim creating = new(1, 1);
    volatile bool created;

    public ScrySqlServerDisclosureStore(ScrySqlServerDisclosureOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException($"{nameof(ScrySqlServerDisclosureOptions)}.{nameof(options.ConnectionString)} is empty. It names the database the disclosure record is kept in.");
        }

        if (options.AcceptTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"{nameof(ScrySqlServerDisclosureOptions)}.{nameof(options.AcceptTimeout)} must be greater than zero. It is how long an answer waits for its record to be accepted.");
        }

        if (options.DrainBatches < 1)
        {
            throw new ArgumentException($"{nameof(ScrySqlServerDisclosureOptions)}.{nameof(options.DrainBatches)} must be greater than zero. It is how many batches are moved out of the outbox in one transaction.");
        }

        if (options.DrainInterval is { } interval &&
            interval <= TimeSpan.Zero)
        {
            throw new ArgumentException($"{nameof(ScrySqlServerDisclosureOptions)}.{nameof(options.DrainInterval)} must be greater than zero. Null leaves moving batches on to the host.");
        }

        this.options = options;
        sql = new(options.Schema, options.LedgerTables);
    }

    /// <summary>
    /// The statements that make the store's schema and tables, each guarded so that running the whole
    /// of it again changes nothing. What a deployment that creates its own objects runs, with
    /// <see cref="ScrySqlServerDisclosureOptions.CreateTables"/> off.
    /// </summary>
    public string Script => sql.Create;

    /// <summary>
    /// Makes whatever of the schema and its tables is missing. Done on first use where
    /// <see cref="ScrySqlServerDisclosureOptions.CreateTables"/> is on; called at startup, it moves a
    /// login that may not create anything from the first answer to the deployment.
    /// </summary>
    public async Task EnsureCreatedAsync(Cancel cancel = default)
    {
        await using var connection = await OpenAsync(cancel);
        await using var command = Command(connection, sql.Create);

        // Not an answer's wait: this is DDL behind a lock another node may be holding.
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancel);
        created = true;
    }

    /// <inheritdoc />
    public void Append(ScryDisclosureBatch batch)
    {
        Ensure();
        using (var connection = new SqlConnection(options.ConnectionString))
        {
            connection.Open();
            using var command = Accept(connection, batch);
            command.ExecuteNonQuery();
        }

        Accepted();
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel)
    {
        await EnsureAsync(cancel);
        await using (var connection = await OpenAsync(cancel))
        {
            await using var command = Accept(connection, batch);
            await command.ExecuteNonQueryAsync(cancel);
        }

        Accepted();
    }

    // The batch as the bytes every sink keeps, handed to the driver as the array they were written
    // into rather than copied out to one of exactly the right length.
    SqlCommand Accept(SqlConnection connection, ScryDisclosureBatch batch)
    {
        var bytes = new ArrayBufferWriter<byte>();
        batch.Serialize(bytes);
        MemoryMarshal.TryGetArray(bytes.WrittenMemory, out var written);

        var command = Command(connection, sql.Accept);
        command.Parameters.Add(new("@event", SqlDbType.UniqueIdentifier) {Value = batch.EventId});
        command.Parameters.Add(new("@sequence", SqlDbType.Int) {Value = batch.Sequence});
        command.Parameters.Add(new("@batch", SqlDbType.VarBinary, written.Count) {Value = written.Array});
        return command;
    }

    async ValueTask<SqlConnection> OpenAsync(Cancel cancel)
    {
        var connection = new SqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancel);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    SqlCommand Command(SqlConnection connection, string text, SqlTransaction? transaction = null) =>
        new(text, connection, transaction)
        {
            CommandTimeout = Math.Max(1, (int) Math.Ceiling(options.AcceptTimeout.TotalSeconds))
        };

    async ValueTask EnsureAsync(Cancel cancel)
    {
        if (created ||
            !options.CreateTables)
        {
            return;
        }

        await creating.WaitAsync(cancel);
        try
        {
            if (!created)
            {
                await EnsureCreatedAsync(cancel);
            }
        }
        finally
        {
            creating.Release();
        }
    }

    void Ensure()
    {
        if (created ||
            !options.CreateTables)
        {
            return;
        }

        creating.Wait();
        try
        {
            if (created)
            {
                return;
            }

            using var connection = new SqlConnection(options.ConnectionString);
            connection.Open();
            using var command = Command(connection, sql.Create);
            command.CommandTimeout = 120;
            command.ExecuteNonQuery();
            created = true;
        }
        finally
        {
            creating.Release();
        }
    }

    static object Value(object? value) =>
        value ?? DBNull.Value;

    static byte[]? Bytes(ScryDisclosureAddress? address) =>
        address?.ToArray();

    static ScryDisclosureAddress? Address(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ScryDisclosureAddress.From((byte[]) reader[ordinal]);
    }

    static string? Text(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetString(ordinal);
    }
}
