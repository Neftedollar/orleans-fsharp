module Orleans.FSharp.Tests.DeclarativeTimerTests

open System
open System.Reflection
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

// --- GrainDefinition.TimerHandlers field tests ---

[<Fact>]
let ``GrainDefinition has TimerHandlers field`` () =
    let defType = typeof<GrainDefinition<int, string>>

    let field =
        defType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
        |> Array.tryFind (fun p -> p.Name = "TimerHandlers")

    test <@ field.IsSome @>

[<Fact>]
let ``TimerHandlers defaults to empty map`` () =
    let def: GrainDefinition<int, string> =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.TimerHandlers |> Map.isEmpty @>

[<Fact>]
let ``onTimer CE keyword adds handler to GrainDefinition`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onTimer "cleanup" (TimeSpan.FromMinutes 5.) (TimeSpan.FromMinutes 5.) (fun state -> task { return state })
        }

    test <@ def.TimerHandlers |> Map.containsKey "cleanup" @>

[<Fact>]
let ``multiple onTimer calls register multiple handlers`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })

            onTimer "cleanup" (TimeSpan.FromMinutes 5.) (TimeSpan.FromMinutes 5.) (fun state ->
                task { return state })

            onTimer "heartbeat" (TimeSpan.FromSeconds 30.) (TimeSpan.FromSeconds 30.) (fun state ->
                task { return state })
        }

    test <@ def.TimerHandlers |> Map.count = 2 @>
    test <@ def.TimerHandlers |> Map.containsKey "cleanup" @>
    test <@ def.TimerHandlers |> Map.containsKey "heartbeat" @>

[<Fact>]
let ``onTimer stores correct dueTime and period`` () =
    let dueTime = TimeSpan.FromMinutes 1.
    let period = TimeSpan.FromMinutes 5.

    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onTimer "myTimer" dueTime period (fun state -> task { return state })
        }

    let (storedDueTime, storedPeriod, _handler) = def.TimerHandlers.["myTimer"]
    test <@ storedDueTime = dueTime @>
    test <@ storedPeriod = period @>

[<Fact>]
let ``onTimer handler has correct signature and executes`` () =
    let mutable handlerCalled = false

    let def =
        grain {
            defaultState 42
            handle (fun state _msg -> task { return state, box state })

            onTimer "test" (TimeSpan.FromSeconds 1.) (TimeSpan.FromSeconds 1.) (fun state ->
                task {
                    handlerCalled <- true
                    return state + 1
                })
        }

    let (_dueTime, _period, handler) = def.TimerHandlers.["test"]
    let result = handler 42 |> fun t -> t.Result
    test <@ handlerCalled @>
    test <@ result = 43 @>

[<Fact>]
let ``onTimer composes with other CE keywords`` () =
    let def =
        grain {
            defaultState "idle"

            handle (fun state (_msg: int) ->
                task { return state, box state })

            persist "Default"
            onActivate (fun s -> task { return s })

            onTimer "check" (TimeSpan.FromSeconds 10.) (TimeSpan.FromSeconds 10.) (fun state ->
                task { return state })
        }

    test <@ def.DefaultState = Some "idle" @>
    test <@ def.Handler |> Option.isSome @>
    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.OnActivate |> Option.isSome @>
    test <@ def.TimerHandlers |> Map.containsKey "check" @>

[<Fact>]
let ``onTimer later registration with same name replaces earlier`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })

            onTimer "dup" (TimeSpan.FromSeconds 1.) (TimeSpan.FromSeconds 1.) (fun state ->
                task { return state + 1 })

            onTimer "dup" (TimeSpan.FromSeconds 5.) (TimeSpan.FromSeconds 5.) (fun state ->
                task { return state + 100 })
        }

    test <@ def.TimerHandlers |> Map.count = 1 @>
    let (_dueTime, _period, handler) = def.TimerHandlers.["dup"]
    let result = handler 0 |> fun t -> t.Result
    // The second handler should have replaced the first
    test <@ result = 100 @>

[<Fact>]
let ``onTimer with zero dueTime stores correctly`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onTimer "immediate" TimeSpan.Zero (TimeSpan.FromMinutes 1.) (fun state -> task { return state })
        }

    let (storedDueTime, storedPeriod, _handler) = def.TimerHandlers.["immediate"]
    test <@ storedDueTime = TimeSpan.Zero @>
    test <@ storedPeriod = TimeSpan.FromMinutes 1. @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``onTimer stores correct name for any non-whitespace timer name`` (name: NonNull<string>) =
    let due = TimeSpan.FromSeconds 1.
    let period = TimeSpan.FromSeconds 1.
    String.IsNullOrWhiteSpace name.Get
    || (let def =
            grain {
                defaultState 0
                handle (fun state (_msg: string) -> task { return state, box state })
                onTimer name.Get due period (fun state -> task { return state })
            }
        def.TimerHandlers |> Map.containsKey name.Get)

[<Property>]
let ``onTimer handler increments state correctly for any initial state`` (initial: int) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            onTimer "inc" TimeSpan.Zero (TimeSpan.FromSeconds 1.) (fun state ->
                task { return state + 1 })
        }
    let (_, _, handler) = def.TimerHandlers.["inc"]
    handler initial |> _.GetAwaiter().GetResult() = initial + 1
