namespace Sample.FSharp.Tests

open System.Text
open System.Text.RegularExpressions
open NUnit.Framework
open VerifyTests
open VerifyTests.DiffPlex

[<SetUpFixture>]
type Setup() =

    /// The stamp is a hash over the whole queryable surface, so a snapshot carrying it would move
    /// whenever the model gained a member — the same reason Sample.Tests scrubs it.
    static let stamp = Regex("(\"stamp\":\\s*)\"[^\"]*\"", RegexOptions.Compiled)

    static let scrubStamps (builder: StringBuilder) =
        let scrubbed = stamp.Replace(builder.ToString(), "$1\"{scrubbed stamp}\"")
        builder.Clear().Append scrubbed |> ignore

    [<OneTimeSetUp>]
    member _.Init() =
        VerifyDiffPlex.Initialize OutputType.Compact
        VerifierSettings.AddScrubber scrubStamps
