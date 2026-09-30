namespace Sample.FSharp.Tests

open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open TUnit.Assertions.Core
open TUnit.Core
open VerifyTests

/// Awaits a TUnit assertion from inside a task.
[<AutoOpen>]
module Check =
    let check (assertion: Assertion<'T>) : Task = assertion.AssertAsync() :> Task

type Setup() =

    /// The stamp is a hash over the whole queryable surface, so a snapshot carrying it would move
    /// whenever the model gained a member — the same reason Sample.Tests scrubs it.
    static let stamp = Regex("(\"stamp\":\\s*)\"[^\"]*\"", RegexOptions.Compiled)

    static let scrubStamps (builder: StringBuilder) =
        let scrubbed = stamp.Replace(builder.ToString(), "$1\"{scrubbed stamp}\"")
        builder.Clear().Append scrubbed |> ignore

    [<Before(HookType.TestSession)>]
    static member Init() =
        VerifierSettings.AddScrubber scrubStamps
