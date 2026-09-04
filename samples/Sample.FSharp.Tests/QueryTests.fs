namespace Sample.FSharp.Tests

open System
open System.Linq
open System.Text.Json
open System.Threading.Tasks
open NUnit.Framework
open Sample.FSharp
open Scry
open VerifyNUnit

[<TestFixture>]
type QueryTests() =
    let mutable server: ScryServer = Unchecked.defaultof<_>

    /// The request as it would travel, for a snapshot of what F# put on the wire.
    let wire (source: IQueryable<'T>) =
        JsonSerializer.Serialize(source.ToScryRequest(), ScryJson.Options)

    [<OneTimeSetUp>]
    member _.Start() : Task =
        task {
            let! started = ScryServer.StartAsync()
            server <- started
        }

    [<OneTimeTearDown>]
    member _.Stop() : Task =
        // Null when the start failed, and a second exception there would hide the first.
        if isNull (box server) then
            Task.CompletedTask
        else
            (server :> IAsyncDisposable).DisposeAsync().AsTask()

    [<Test>]
    member _.ActiveEmployeesRequest() : Task =
        Verifier.Verify(wire (Queries.activeEmployees server.Query)).ToTask()

    [<Test>]
    member _.ActiveEmployees() : Task =
        task {
            let! rows = Queries.activeEmployeesAsync server.Query
            do! Verifier.Verify(rows).ToTask() :> Task
        }

    // The fields are written Headcount then Department and declared the other way round, which is
    // the shape the F# compiler binds each field to a variable for. One Select reaches the server.
    [<Test>]
    member _.HeadcountRequest() : Task =
        Verifier.Verify(wire (Queries.headcount server.Query)).ToTask()

    [<Test>]
    member _.Headcount() : Task =
        task {
            let! rows = (Queries.headcount server.Query).ToListAsync()
            do! Verifier.Verify(rows).ToTask() :> Task
        }

    [<Test>]
    member _.ReportsTo() : Task =
        task {
            let! alice = server.Query.Employee.FirstAsync(fun e -> e.Name = "Alice")
            let! rows = (Queries.reportsTo server.Query alice.Id 10).ToListAsync()
            do! Verifier.Verify(rows).ToTask() :> Task
        }

    [<Test>]
    member _.NamedLike() : Task =
        task {
            let! rows = (Queries.namedLike server.Query "ar").ToListAsync()
            do! Verifier.Verify(rows).ToTask() :> Task
        }

    [<Test>]
    member _.ActiveCount() : Task =
        task {
            let! count = Queries.activeCountAsync server.Query
            Assert.That(count, Is.EqualTo 3)
        }
