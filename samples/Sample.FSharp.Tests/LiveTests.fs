namespace Sample.FSharp.Tests

open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Sample.FSharp

/// A live query from F#: the same LINQ, kept answered. A database of its own, because these write.
[<TestFixture>]
type LiveTests() =
    let mutable server: ScryServer = Unchecked.defaultof<_>

    let patience = TimeSpan.FromSeconds 30.

    [<OneTimeSetUp>]
    member _.Start() : Task =
        task {
            let! started = ScryServer.StartAsync "Live"
            server <- started
        }

    [<OneTimeTearDown>]
    member _.Stop() : Task =
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
            Assert.That(first, Is.True, "The first answer never arrived.")
            let before = lock answers (fun () -> answers[0])

            do! server.Rename(before.Head, "Aaron Renamed")

            let! second = heard.WaitAsync patience
            Assert.That(second, Is.True, "The change never arrived.")
            let after = lock answers (fun () -> answers[1])
            Assert.That(after, Does.Contain "Aaron Renamed")
            Assert.That(after, Does.Not.Contain before.Head)
        }

    [<Test>]
    member _.AStreamIsReadUntilCancelled() : Task =
        task {
            use leaving = new CancellationTokenSource()
            use heard = new SemaphoreSlim 0

            let watching =
                Live.watch server.Query (fun _ -> heard.Release() |> ignore) leaving.Token

            let! first = heard.WaitAsync patience
            Assert.That(first, Is.True, "The first answer never arrived.")

            leaving.Cancel()

            Assert.CatchAsync<OperationCanceledException>(Func<Task>(fun () -> watching :> Task))
            |> ignore
        }
