# Scry.Server

Server-side execution for [Scry](https://github.com/Papyrine/Scry). Validates an incoming query AST against the allow-list, rebinds it to the real EF Core entity types, applies row-level policies, executes against a `DbContext`, and returns projected rows.

<!-- snippet: serverRegistration -->
<a id='snippet-serverRegistration'></a>
```cs
builder.Services
    .AddScry<SampleContext>(
    _ =>
    {
        // Holiday is a [QueryablePoco]: it has no table, so the server supplies its rows. Every
        // [QueryablePoco] type must be registered here or AddScry throws at startup.
        _.AddPocoSource(_ => Holiday.Seed());
        // Department.Handbook is an [Attachment], and one exposed without a check is a startup
        // failure. Registered here rather than by [AttachmentWith] because the model project
        // references the annotations alone and has no server type to name.
        _.AddAttachmentPolicy<Department, HandbookPolicy>();
        _.MaxPageSize = 200;

        // A row policy whose decision is too slow to run per row in SQL, so it runs in C# and
        // the server remembers what it answered. Revision is what tells it a row has changed
        // and needs deciding again — see /docs/policies.md and the /permissions page.
        _.AddCachedPolicy<Order, long, RegionAccessPolicy>(_ => _.Revision);

        // Repeat a query while nothing has been written and the answer is a 304 rather than a
        // re-execution. Optional, and off until a freshness source says how to tell — see
        // /docs/caching.md.
        _.UseDeltaFreshness<SampleContext>();

        // What a cached response belongs to. This server has sources whose answers depend on
        // who asked — the row policy above, and Department.Handbook's attachment check — and
        // MapScry refuses to start without this. The sample has no sign-in, so there is one
        // caller and one scope; a real app returns its tenant or its principal, and a client
        // signing in as someone else is then never handed the previous one's rows.
        _.CacheScope = _ => "sample";
    });
```
<sup><a href='/samples/Sample.Server/Program.cs#L31-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-serverRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AddPocoSource` supplies the rows for a `[QueryablePoco]` type — see [POCO sources](https://github.com/Papyrine/Scry/blob/main/docs/server.md#poco-sources).

<!-- snippet: mapScry -->
<a id='snippet-mapScry'></a>
```cs
app.MapScry("/api/query");
```
<sup><a href='/samples/Sample.Server/Program.cs#L79-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Docs: [Server](https://github.com/Papyrine/Scry/blob/main/docs/server.md) · [Row policies](https://github.com/Papyrine/Scry/blob/main/docs/policies.md) · [Security model](https://github.com/Papyrine/Scry/blob/main/docs/security.md)
