namespace Sample.FSharp

open System
open System.Threading
open Scry
open Scry.Generated

module Live =

    // begin-snippet: fsharpLiveObservable
    /// A live query as an observable, composed with FSharp.Core's own Observable module. There is no
    /// reactive package here: Scry hands over the IObservable that ships with .NET, and F# already
    /// knows what to do with one. Each answer is the whole current result.
    let activeNames (query: ScryQuery) : IObservable<string list> =
        (Queries.activeEmployees query).Live().AsObservable()
        |> Observable.map (fun rows -> rows |> Seq.map _.Name |> List.ofSeq)
    // end-snippet

    // begin-snippet: fsharpLiveStream
    /// The same live query read as a stream: an IAsyncEnumerable, pulled one answer at a time until
    /// the token is cancelled, which is also what tells the server the subscription is over.
    let watch (query: ScryQuery) (onAnswer: EmployeeRow list -> unit) (cancel: CancellationToken) =
        task {
            use answers =
                (Queries.activeEmployees query).Live().GetAsyncEnumerator cancel

            let mutable more = true

            while more do
                let! next = answers.MoveNextAsync()
                more <- next

                if more then
                    onAnswer (List.ofSeq answers.Current)
        }
    // end-snippet

    /// A terminal that folds the rows away is as live as one that returns them.
    let activeCount (query: ScryQuery) =
        query.Employee.LiveCount(_.Active)
