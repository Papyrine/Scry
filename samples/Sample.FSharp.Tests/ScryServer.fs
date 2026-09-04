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

    static member StartAsync() =
        task {
            let! database = sqlInstance.Build()
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseTestServer() |> ignore

            builder.Services.AddDbContext<SampleContext>(fun (options: DbContextOptionsBuilder) ->
                options.UseSqlServer database.ConnectionString |> ignore)
            |> ignore

            builder.Services.AddScry<SampleContext>(fun options ->
                options.AddPocoSource(fun _ -> Holiday.Seed())
                options.AddAttachmentPolicy<Department, HandbookPolicy>()
                options.AddAttachmentPolicy<Employee, PhotoPolicy>())
            |> ignore

            let app = builder.Build()
            app.MapScry "/api/query" |> ignore
            do! app.StartAsync()
            return new ScryServer(app, database)
        }

    /// The generated entry point over an HTTP client into the hosted server.
    member _.Query = ScryQuery(ScryClient.ForHttp(app.GetTestClient(), "/api/query"))

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    do! app.DisposeAsync()
                    do! database.DisposeAsync()
                }
                :> Task)
// end-snippet
