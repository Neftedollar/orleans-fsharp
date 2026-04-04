module Orleans.FSharp.Tests.ReminderExceptionTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Runtime
open Orleans.FSharp

/// <summary>
/// Tests verifying that reminder handler exceptions are handled gracefully.
/// A throwing handler should not prevent future reminder ticks from being processed.
/// </summary>

[<Fact>]
let ``Reminder handler that throws does not corrupt state`` () =
    let mutable callCount = 0

    let handler (state: int) (_name: string) (_status: TickStatus) =
        task {
            callCount <- callCount + 1

            if callCount = 2 then
                return raise (InvalidOperationException("Test exception"))
            else
                return state + 1
        }

    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onReminder "FaultyReminder" handler
        }

    let registeredHandler = def.ReminderHandlers.["FaultyReminder"]
    let dummyStatus = TickStatus()

    // First call succeeds
    let result1 = registeredHandler 0 "FaultyReminder" dummyStatus |> fun t -> t.Result
    test <@ result1 = 1 @>

    // Second call throws -- caller (the grain runtime) catches this
    let ex =
        Assert.ThrowsAsync<InvalidOperationException>(fun () ->
            registeredHandler 1 "FaultyReminder" dummyStatus :> Task)

    test <@ ex.Result.Message = "Test exception" @>

    // Third call succeeds again -- handler is still usable
    let result3 = registeredHandler 1 "FaultyReminder" dummyStatus |> fun t -> t.Result
    test <@ result3 = 2 @>

[<Fact>]
let ``GrainDefinition with throwing reminder handler has handler registered`` () =
    let handler (_state: int) (_name: string) (_status: TickStatus) =
        task { return raise (InvalidOperationException("Always throws")) }

    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onReminder "ThrowingReminder" handler
        }

    test <@ def.ReminderHandlers |> Map.containsKey "ThrowingReminder" @>
    test <@ def.ReminderHandlers |> Map.count = 1 @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``reminder handler returning state + delta works for any initial state and delta`` (initial: int) (delta: int) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            onReminder "AddDelta" (fun state _n _status -> task { return state + delta })
        }

    let handler = def.ReminderHandlers.["AddDelta"]
    let result = handler initial "AddDelta" (TickStatus()) |> fun t -> t.Result
    result = initial + delta

[<Property>]
let ``multiple reminder handlers registered with distinct names all survive`` (n: PositiveInt) =
    let count = min n.Get 5
    let names = List.init count (fun i -> $"Reminder{i}")

    let mutable def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
        }

    for name in names do
        def <- { def with ReminderHandlers = def.ReminderHandlers |> Map.add name (fun s _n _t -> task { return s }) }

    def.ReminderHandlers |> Map.count = count
