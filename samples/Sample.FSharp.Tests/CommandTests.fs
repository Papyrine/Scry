namespace Sample.FSharp.Tests

open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open TUnit.Assertions
open TUnit.Assertions.Extensions
open TUnit.Core
open Scry
open Scry.Generated
open Sample.FSharp

/// Commands from F#, against the sample's own handlers. A database of its own, because these write.
[<NotInParallel>]
type CommandTests() =
    static let mutable server: ScryServer = Unchecked.defaultof<_>

    let patience = TimeSpan.FromSeconds 30.

    [<Before(HookType.Class)>]
    static member Start() : Task =
        task {
            let! started = ScryServer.StartAsync "Commands"
            server <- started
        }

    [<After(HookType.Class)>]
    static member Stop() : Task =
        if isNull (box server) then
            Task.CompletedTask
        else
            (server :> IAsyncDisposable).DisposeAsync().AsTask()

    member private _.IdOf(name: string) : Task<int> =
        task {
            let! rows = server.Query.Employee.Where(fun e -> e.Name = name).Select(fun e -> {| Id = e.Id |}).ToListAsync()
            return rows[0].Id
        }

    member private _.NameOf(id: int) : Task<string list> =
        task {
            let! rows = server.Query.Employee.Where(fun e -> e.Id = id).Select(fun e -> {| Name = e.Name |}).ToListAsync()
            return rows |> Seq.map _.Name |> List.ofSeq
        }

    [<Test>]
    member this.RenameCompletesInline() : Task =
        task {
            let! carol = this.IdOf "Carol"

            let! outcome = Commands.rename server.Query carol "Caroline"

            do! check (Assert.That(outcome.Status).IsEqualTo ScryCommandStatus.Completed)
            let! renamed = this.NameOf carol
            do! check (Assert.That((renamed = [ "Caroline" ])).IsTrue().Because $"%A{renamed}")
        }

    [<Test>]
    member this.CreateAnswersWithTheTypedResult() : Task =
        task {
            let! id = Commands.hire server.Query "Dana" 1

            let! hired = this.NameOf id
            do! check (Assert.That((hired = [ "Dana" ])).IsTrue().Because $"%A{hired}")
        }

    // The rename reaches the live query through the server's change interceptor: nothing about the
    // command itself is sent to it.
    [<Test>]
    member this.ALiveQuerySeesTheRename() : Task =
        task {
            let! aaron = this.IdOf "Aaron"
            let client = ScryClient.ForHttp(server.Http, "/api/query")
            let query = ScryQuery(client)
            let answers = ResizeArray<string list>()
            use heard = new SemaphoreSlim 0

            use subscription =
                query.Employee.Where(fun e -> e.Id = aaron).Select(fun e -> {| Name = e.Name |}).Live().AsObservable()
                |> Observable.subscribe (fun rows ->
                    lock answers (fun () -> answers.Add(rows |> Seq.map _.Name |> List.ofSeq))
                    heard.Release() |> ignore)

            let! first = heard.WaitAsync patience
            do! check (Assert.That(first).IsTrue().Because "The first answer never arrived.")

            let! outcome = Commands.rename query aaron "Aaron Commanded"
            do! check (Assert.That(outcome.Status).IsEqualTo ScryCommandStatus.Completed)

            let! second = heard.WaitAsync patience
            do! check (Assert.That(second).IsTrue().Because "The rename never arrived.")
            let last = lock answers (fun () -> answers[answers.Count - 1])
            do! check (Assert.That((last = [ "Aaron Commanded" ])).IsTrue().Because $"%A{last}")

            // The client before the server: a live query left open would hold the server's shutdown.
            subscription.Dispose()
            do! client.DisposeAsync()
        }
