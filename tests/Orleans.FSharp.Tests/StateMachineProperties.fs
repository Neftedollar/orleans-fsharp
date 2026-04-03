module Orleans.FSharp.Tests.StateMachineProperties

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit

/// Counter state: either zero or a positive count.
type CounterState =
    | Zero
    | Count of int

/// Commands that can be applied to the counter.
type CounterCommand =
    | Increment
    | Decrement
    | GetValue

/// Pure state transition function for the counter state machine.
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

/// Check that a counter state is valid (value >= 0, Count always > 0).
let isValidState (state: CounterState) : bool =
    match state with
    | Zero -> true
    | Count n -> n > 0

/// Apply a sequence of commands to an initial state.
let applyCommands (commands: CounterCommand list) : CounterState =
    commands |> List.fold transition Zero

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
