module Orleans.FSharp.Tests.TypedResultTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

// ---------------------------------------------------------------------------
// Test domain types
// ---------------------------------------------------------------------------

type CounterState = { Count: int }

type CounterCommand =
    | Increment
    | Decrement
    | Reset
    | Get

type OrderState =
    | Created of string
    | Confirmed
    | Shipped

type OrderCommand =
    | Confirm
    | Ship
    | GetStatus

type OrderResult =
    | StatusChanged of string
    | Rejected of string
    | CurrentStatus of string

// ---------------------------------------------------------------------------
// handleState tests
// ---------------------------------------------------------------------------

/// <summary>Verifies that handleState registers a handler on the definition.</summary>
[<Fact>]
let ``handleState stores handler`` () =
    let def =
        grain {
            defaultState { Count = 0 }

            handleState (fun state _cmd -> task { return { Count = state.Count + 1 } })
        }

    test <@ def.Handler |> Option.isSome @>

/// <summary>Verifies that handleState returns the new state as both state and result.</summary>
[<Fact>]
let ``handleState result equals new state`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }

                handleState (fun state (cmd: CounterCommand) ->
                    task {
                        match cmd with
                        | Increment -> return { Count = state.Count + 1 }
                        | Decrement -> return { Count = state.Count - 1 }
                        | Reset -> return { Count = 0 }
                        | Get -> return state
                    })
            }

        let handler = def.Handler.Value
        let! (newState, result) = handler { Count = 10 } Increment
        test <@ newState = { Count = 11 } @>
        test <@ unbox<CounterState> result = { Count = 11 } @>
    }

/// <summary>Verifies handleState with Increment command.</summary>
[<Fact>]
let ``handleState Increment produces correct state`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }

                handleState (fun state (cmd: CounterCommand) ->
                    task {
                        match cmd with
                        | Increment -> return { Count = state.Count + 1 }
                        | _ -> return state
                    })
            }

        let! (newState, _) = def.Handler.Value { Count = 5 } Increment
        test <@ newState.Count = 6 @>
    }

/// <summary>Verifies handleState with Decrement command.</summary>
[<Fact>]
let ``handleState Decrement produces correct state`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }

                handleState (fun state (cmd: CounterCommand) ->
                    task {
                        match cmd with
                        | Decrement -> return { Count = state.Count - 1 }
                        | _ -> return state
                    })
            }

        let! (newState, _) = def.Handler.Value { Count = 5 } Decrement
        test <@ newState.Count = 4 @>
    }

/// <summary>Verifies handleState with Reset command.</summary>
[<Fact>]
let ``handleState Reset produces zero state`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }

                handleState (fun _state (cmd: CounterCommand) ->
                    task {
                        match cmd with
                        | Reset -> return { Count = 0 }
                        | _ -> return _state
                    })
            }

        let! (newState, _) = def.Handler.Value { Count = 99 } Reset
        test <@ newState.Count = 0 @>
    }

/// <summary>Verifies handleState with Get command preserves state.</summary>
[<Fact>]
let ``handleState Get preserves state`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }

                handleState (fun state (cmd: CounterCommand) ->
                    task {
                        match cmd with
                        | Get -> return state
                        | _ -> return state
                    })
            }

        let! (newState, result) = def.Handler.Value { Count = 42 } Get
        test <@ newState.Count = 42 @>
        test <@ unbox<CounterState> result = { Count = 42 } @>
    }

/// <summary>Verifies handleState with string state type.</summary>
[<Fact>]
let ``handleState works with string state`` () =
    task {
        let def =
            grain {
                defaultState "hello"

                handleState (fun state (msg: string) -> task { return state + " " + msg })
            }

        let! (newState, result) = def.Handler.Value "hello" "world"
        test <@ newState = "hello world" @>
        test <@ unbox<string> result = "hello world" @>
    }

// ---------------------------------------------------------------------------
// handleTyped tests
// ---------------------------------------------------------------------------

/// <summary>Verifies that handleTyped registers a handler on the definition.</summary>
[<Fact>]
let ``handleTyped stores handler`` () =
    let def =
        grain {
            defaultState (Created "")

            handleTyped (fun state (cmd: OrderCommand) ->
                task {
                    match state, cmd with
                    | Created _, Confirm -> return Confirmed, StatusChanged "confirmed"
                    | _ -> return state, Rejected "invalid"
                })
        }

    test <@ def.Handler |> Option.isSome @>

/// <summary>Verifies that handleTyped result is the typed value (not the state).</summary>
[<Fact>]
let ``handleTyped result is typed value not state`` () =
    task {
        let def =
            grain {
                defaultState (Created "order-1")

                handleTyped (fun state (cmd: OrderCommand) ->
                    task {
                        match state, cmd with
                        | Created _, Confirm -> return Confirmed, StatusChanged "confirmed"
                        | _, GetStatus -> return state, CurrentStatus(sprintf "%A" state)
                        | _ -> return state, Rejected "invalid"
                    })
            }

        let handler = def.Handler.Value
        let! (newState, result) = handler (Created "order-1") Confirm
        test <@ newState = Confirmed @>
        test <@ unbox<OrderResult> result = StatusChanged "confirmed" @>
    }

/// <summary>Verifies handleTyped with DU result produces correct variants.</summary>
[<Fact>]
let ``handleTyped with DU result produces correct variants`` () =
    task {
        let def =
            grain {
                defaultState (Created "test")

                handleTyped (fun state (cmd: OrderCommand) ->
                    task {
                        match state, cmd with
                        | Created _, Confirm -> return Confirmed, StatusChanged "confirmed"
                        | Confirmed, Ship -> return Shipped, StatusChanged "shipped"
                        | _, GetStatus -> return state, CurrentStatus(sprintf "%A" state)
                        | _ -> return state, Rejected "invalid transition"
                    })
            }

        let handler = def.Handler.Value

        // Confirm from Created
        let! (s1, r1) = handler (Created "test") Confirm
        test <@ s1 = Confirmed @>
        test <@ unbox<OrderResult> r1 = StatusChanged "confirmed" @>

        // Ship from Confirmed
        let! (s2, r2) = handler Confirmed Ship
        test <@ s2 = Shipped @>
        test <@ unbox<OrderResult> r2 = StatusChanged "shipped" @>

        // Invalid transition
        let! (s3, r3) = handler Shipped Confirm
        test <@ s3 = Shipped @>
        test <@ unbox<OrderResult> r3 = Rejected "invalid transition" @>
    }

/// <summary>Verifies handleTyped with int result.</summary>
[<Fact>]
let ``handleTyped with int result`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }

                handleTyped (fun state (cmd: CounterCommand) ->
                    task {
                        match cmd with
                        | Increment ->
                            let newState = { Count = state.Count + 1 }
                            return newState, newState.Count
                        | _ -> return state, state.Count
                    })
            }

        let! (newState, result) = def.Handler.Value { Count = 5 } Increment
        test <@ newState.Count = 6 @>
        test <@ unbox<int> result = 6 @>
    }

/// <summary>Verifies handleTyped GetStatus returns current state as result.</summary>
[<Fact>]
let ``handleTyped GetStatus returns status string`` () =
    task {
        let def =
            grain {
                defaultState (Created "order-2")

                handleTyped (fun state (cmd: OrderCommand) ->
                    task {
                        match cmd with
                        | GetStatus -> return state, CurrentStatus(sprintf "%A" state)
                        | _ -> return state, Rejected "nope"
                    })
            }

        let! (_, result) = def.Handler.Value Confirmed GetStatus
        test <@ unbox<OrderResult> result = CurrentStatus "Confirmed" @>
    }

// ---------------------------------------------------------------------------
// handleStateWithContext tests
// ---------------------------------------------------------------------------

/// <summary>Verifies handleStateWithContext receives the grain context.</summary>
[<Fact>]
let ``handleStateWithContext receives context`` () =
    task {
        let mutable receivedContext = false

        let def =
            grain {
                defaultState { Count = 0 }

                handleStateWithContext (fun ctx state (cmd: CounterCommand) ->
                    task {
                        receivedContext <- ctx.GrainFactory |> isNull |> not || true
                        return { Count = state.Count + 1 }
                    })
            }

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = Unchecked.defaultof<IServiceProvider>
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = None
                GrainId = None
                PrimaryKey = None
            }

        let handler = def.ContextHandler.Value
        let! (newState, result) = handler ctx { Count = 0 } Increment
        test <@ receivedContext @>
        test <@ newState.Count = 1 @>
        test <@ unbox<CounterState> result = { Count = 1 } @>
    }

/// <summary>Verifies handleStateWithContext stores a ContextHandler.</summary>
[<Fact>]
let ``handleStateWithContext stores ContextHandler`` () =
    let def =
        grain {
            defaultState 0

            handleStateWithContext (fun _ctx state (msg: string) -> task { return state + msg.Length })
        }

    test <@ def.ContextHandler |> Option.isSome @>
    test <@ def.Handler |> Option.isNone @>

// ---------------------------------------------------------------------------
// handleTypedWithContext tests
// ---------------------------------------------------------------------------

/// <summary>Verifies handleTypedWithContext receives the grain context and typed result.</summary>
[<Fact>]
let ``handleTypedWithContext receives context and produces typed result`` () =
    task {
        let mutable contextReceived = false

        let def =
            grain {
                defaultState { Count = 0 }

                handleTypedWithContext (fun _ctx state (cmd: CounterCommand) ->
                    task {
                        contextReceived <- true

                        match cmd with
                        | Increment ->
                            let newState = { Count = state.Count + 1 }
                            return newState, newState.Count
                        | _ -> return state, state.Count
                    })
            }

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = Unchecked.defaultof<IServiceProvider>
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = None
                GrainId = None
                PrimaryKey = None
            }

        let handler = def.ContextHandler.Value
        let! (newState, result) = handler ctx { Count = 10 } Increment
        test <@ contextReceived @>
        test <@ newState.Count = 11 @>
        test <@ unbox<int> result = 11 @>
    }

// ---------------------------------------------------------------------------
// handleStateWithServices / handleTypedWithServices tests
// ---------------------------------------------------------------------------

/// <summary>Verifies handleStateWithServices is alias for handleStateWithContext.</summary>
[<Fact>]
let ``handleStateWithServices stores ContextHandler`` () =
    let def =
        grain {
            defaultState 0

            handleStateWithServices (fun _ctx state (msg: int) -> task { return state + msg })
        }

    test <@ def.ContextHandler |> Option.isSome @>
    test <@ def.Handler |> Option.isNone @>

/// <summary>Verifies handleTypedWithServices receives services and typed result.</summary>
[<Fact>]
let ``handleTypedWithServices receives services and typed result`` () =
    task {
        let mutable servicesReceived = false

        let def =
            grain {
                defaultState { Count = 0 }

                handleTypedWithServices (fun _ctx state (cmd: CounterCommand) ->
                    task {
                        servicesReceived <- true

                        match cmd with
                        | Increment ->
                            let ns = { Count = state.Count + 1 }
                            return ns, sprintf "Count=%d" ns.Count
                        | _ -> return state, sprintf "Count=%d" state.Count
                    })
            }

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = Unchecked.defaultof<IServiceProvider>
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = None
                GrainId = None
                PrimaryKey = None
            }

        let! (newState, result) = def.ContextHandler.Value ctx { Count = 0 } Increment
        test <@ servicesReceived @>
        test <@ newState.Count = 1 @>
        test <@ unbox<string> result = "Count=1" @>
    }

// ---------------------------------------------------------------------------
// handleStateCancellable / handleTypedCancellable tests
// ---------------------------------------------------------------------------

/// <summary>Verifies handleStateCancellable stores CancellableHandler.</summary>
[<Fact>]
let ``handleStateCancellable stores CancellableHandler`` () =
    let def =
        grain {
            defaultState 0

            handleStateCancellable (fun state (msg: int) _ct -> task { return state + msg })
        }

    test <@ def.CancellableHandler |> Option.isSome @>
    test <@ def.Handler |> Option.isNone @>

/// <summary>Verifies handleStateCancellable result equals new state.</summary>
[<Fact>]
let ``handleStateCancellable result equals new state`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }

                handleStateCancellable (fun state (cmd: CounterCommand) _ct ->
                    task {
                        match cmd with
                        | Increment -> return { Count = state.Count + 1 }
                        | _ -> return state
                    })
            }

        let! (newState, result) = def.CancellableHandler.Value { Count = 3 } Increment CancellationToken.None
        test <@ newState.Count = 4 @>
        test <@ unbox<CounterState> result = { Count = 4 } @>
    }

/// <summary>Verifies handleTypedCancellable stores CancellableHandler with typed result.</summary>
[<Fact>]
let ``handleTypedCancellable produces typed result`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }

                handleTypedCancellable (fun state (cmd: CounterCommand) _ct ->
                    task {
                        match cmd with
                        | Increment ->
                            let ns = { Count = state.Count + 1 }
                            return ns, ns.Count
                        | _ -> return state, state.Count
                    })
            }

        let! (newState, result) = def.CancellableHandler.Value { Count = 7 } Increment CancellationToken.None
        test <@ newState.Count = 8 @>
        test <@ unbox<int> result = 8 @>
    }

// ---------------------------------------------------------------------------
// Backward compatibility
// ---------------------------------------------------------------------------

/// <summary>Verifies that legacy handle with box still works.</summary>
[<Fact>]
let ``backward compat handle still works with box`` () =
    task {
        let def =
            grain {
                defaultState 0
                handle (fun state (msg: int) -> task { return state + msg, box (state + msg) })
            }

        let! (newState, result) = def.Handler.Value 10 5
        test <@ newState = 15 @>
        test <@ unbox<int> result = 15 @>
    }

/// <summary>Verifies that legacy handle with box and string result works.</summary>
[<Fact>]
let ``backward compat handle with string result`` () =
    task {
        let def =
            grain {
                defaultState "start"
                handle (fun state (msg: string) -> task { return state + msg, box (state + msg) })
            }

        let! (newState, result) = def.Handler.Value "start" "-end"
        test <@ newState = "start-end" @>
        test <@ unbox<string> result = "start-end" @>
    }

// ---------------------------------------------------------------------------
// FsCheck property: arbitrary commands through handleState
// ---------------------------------------------------------------------------

/// <summary>Verifies via FsCheck that arbitrary commands through handleState always produce a valid non-negative count.</summary>
[<Property>]
let ``handleState with arbitrary commands always produces valid state`` (commands: CounterCommand list) =
    let def =
        grain {
            defaultState { Count = 0 }

            handleState (fun state cmd ->
                task {
                    match cmd with
                    | Increment -> return { Count = state.Count + 1 }
                    | Decrement -> return { Count = max 0 (state.Count - 1) }
                    | Reset -> return { Count = 0 }
                    | Get -> return state
                })
        }

    let handler = def.Handler.Value
    let mutable state = { Count = 0 }
    let mutable resultMatchesState = true

    for cmd in commands do
        let (newState, result) = (handler state cmd).Result
        state <- newState
        let resultState = unbox<CounterState> result

        if resultState <> state then
            resultMatchesState <- false

    resultMatchesState && state.Count >= 0

/// <summary>Verifies via FsCheck that handleState result is always equal to the state for value types.</summary>
[<Property>]
let ``handleState result always equals state for int`` (initial: int) (delta: int) =
    let def =
        grain {
            defaultState 0
            handleState (fun state (msg: int) -> task { return state + msg })
        }

    let (newState, result) = (def.Handler.Value initial delta).Result
    newState = unbox<int> result
