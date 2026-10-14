# Scry.Server.Disclosure.SqlServer

A SQL Server store for the [Scry](https://github.com/Papyrine/Scry) [disclosure audit](https://github.com/Papyrine/Scry/blob/main/docs/disclosure-audit.md): a record of everything a server sent to each caller.

```cs
builder.Services.AddScry<SampleContext>(
    _ => _.UseSqlServerDisclosureAudit(connectionString));
```

Every answer is accepted into an outbox table before it is sent, in one committed `INSERT`. An answer the database does not accept is not sent. Pointed at the application's own database, that adds nothing that can be down to the path of an answer.

What was accepted is then moved, a step behind every answer, into tables that are only ever added to: who was sent which row of which source, when, and by which members. Every batch is linked into a chain of hashes, kept in an append-only ledger table where the server has them (SQL Server 2022, Azure SQL), which shows a change to anything recorded. The record itself is kept in segments, as a log is kept in files: a new one is started by age or by size, and the oldest is dropped, or handed on first, when there are more than are to be kept. An answer given again adds one row: the list of units it was made of is kept once, and each answer names it. A list is kept as runs of units that other lists share, so an answer that differs from an earlier one by a row adds the run that row is in and not the whole list again. `AddSqlServerDisclosureRecord` registers the record on a host that only reads it, such as an admin application with the disclosure explorer on it, and moves nothing. The content itself is kept apart, by address and once however often it is sent, in a table an erasure can remove from, or outside the database through `IScryDisclosureBlobStore`.

The same store answers the questions the record exists for, as `IScryDisclosureReader`: who received a row, what a caller received in a range of time, and whether a caller ever received a member. It erases a row's content as `IScryDisclosureEraser`, and says how far behind it is as `IScryDisclosureStatus`.

The store creates its schema and tables the first time it is used. A deployment that creates its own objects turns that off and runs `ScrySqlServerDisclosureStore.Script`. It is written for SQL Server 2017 and later, and for Azure SQL.

The record is the most sensitive data the host holds: it is everything every caller was ever sent, in one place. Give it the protection the tables it describes have, and more.

Docs: [Disclosure audit](https://github.com/Papyrine/Scry/blob/main/docs/disclosure-audit.md)
