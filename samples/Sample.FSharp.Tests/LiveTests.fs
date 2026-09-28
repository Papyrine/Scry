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

/// A live query from F#: the same LINQ, kept answered. A database of its own, because these write.
[<NotInParallel>]
type LiveTests() =
    static let mutable server: ScryServer = Unchecked.defaultof<_>

    let patience = TimeSpan.FromSeconds 30.

    [<Before(HookType.Class)>]
    static member Start() : Task =
        task {
            let! started = ScryServer.StartAsync "Live"
            server <- started
        }

    [<After(HookType.Class)>]
    static member Stop() : Task =
        if isNull (box server) then
            Task.CompletedTask
        else
            (server :> IAsyncDisposable).DisposeAsync().AsTask()

    // Composed with FSharp.Core's Observable module and nothing else.
    [<Test>]
    member _.AnObservableHearsEachChange() : Task =
        task {
            let answers = ResizeArray<string list>()
            use heard = new SemaphoreSlim 0

            use _ =
                Live.activeNames server.Query
                |> Observable.subscribe (fun names ->
                    lock answers (fun () -> answers.Add names)
                    heard.Release() |> ignore)

            let! first = heard.WaitAsync patience
            do! check (Assert.That(first).IsTrue().Because "The first answer never arrived.")
            let before = lock answers (fun () -> answers[0])

            // Written the way a client writes: a command, whose handler's save reaches the live query
            // through the server's change interceptor.
            let! rows = server.Query.Employee.Where(fun e -> e.Name = before.Head).Select(fun e -> {| Id = e.Id |}).ToListAsync()
            let! renamed = Commands.rename server.Query rows[0].Id "Aaron Renamed"
            do! check (Assert.That(renamed.Status).IsEqualTo ScryCommandStatus.Completed)

            let! second = heard.WaitAsync patience
            do! check (Assert.That(second).IsTrue().Because "The change never arrived.")
            let after = lock answers (fun () -> answers[1])
            do! check (Assert.That(List.contains "Aaron Renamed" after).IsTrue().Because $"%A{after}")
            do! check (Assert.That(List.contains before.Head after).IsFalse().Because $"%A{after}")
        }

    [<Test>]
    member _.AStreamIsReadUntilCancelled() : Task =
        task {
            use leaving = new CancellationTokenSource()
            use heard = new SemaphoreSlim 0

            let watching =
                Live.watch server.Query (fun _ -> heard.Release() |> ignore) leaving.Token

            let! first = heard.WaitAsync patience
            do! check (Assert.That(first).IsTrue().Because "The first answer never arrived.")

            leaving.Cancel()

            let! _ = Assert.ThrowsAsync<OperationCanceledException>(Func<Task>(fun () -> watching :> Task))
            ()
        }
