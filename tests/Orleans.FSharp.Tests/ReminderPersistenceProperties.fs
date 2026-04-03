module Orleans.FSharp.Tests.ReminderPersistenceProperties

open System
open System.Threading.Tasks
open Xunit
open FsCheck
open FsCheck.Xunit
open Swensen.Unquote
open Orleans.Runtime
open Orleans.FSharp

/// <summary>
/// FsCheck property tests verifying that reminder handlers registered in GrainDefinition
/// preserve the invariant that state is correctly updated through any sequence of reminder firings.
/// </summary>

/// <summary>
/// A command representing a reminder tick for property testing.
/// </summary>
type ReminderTick =
    | Tick of reminderName: string
    | NoOp

/// <summary>
/// Property: for any sequence of reminder ticks, the state accumulated by reminder handlers
/// equals the expected fold result.
/// </summary>
[<Property(MaxTest = 50)>]
let ``Reminder handler fold is consistent with sequential application`` (ticks: bool list) =
    // Build a grain definition with a single reminder that increments state
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })

            onReminder "TestReminder" (fun state _name _status ->
                task { return state + 1 })
        }

    let handler = def.ReminderHandlers.["TestReminder"]
    let dummyStatus = TickStatus()

    // Apply the handler for each true tick
    let mutable state = def.DefaultState.Value

    for shouldTick in ticks do
        if shouldTick then
            let result = handler state "TestReminder" dummyStatus
            state <- result.Result

    let expectedCount = ticks |> List.filter id |> List.length
    state = expectedCount

/// <summary>
/// Property: registering multiple reminder handlers and firing them in any order
/// produces the expected aggregate state.
/// </summary>
[<Property(MaxTest = 50)>]
let ``Multiple reminder handlers compose correctly`` (ticksA: bool list) (ticksB: bool list) =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })

            onReminder "ReminderA" (fun state _name _status ->
                task { return state + 1 })

            onReminder "ReminderB" (fun state _name _status ->
                task { return state + 10 })
        }

    let handlerA = def.ReminderHandlers.["ReminderA"]
    let handlerB = def.ReminderHandlers.["ReminderB"]
    let dummyStatus = TickStatus()

    let mutable state = def.DefaultState.Value

    // Interleave the two tick sequences
    let maxLen = max ticksA.Length ticksB.Length
    let paddedA = ticksA @ List.replicate (maxLen - ticksA.Length) false
    let paddedB = ticksB @ List.replicate (maxLen - ticksB.Length) false

    for i in 0 .. maxLen - 1 do
        if paddedA.[i] then
            state <- (handlerA state "ReminderA" dummyStatus).Result

        if paddedB.[i] then
            state <- (handlerB state "ReminderB" dummyStatus).Result

    let expectedA = ticksA |> List.filter id |> List.length
    let expectedB = ticksB |> List.filter id |> List.length
    state = expectedA + (expectedB * 10)
