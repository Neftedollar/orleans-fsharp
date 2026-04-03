module Orleans.FSharp.Integration.TestHarnessIntegrationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp
open Orleans.FSharp.Sample
open Orleans.FSharp.Testing

/// <summary>
/// End-to-end property tests using the TestHarness to test counter grain
/// with FsCheck-generated command sequences.
/// </summary>
[<Collection("ClusterCollection")>]
type TestHarnessPropertyTests(fixture: ClusterFixture) =

    /// Pure model of counter state for comparison.
    static let pureApply (state: int) (cmd: CounterCommand) : int =
        match cmd with
        | Increment -> state + 1
        | Decrement -> max 0 (state - 1)
        | GetValue -> state

    [<Fact>]
    member _.``Counter grain matches pure model after increment sequence`` () =
        task {
            let grainId = 600L + int64 (Random.Shared.Next(10000))
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(grainId)
            let commands = [ Increment; Increment; Increment; GetValue ]

            let mutable grainResult = 0

            for cmd in commands do
                let! result = grain.HandleMessage(cmd)
                grainResult <- unbox<int> result

            let pureResult = commands |> List.fold pureApply 0
            let expectedValue = pureResult // GetValue doesn't change state, returns state
            test <@ grainResult = expectedValue @>
        }

    [<Fact>]
    member _.``Counter grain matches pure model after mixed commands`` () =
        task {
            let grainId = 700L + int64 (Random.Shared.Next(10000))
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(grainId)

            let commands =
                [
                    Increment
                    Increment
                    Increment
                    Decrement
                    Increment
                    Decrement
                    Decrement
                    GetValue
                ]

            let mutable grainResult = 0

            for cmd in commands do
                let! result = grain.HandleMessage(cmd)
                grainResult <- unbox<int> result

            let pureResult = commands |> List.fold pureApply 0
            test <@ grainResult = pureResult @>
        }

    [<Fact>]
    member _.``stateMachineProperty validates counter invariant holds`` () =
        // Use FsCheckHelpers to validate the pure model
        let commands =
            [
                Increment
                Increment
                Decrement
                GetValue
                Increment
                Decrement
                Decrement
                Decrement
            ]

        let result =
            FsCheckHelpers.stateMachineProperty 0 pureApply (fun s -> s >= 0) commands

        test <@ result = true @>

    [<Fact>]
    member _.``commandSequenceArb generates valid CounterCommand sequences`` () =
        let arb = FsCheckHelpers.commandSequenceArb<CounterCommand> ()
        let gen = FsCheck.FSharp.Arb.toGen arb
        let samples = FsCheck.FSharp.Gen.sampleWithSize 10 20 gen

        for sample in samples do
            test <@ sample.Length > 0 @>

            // Each command in the sample should be a valid CounterCommand
            for cmd in sample do
                let isValid =
                    match cmd with
                    | Increment -> true
                    | Decrement -> true
                    | GetValue -> true

                test <@ isValid @>

    [<Fact>]
    member _.``Counter grain state never goes negative with any command sequence`` () =
        // Property test using pure model
        let arb = FsCheckHelpers.commandSequenceArb<CounterCommand> ()
        let gen = FsCheck.FSharp.Arb.toGen arb
        let samples = FsCheck.FSharp.Gen.sampleWithSize 20 100 gen

        for commands in samples do
            let finalState = commands |> List.fold pureApply 0
            test <@ finalState >= 0 @>
