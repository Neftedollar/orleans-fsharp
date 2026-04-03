module MyApp.Tests.CounterTests

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open MyApp.Grains

/// Pure state transition function for property-based testing.
let transition (state: CounterState) (cmd: CounterCommand) : CounterState =
    match state, cmd with
    | Zero, Increment -> Count 1
    | Zero, Decrement -> Zero
    | Zero, GetValue -> Zero
    | Count n, Increment -> Count(n + 1)
    | Count n, Decrement when n > 1 -> Count(n - 1)
    | Count _, Decrement -> Zero
    | Count _, GetValue -> state

/// Extract the integer value from a counter state.
let stateValue (state: CounterState) : int =
    match state with
    | Zero -> 0
    | Count n -> n

/// Check that a counter state is valid.
let isValidState (state: CounterState) : bool =
    match state with
    | Zero -> true
    | Count n -> n > 0

/// Apply a sequence of commands to an initial state.
let applyCommands (commands: CounterCommand list) : CounterState =
    commands |> List.fold transition Zero

// --- Property-based tests ---

[<Property>]
let ``arbitrary command sequences always produce valid state`` (commands: CounterCommand list) =
    let finalState = applyCommands commands
    isValidState finalState

[<Property>]
let ``state value is always non-negative`` (commands: CounterCommand list) =
    let finalState = applyCommands commands
    stateValue finalState >= 0

[<Property>]
let ``increment then decrement is identity for Count n`` (n: PositiveInt) =
    let state = Count n.Get
    let afterIncDec = state |> (fun s -> transition s Increment) |> (fun s -> transition s Decrement)
    afterIncDec = state

[<Property>]
let ``GetValue does not change state`` (commands: CounterCommand list) =
    let state = applyCommands commands
    let stateAfterGet = transition state GetValue
    state = stateAfterGet

[<Property>]
let ``increment always increases value by 1`` (commands: CounterCommand list) =
    let state = applyCommands commands
    let before = stateValue state
    let after = stateValue (transition state Increment)
    after = before + 1

[<Property>]
let ``decrement never goes below zero`` (commands: CounterCommand list) =
    let state = applyCommands commands
    let after = transition state Decrement
    stateValue after >= 0

// --- Unit tests ---

[<Fact>]
let ``Zero incremented once gives Count 1`` () =
    let result = transition Zero Increment
    test <@ result = Count 1 @>

[<Fact>]
let ``Zero decremented stays Zero`` () =
    let result = transition Zero Decrement
    test <@ result = Zero @>

[<Fact>]
let ``Count 1 decremented gives Zero`` () =
    let result = transition (Count 1) Decrement
    test <@ result = Zero @>

[<Fact>]
let ``grain definition has default state Zero`` () =
    test <@ CounterGrainDef.counter.DefaultState = Some Zero @>

[<Fact>]
let ``grain definition has persistence configured`` () =
    test <@ CounterGrainDef.counter.PersistenceName = Some "Default" @>
