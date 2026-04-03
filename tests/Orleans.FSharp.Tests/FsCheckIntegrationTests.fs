module Orleans.FSharp.Tests.FsCheckIntegrationTests

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Testing

// --- Test types for FsCheck integration ---

/// Simple command type for testing.
type SimpleCommand =
    | Add
    | Remove
    | Reset

/// Apply a SimpleCommand to a state (count).
let applySimple (state: int) (cmd: SimpleCommand) : int =
    match cmd with
    | Add -> state + 1
    | Remove -> max 0 (state - 1)
    | Reset -> 0

/// Intentionally broken apply that can go negative.
let applyBroken (state: int) (cmd: SimpleCommand) : int =
    match cmd with
    | Add -> state + 1
    | Remove -> state - 1 // Bug: allows negative
    | Reset -> 0

// --- commandSequenceArb tests ---

[<Fact>]
let ``commandSequenceArb generates non-empty lists`` () =
    let arb = FsCheckHelpers.commandSequenceArb<SimpleCommand> ()
    let gen = FsCheck.FSharp.Arb.toGen arb
    let samples = FsCheck.FSharp.Gen.sampleWithSize 10 20 gen

    for sample in samples do
        test <@ sample.Length > 0 @>

[<Fact>]
let ``commandSequenceArb generates lists of valid commands`` () =
    let arb = FsCheckHelpers.commandSequenceArb<SimpleCommand> ()
    let gen = FsCheck.FSharp.Arb.toGen arb
    let samples = FsCheck.FSharp.Gen.sampleWithSize 10 20 gen

    for sample in samples do
        for cmd in sample do
            // Verify each command is a valid SimpleCommand variant
            let isValid =
                match cmd with
                | Add -> true
                | Remove -> true
                | Reset -> true

            test <@ isValid @>

[<Fact>]
let ``commandSequenceArb generates varying lengths`` () =
    let arb = FsCheckHelpers.commandSequenceArb<SimpleCommand> ()
    let gen = FsCheck.FSharp.Arb.toGen arb
    let samples = FsCheck.FSharp.Gen.sampleWithSize 50 100 gen
    let lengths = samples |> Array.map (fun l -> l.Length) |> Array.distinct

    // Should generate at least a few different lengths
    test <@ lengths.Length > 1 @>

// --- stateMachineProperty tests ---

[<Property>]
let ``stateMachineProperty holds for correct implementation`` (commands: SimpleCommand list) =
    commands.Length = 0
    || FsCheckHelpers.stateMachineProperty 0 applySimple (fun s -> s >= 0) commands

[<Fact>]
let ``stateMachineProperty detects broken invariant`` () =
    // The broken implementation allows negative state
    let violatingCommands = [ Remove; Remove; Remove ]

    let result =
        FsCheckHelpers.stateMachineProperty 0 applyBroken (fun s -> s >= 0) violatingCommands

    test <@ result = false @>

[<Fact>]
let ``stateMachineProperty returns true for valid sequences`` () =
    let result =
        FsCheckHelpers.stateMachineProperty 0 applySimple (fun s -> s >= 0) [ Add; Add; Remove; Reset; Add ]

    test <@ result = true @>

[<Fact>]
let ``stateMachineProperty works with empty command list`` () =
    let result =
        FsCheckHelpers.stateMachineProperty 0 applySimple (fun s -> s >= 0) []

    test <@ result = true @>

[<Property>]
let ``stateMachineProperty always holds for non-negative counter`` (commands: SimpleCommand list) =
    commands.Length = 0
    || FsCheckHelpers.stateMachineProperty 0 applySimple (fun s -> s >= 0) commands
