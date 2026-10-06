// A Scry server that keeps a record of everything it sends: the disclosure audit, kept in SQL Server
// in the application's own database, with the explorer that reads it back.
//
//   dotnet run --project samples/Sample.DisclosureServer
//
// Open it, be alice, and ask a few queries in the query explorer. Then be the auditor and open
// /scry-disclosures: who was sent which row, what a caller received, and whether a caller ever saw
// a member — see /docs/disclosure-audit.md.
//
// A host of its own, apart from Sample.WebServer, because that one answers a repeated query with a
// 304 and this one cannot: a 304 runs nothing, so there would be nothing to record, and a server
// with the audit on refuses to start beside conditional requests.
class Program
{
    // A LocalDB instance of this sample's own. The other samples and the test projects share one,
    // and a second process building the same template while another holds it deadlocks inside SQL Server.
    static SqlInstance<SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            SampleContext.Initialize(_);
            return Task.CompletedTask;
        },
        storage: Storage.FromSuffix<SampleContext>("Disclosure"));

    static async Task Main(string[] args)
    {
        // Named after this method, unless the launcher names it: the browser suite runs several of
        // these at once, as it does of Sample.WebServer.
        var name = Environment.GetEnvironmentVariable("SAMPLE_DATABASE");
        SqlDatabase<SampleContext> database;
        if (name is null)
        {
            database = await sqlInstance.Build();
        }
        else
        {
            database = await sqlInstance.Build(name);
        }

        var builder = WebApplication.CreateBuilder(args);
        var services = builder.Services;
        services.AddDbContext<SampleContext>(_ => _.UseSqlServer(database.ConnectionString));

        // The clock the record is timed by: the system's, unless the launcher pins one. The browser
        // suite does, so that a screenshot of the record shows the same times on every run.
        var clock = Clock();

        // begin-snippet: sampleDisclosureAudit
        services
            .AddScry<SampleContext>(_ =>
            {
                // What the model needs before any server will start: see Sample.WebServer.
                _.AddPocoSource(_ => Holiday.Seed());
                _.AddAttachmentPolicy<Department, SignedIn<Department>>();
                _.AddAttachmentPolicy<Employee, SignedIn<Employee>>();

                // Every answer is a committed row in this database before it is sent, and an answer
                // the database does not take is not given. The application's own database, so that
                // recording adds nothing that can be down to the path of an answer.
                _.UseSqlServerDisclosureAudit(
                    database.ConnectionString,
                    store =>
                    {
                        // Each batch of the record linked to the one before, so that a change to what
                        // was recorded shows. A server with ledger tables has them as well, and they
                        // are the stronger of the two; this is what the explorer's check reads.
                        store.HashChain = true;
                        store.Clock = clock;
                    },
                    audit =>
                    {
                        audit.Node = "sample";
                        audit.Clock = clock;

                        // A row is recorded by its key, and a source with none has to be given one or
                        // owned up to: a view by what its rows are grouped on, a list from memory by
                        // nothing at all.
                        audit.Key<EmployeeSummary>(_ => _.Department);
                        audit.Unkeyed<Holiday>();
                    });
            });
        // end-snippet

        var app = builder.Build();
        app.UseDemoSignIn();
        app.MapDemoSignIn();
        app.MapScry("/api/query");

        // Something to ask with. Open to anybody with a name, since what it asks goes through the
        // same endpoint, policies and record as any other client's.
        app.MapScryExplorer(_ =>
        {
            _.Route = "/scry";
            _.EnableGuard = _ => _.User.Identity?.IsAuthenticated == true;
        });

        // begin-snippet: mapDisclosureExplorer
        // The record, read back. Shut to everybody but the one person whose job it is — a real host
        // puts its own authorization here, and RequireAuthorization on what this returns.
        app.MapScryDisclosureExplorer(_ =>
        {
            _.EnableGuard = DemoSignIn.IsReviewer;
            _.EnableExport = DemoSignIn.IsReviewer;

            // Off unless a host turns it on, and it cannot be taken back: on here so there is
            // something to try it against.
            _.EnableErase = DemoSignIn.IsReviewer;
        });
        // end-snippet

        await app.RunAsync();
    }

    static TimeProvider Clock()
    {
        if (DateTimeOffset.TryParse(Environment.GetEnvironmentVariable("SAMPLE_CLOCK"), out var start))
        {
            return new Pinned(start);
        }

        return TimeProvider.System;
    }

    // Starts where it is told and says a second later each time it is asked.
    sealed class Pinned(DateTimeOffset start) :
        TimeProvider
    {
        long asked;

        public override DateTimeOffset GetUtcNow() =>
            start.AddSeconds(Interlocked.Increment(ref asked));
    }

    // The check that authorizes an attachment's fetch: anybody with a name. A source exposing an
    // attachment with no check refuses to start — see Sample.WebServer's HandbookPolicy.
    sealed class SignedIn<TSource> :
        IAttachmentPolicy<TSource>
    {
        public bool Authorize(ScryAttachmentContext context) =>
            context.Services.GetService<IHttpContextAccessor>()?.HttpContext?.User.Identity?.IsAuthenticated == true;
    }
}
