namespace Sample.FSharp.Tests

open System
open System.Threading.Tasks
open EfLocalDb
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Sample.Model
open Scry
open Scry.Generated

/// Every [Attachment] needs a policy before the server will start; nothing here fetches one, so both
/// allow. One type per entity, since a policy answering for two is refused as ambiguous.
type HandbookPolicy() =
    interface IAttachmentPolicy<Department> with
        member _.Authorize _ = true

type PhotoPolicy() =
    interface IAttachmentPolicy<Employee> with
        member _.Authorize _ = true

// begin-snippet: fsharpServer
/// The sample server hosted in-process, as Sample.Tests hosts it, against a LocalDB database cloned
/// from a seeded template. Registered from F# to show that the server side reads the same either way.
type ScryServer private (app: WebApplication, database: SqlDatabase<SampleContext>) =
    // A LocalDB instance of its own. The instance is named after the context by default, which
    // Sample.Tests already uses, and the two projects run in parallel under one dotnet test: both
    // rebuilding the one template at once deadlocks inside SQL Server.
    static let sqlInstance =
        new SqlInstance<SampleContext>(
            constructInstance = (fun builder -> new SampleContext(builder.Options)),
            buildTemplate =
                (fun context ->
                    SampleContext.Initialize context
                    Task.CompletedTask),
            storage = Storage.FromSuffix<SampleContext> "FSharp")

    /// A suffix gives a fixture that writes a database of its own, so the fixtures that snapshot the
    /// seed never see what it changed.
    static member StartAsync(?databaseSuffix: string) =
        task {
            let! database = sqlInstance.Build(databaseSuffix = defaultArg databaseSuffix null)
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseTestServer() |> ignore

            // The interceptor reports what this context saves, which is what makes a live query hear
            // of it. Resolved rather than constructed, so it reports to the place the server listens.
            builder.Services.AddDbContext<SampleContext>(fun (services: IServiceProvider) (options: DbContextOptionsBuilder) ->
                options
                    .UseSqlServer(database.ConnectionString)
                    .AddInterceptors(services.GetRequiredService<ScryChangeInterceptor>())
                |> ignore)
            |> ignore

            builder.Services.AddScry<SampleContext>(fun options ->
                options.AddPocoSource(fun _ -> Holiday.Seed())
                options.AddAttachmentPolicy<Department, HandbookPolicy>()
                options.AddAttachmentPolicy<Employee, PhotoPolicy>()

                // Live queries are off until a server says how many it will hold open.
                options.MaxSubscriptions <- 10
                options.SubscriptionThrottle <- TimeSpan.Zero)
            |> ignore

            let app = builder.Build()
            app.MapScry "/api/query" |> ignore
            do! app.StartAsync()
            return new ScryServer(app, database)
        }

    /// The generated entry point over an HTTP client into the hosted server.
    member _.Query = ScryQuery(ScryClient.ForHttp(app.GetTestClient(), "/api/query"))

    /// Renames an employee through the server's own context, as the application would.
    member _.Rename(name: string, renamed: string) : Task =
        task {
            use scope = app.Services.CreateScope()
            let context = scope.ServiceProvider.GetRequiredService<SampleContext>()
            // Named explicitly: Scry's terminals and EF's are both in scope here, and they are not the
            // same method — this one has to run against the database.
            let! employee =
                EntityFrameworkQueryableExtensions.FirstAsync(context.Employees, (fun employee -> employee.Name = name))
            employee.Name <- renamed
            let! _ = context.SaveChangesAsync()
            return ()
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    do! app.DisposeAsync()
                    do! database.DisposeAsync()
                }
                :> Task)
// end-snippet
