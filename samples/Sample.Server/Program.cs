class Program
{
    // Back the sample with EfLocalDb: each launch runs against its own LocalDB database, cloned from a
    // seeded template. EfLocalDb manages its own instance and self-heals orphaned files, so the sample
    // can never wedge on a leftover .mdf the way a fixed-name EnsureCreated database can.
    static SqlInstance<SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    static async Task Main(string[] args)
    {
        var database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder(args);

        // Serve the Blazor client's static web assets even when not running in the Development environment.
        builder.WebHost.UseStaticWebAssets();

        builder.Services
            .AddDbContext<SampleContext>(_ => _.UseSqlServer(database.ConnectionString));

        // The sample's own authorization data, and the policy that reads it. The policy is resolved
        // from here rather than constructed, which is what lets it take a dependency at all.
        builder.Services.AddSingleton<RegionGrants>();
        builder.Services.AddSingleton<RegionAccessPolicy>();

        // begin-snippet: serverRegistration
        builder.Services
            .AddScry<SampleContext>(
            _ =>
            {
                // Holiday is a [QueryablePoco]: it has no table, so the server supplies its rows. Every
                // [QueryablePoco] type must be registered here or AddScry throws at startup.
                _.AddPocoSource(_ => Holiday.Seed());
                // Department.Handbook and Employee.Photo are [Attachment]s, and one exposed without a
                // check is a startup failure. Registered here rather than by [AttachmentWith] because
                // the model project references the annotations alone and has no server type to name.
                _.AddAttachmentPolicy<Department, HandbookPolicy>();
                _.AddAttachmentPolicy<Employee, PhotoPolicy>();
                _.MaxPageSize = 200;

                // begin-snippet: addCachedPolicy
                // A row policy whose decision is too slow to run per row in SQL, so it runs in C# and
                // the server remembers what it answered. Revision is what tells it a row has changed
                // and needs deciding again — see /docs/policies.md and the /permissions page.
                _.AddCachedPolicy<Order, long, RegionAccessPolicy>(_ => _.Revision);
                // end-snippet

                // Repeat a query while nothing has been written and the answer is a 304 rather than a
                // re-execution. Optional, and off until a freshness source says how to tell — see
                // /docs/caching.md.
                _.UseDeltaFreshness<SampleContext>();

                // What a cached response belongs to. This server has sources whose answers depend on
                // who asked — the row policy above, and Department.Handbook's attachment check — and
                // MapScry refuses to start without this. The sample has no sign-in, so the caller
                // half is a constant; a real app returns its tenant or its principal, and a client
                // signing in as someone else is then never handed the previous one's rows.
                //
                // The grants version is the other half, and is the part worth copying. A response
                // varies by what the caller is allowed to see, and QueryFreshness only watches the
                // database — so a grant changing outside it would move nothing, and a cache holding
                // the old rows would go on answering with rows the caller has since lost.
                _.CacheScope = _ => $"sample-{_.RequestServices.GetRequiredService<RegionGrants>().Version}";
            });
        // end-snippet

        // Scry's telemetry is dormant until something subscribes; opting in is one AddSource and one
        // AddMeter. See /docs/observability.md for the spans, instruments, and tags.
        // begin-snippet: openTelemetry
        builder.Services.AddOpenTelemetry()
            .WithTracing(_ => _.AddSource(ScryInstrumentation.ActivitySourceName))
            .WithMetrics(_ => _.AddMeter(ScryInstrumentation.MeterName));
        // end-snippet

        var app = builder.Build();

        app.UseBlazorFrameworkFiles();
        app.UseStaticFiles();

        // begin-snippet: mapScry
        app.MapScry("/api/query");
        // end-snippet

        // What the /permissions page drives. None of it is Scry's — it is the sample standing in for
        // the two things only a host can know about a cached policy.
        app.MapGet("/api/grants", (RegionGrants grants) =>
            new GrantState([.. RegionGrants.Regions], [.. grants.For("sample")], grants.Lookups));

        // begin-snippet: invalidateCachedPolicy
        // A grant moved. Nothing about any order changed, so no version column could notice and no
        // query would ever decide those rows again — the cache has to be told, and telling it is part
        // of the authorization path rather than a cache optimization.
        app.MapPost("/api/grants/{region}", (string region, bool allowed, RegionGrants grants, ScryPolicyCache cache) =>
        {
            grants.Set("sample", region, allowed);
            cache.InvalidateScope<Order>("sample");
            return Results.NoContent();
        });
        // end-snippet

        // begin-snippet: cachedPolicyReadThrough
        // A row changed. Nobody tells the cache anything here: the next query sees a revision past the
        // watermark this scope was decided up to, and decides that one row on the spot. An insert by
        // any writer at all is correct on its first read for the same reason.
        app.MapPost("/api/orders/{id:int}/touch", async (int id, SampleContext data) =>
        {
            var order = await data.Orders.FindAsync(id);
            if (order is null)
            {
                return Results.NotFound();
            }

            // Named explicitly: Scry's async terminals and EF's are both in scope here, and they are
            // not the same method — this one has to run against the database.
            order.Revision = await EntityFrameworkQueryableExtensions.MaxAsync(data.Orders, _ => _.Revision) + 1;
            await data.SaveChangesAsync();
            return Results.NoContent();
        });
        // end-snippet
        // begin-snippet: mapExplorer
        app.MapScryExplorer(
            _ =>
            {
                _.Route = "/scry";
                // This sample always exposes the explorer. The default guard is Development-only — in a real
                // app, run in Development or set EnableGuard to your own check (e.g. an admin authorization).
                _.EnableGuard = _ => true;
            });
        // end-snippet
        app.MapFallbackToFile("index.html");

        await app.RunAsync();
    }
}

/// <summary>What the /permissions page needs to render: the grants, and how much deciding they cost.</summary>
public record GrantState(IReadOnlyList<string> Regions, IReadOnlyList<string> Granted, int Lookups);
