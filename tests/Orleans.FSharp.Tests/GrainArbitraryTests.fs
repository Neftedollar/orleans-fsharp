module Orleans.FSharp.Tests.GrainArbitraryTests

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.FSharp
open Orleans.FSharp.Testing

// --- Test DU types ---

/// Simple counter state (same shape as the Sample project's CounterState).
type CounterState =
    | Zero
    | Count of int

/// Order state with string payloads.
type OrderState =
    | Idle
    | Processing of description: string
    | Completed of summary: string
    | Failed of reason: string

/// Commands for the counter.
type CounterCommand =
    | Increment
    | Decrement
    | GetValue

/// Complex nested DU for stress-testing the generator.
type Address =
    { Street: string
      City: string
      Zip: int }

type ContactInfo =
    | Email of address: string
    | Phone of number: string
    | Mail of address: Address

type CustomerStatus =
    | Active
    | Suspended of reason: string
    | Premium of level: int * since: string

type ComplexState =
    | Empty
    | Simple of name: string
    | WithOption of value: int option
    | WithList of items: string list
    | Nested of contact: ContactInfo
    | MultiField of name: string * age: int * active: bool
    | WithCustomer of status: CustomerStatus

// --- forState tests ---

[<Fact>]
let ``forState generates CounterState values covering all cases`` () =
    let arb = GrainArbitrary.forState<CounterState> ()
    let gen = Arb.toGen arb
    let samples = Gen.sampleWithSize 10 100 gen

    let hasZero = samples |> Array.exists (fun s -> match s with Zero -> true | _ -> false)
    let hasCount = samples |> Array.exists (fun s -> match s with Count _ -> true | _ -> false)

    test <@ hasZero @>
    test <@ hasCount @>

[<Fact>]
let ``forState generates OrderState values covering all cases`` () =
    let arb = GrainArbitrary.forState<OrderState> ()
    let gen = Arb.toGen arb
    let samples = Gen.sampleWithSize 10 200 gen

    let hasIdle = samples |> Array.exists (fun s -> match s with Idle -> true | _ -> false)
    let hasProcessing = samples |> Array.exists (fun s -> match s with Processing _ -> true | _ -> false)
    let hasCompleted = samples |> Array.exists (fun s -> match s with Completed _ -> true | _ -> false)
    let hasFailed = samples |> Array.exists (fun s -> match s with Failed _ -> true | _ -> false)

    test <@ hasIdle @>
    test <@ hasProcessing @>
    test <@ hasCompleted @>
    test <@ hasFailed @>

[<Fact>]
let ``forState generates valid CounterState values`` () =
    let arb = GrainArbitrary.forState<CounterState> ()
    let gen = Arb.toGen arb
    let samples = Gen.sampleWithSize 20 50 gen

    for sample in samples do
        let isValid =
            match sample with
            | Zero -> true
            | Count _ -> true

        test <@ isValid @>

[<Fact>]
let ``forState generates complex nested DU values`` () =
    let arb = GrainArbitrary.forState<ComplexState> ()
    let gen = Arb.toGen arb
    let samples = Gen.sampleWithSize 10 200 gen

    let hasEmpty = samples |> Array.exists (fun s -> match s with Empty -> true | _ -> false)
    let hasSimple = samples |> Array.exists (fun s -> match s with Simple _ -> true | _ -> false)
    let hasNested = samples |> Array.exists (fun s -> match s with Nested _ -> true | _ -> false)
    let hasMultiField = samples |> Array.exists (fun s -> match s with MultiField _ -> true | _ -> false)

    test <@ hasEmpty @>
    test <@ hasSimple @>
    test <@ hasNested @>
    test <@ hasMultiField @>

[<Fact>]
let ``forState with nested DUs generates nested ContactInfo cases`` () =
    let arb = GrainArbitrary.forState<ContactInfo> ()
    let gen = Arb.toGen arb
    let samples = Gen.sampleWithSize 10 200 gen

    let hasEmail = samples |> Array.exists (fun s -> match s with Email _ -> true | _ -> false)
    let hasPhone = samples |> Array.exists (fun s -> match s with Phone _ -> true | _ -> false)
    let hasMail = samples |> Array.exists (fun s -> match s with Mail _ -> true | _ -> false)

    test <@ hasEmail @>
    test <@ hasPhone @>
    test <@ hasMail @>

[<Fact>]
let ``forState generates CustomerStatus with multi-field case`` () =
    let arb = GrainArbitrary.forState<CustomerStatus> ()
    let gen = Arb.toGen arb
    let samples = Gen.sampleWithSize 10 200 gen

    let hasActive = samples |> Array.exists (fun s -> match s with Active -> true | _ -> false)
    let hasSuspended = samples |> Array.exists (fun s -> match s with Suspended _ -> true | _ -> false)
    let hasPremium = samples |> Array.exists (fun s -> match s with Premium _ -> true | _ -> false)

    test <@ hasActive @>
    test <@ hasSuspended @>
    test <@ hasPremium @>

// --- forCommands tests ---

[<Fact>]
let ``forCommands generates non-empty command lists`` () =
    let arb = GrainArbitrary.forCommands<CounterCommand> ()
    let gen = Arb.toGen arb
    let samples = Gen.sampleWithSize 10 50 gen

    for sample in samples do
        test <@ sample.Length > 0 @>

[<Fact>]
let ``forCommands generates all command cases`` () =
    let arb = GrainArbitrary.forCommands<CounterCommand> ()
    let gen = Arb.toGen arb
    let samples = Gen.sampleWithSize 10 50 gen

    let allCmds = samples |> Array.collect List.toArray

    let hasIncrement = allCmds |> Array.exists (fun c -> match c with Increment -> true | _ -> false)
    let hasDecrement = allCmds |> Array.exists (fun c -> match c with Decrement -> true | _ -> false)
    let hasGetValue = allCmds |> Array.exists (fun c -> match c with GetValue -> true | _ -> false)

    test <@ hasIncrement @>
    test <@ hasDecrement @>
    test <@ hasGetValue @>

[<Fact>]
let ``forCommands generates varying list lengths`` () =
    let arb = GrainArbitrary.forCommands<CounterCommand> ()
    let gen = Arb.toGen arb
    let samples = Gen.sampleWithSize 50 100 gen
    let lengths = samples |> Array.map (fun l -> l.Length) |> Array.distinct

    test <@ lengths.Length > 1 @>

// --- Property-based tests using forState ---

[<Fact>]
let ``forState CounterState property: all generated values match expected pattern`` () =
    let arb = GrainArbitrary.forState<CounterState> ()
    let prop = Prop.forAll arb (fun state ->
        match state with
        | Zero -> true
        | Count _ -> true)

    Check.One(Config.QuickThrowOnFailure, prop)

[<Fact>]
let ``forCommands used with stateMachineProperty`` () =
    let arb = GrainArbitrary.forCommands<CounterCommand> ()

    let apply (state: CounterState) (cmd: CounterCommand) : CounterState =
        match state, cmd with
        | Zero, Increment -> Count 1
        | Zero, Decrement -> Zero
        | Zero, GetValue -> Zero
        | Count n, Increment -> Count(n + 1)
        | Count n, Decrement when n > 1 -> Count(n - 1)
        | Count _, Decrement -> Zero
        | Count _, GetValue -> state

    let invariant (state: CounterState) : bool =
        match state with
        | Zero -> true
        | Count n -> n > 0

    let prop = Prop.forAll arb (fun cmds ->
        FsCheckHelpers.stateMachineProperty Zero apply invariant cmds)

    Check.One(Config.QuickThrowOnFailure, prop)
