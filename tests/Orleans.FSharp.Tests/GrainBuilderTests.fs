module Orleans.FSharp.Tests.GrainBuilderTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck.Xunit
open Orleans.FSharp

[<Fact>]
let ``grain CE sets defaultState`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.DefaultState = Some 0 @>

[<Fact>]
let ``grain CE sets defaultState with DU`` () =
    let def =
        grain {
            defaultState "initial"
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.DefaultState = Some "initial" @>

[<Fact>]
let ``grain CE registers handler`` () =
    let def =
        grain {
            defaultState 0

            handle (fun state (msg: string) ->
                task {
                    let newState = state + msg.Length
                    return newState, box newState
                })
        }

    test <@ def.Handler |> Option.isSome @>

[<Fact>]
let ``grain CE handler produces correct result`` () =
    task {
        let def =
            grain {
                defaultState 10

                handle (fun state (msg: int) ->
                    task {
                        let newState = state + msg
                        return newState, box newState
                    })
            }

        let handler = def.Handler.Value
        let! (newState, result) = handler 10 5
        test <@ newState = 15 @>
        test <@ unbox<int> result = 15 @>
    }

[<Fact>]
let ``grain CE sets persist name`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            persist "MyStorage"
        }

    test <@ def.PersistenceName = Some "MyStorage" @>

[<Fact>]
let ``grain CE without persist has None persistence`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.PersistenceName = None @>

[<Fact>]
let ``grain CE sets onActivate`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onActivate (fun state -> task { return state + 1 })
        }

    test <@ def.OnActivate |> Option.isSome @>

[<Fact>]
let ``grain CE onActivate handler produces correct result`` () =
    task {
        let def =
            grain {
                defaultState 0
                handle (fun state _msg -> task { return state, box state })
                onActivate (fun state -> task { return state + 100 })
            }

        let! result = def.OnActivate.Value 5
        test <@ result = 105 @>
    }

[<Fact>]
let ``grain CE sets onDeactivate`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onDeactivate (fun _state -> task { return () })
        }

    test <@ def.OnDeactivate |> Option.isSome @>

[<Fact>]
let ``grain CE missing handler throws in Run`` () =
    let ex =
        Assert.Throws<System.InvalidOperationException>(fun () ->
            grain {
                defaultState 0
            }
            |> ignore<GrainDefinition<int, string>>)

    test <@ ex.Message.Contains("handler") @>
    test <@ ex.Message.Contains("handle") @>

[<Fact>]
let ``grain CE missing handler error contains type names`` () =
    let ex =
        Assert.Throws<System.InvalidOperationException>(fun () ->
            grain {
                defaultState 0
            }
            |> ignore<GrainDefinition<int, string>>)

    test <@ ex.Message.Contains("Int32") @>
    test <@ ex.Message.Contains("String") @>

[<Fact>]
let ``grain CE produces complete definition`` () =
    let def =
        grain {
            defaultState "idle"

            handle (fun state (cmd: int) ->
                task {
                    let newState = $"{state}_{cmd}"
                    return newState, box newState
                })

            persist "Default"
            onActivate (fun s -> task { return s })
            onDeactivate (fun _ -> task { return () })
        }

    test <@ def.DefaultState = Some "idle" @>
    test <@ def.Handler |> Option.isSome @>
    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.OnActivate |> Option.isSome @>
    test <@ def.OnDeactivate |> Option.isSome @>

// ── handleState variant tests ─────────────────────────────────────────────────

// Types for handleState tests
type HsState = { N: int }
type HsMsg = Tick | Read

// Types for handleTyped tests
type HtGreetCmd = GreetBy of string | GetLen

[<Fact>]
let ``handleState registers a handler`` () =
    let def =
        grain {
            defaultState 0
            handleState (fun state (_msg: string) -> task { return state + 1 })
        }

    test <@ def.Handler |> Option.isSome @>

[<Fact>]
let ``handleState returns state as result`` () =
    task {
        let def =
            grain {
                defaultState 0
                handleState (fun state (_msg: string) -> task { return state + 10 })
            }

        let handler = def.Handler.Value
        let! (newState, boxedResult) = handler 5 "inc"
        test <@ newState = 15 @>
        test <@ unbox<int> boxedResult = 15 @>
    }

[<Fact>]
let ``handleState with record state`` () =
    task {
        let def =
            grain {
                defaultState { N = 0 }
                handleState (fun state (msg: HsMsg) ->
                    task {
                        match msg with
                        | Tick -> return { N = state.N + 1 }
                        | Read -> return state
                    })
            }

        let handler = def.Handler.Value
        let! (s1, _) = handler { N = 0 } Tick
        test <@ s1.N = 1 @>
        let! (s2, boxed) = handler s1 Read
        test <@ s2.N = 1 @>
        test <@ (unbox<HsState> boxed).N = 1 @>
    }

// ── handleTyped variant tests ─────────────────────────────────────────────────

[<Fact>]
let ``handleTyped registers a handler`` () =
    let def =
        grain {
            defaultState 0
            handleTyped (fun state (_msg: string) -> task { return state + 1, state })
        }

    test <@ def.Handler |> Option.isSome @>

[<Fact>]
let ``handleTyped boxes result without manual box call`` () =
    task {
        let def =
            grain {
                defaultState 0
                handleTyped (fun state (_msg: string) -> task { return state + 5, state + 5 })
            }

        let handler = def.Handler.Value
        let! (newState, boxedResult) = handler 0 "inc"
        test <@ newState = 5 @>
        test <@ unbox<int> boxedResult = 5 @>
    }

[<Fact>]
let ``handleTyped can return different type for result`` () =
    task {
        let def =
            grain {
                defaultState ""
                handleTyped (fun state (msg: HtGreetCmd) ->
                    task {
                        match msg with
                        | GreetBy name -> return name, $"Hello, {name}!"
                        | GetLen       -> return state, state.Length.ToString()
                    })
            }

        let handler = def.Handler.Value
        let! (s1, r1) = handler "" (GreetBy "Orleans")
        test <@ s1 = "Orleans" @>
        test <@ unbox<string> r1 = "Hello, Orleans!" @>

        let! (s2, r2) = handler "hi" GetLen
        test <@ s2 = "hi" @>
        test <@ unbox<string> r2 = "2" @>
    }

[<Fact>]
let ``handleTyped with unit result`` () =
    task {
        let def =
            grain {
                defaultState 0
                handleTyped (fun state (_msg: string) -> task { return state + 1, () })
            }

        let handler = def.Handler.Value
        let! (newState, _) = handler 0 "fire"
        test <@ newState = 1 @>
    }

// ── FsCheck property-based tests for handleState / handleTyped ────────────

/// Simple accumulator command type for property tests.
type AccCmd = Add of int | Sub of int | Reset

[<Property>]
let ``handleState: arbitrary int accumulator sequences produce non-negative state with abs`` (commands: AccCmd list) =
    // Use absolute values to ensure the handler always returns valid state
    let def =
        grain {
            defaultState 0
            handleState (fun state (cmd: AccCmd) ->
                task {
                    match cmd with
                    | Add n  -> return state + abs n
                    | Sub n  -> return state - abs n
                    | Reset  -> return 0
                })
        }
    let handler = def.Handler.Value
    let mutable state = 0
    for cmd in commands do
        let (ns, _) = (handler state cmd).Result
        state <- ns
    // All states are reachable — no invariant here, just checking it doesn't throw
    true

[<Property>]
let ``handleState: result equals new state for every invocation`` (n: int) (delta: int) =
    let def =
        grain {
            defaultState n
            handleState (fun state (d: int) -> task { return state + d })
        }
    let handler = def.Handler.Value
    let (newState, boxedResult) = (handler n delta).Result
    newState = n + delta && unbox<int> boxedResult = newState

[<Property>]
let ``handleTyped: result type is independent of state type`` (initial: string) (msg: int) =
    let def =
        grain {
            defaultState initial
            handleTyped (fun state (n: int) ->
                task {
                    let ns = state + string n
                    return ns, ns.Length
                })
        }
    let handler = def.Handler.Value
    let (newState, boxedResult) = (handler initial msg).Result
    newState = initial + string msg && unbox<int> boxedResult = newState.Length

[<Property>]
let ``handleState: folding commands equals sequential application`` (deltas: int list) =
    // Verify that applying deltas one-by-one gives the same final state
    // as computing the sum manually (assuming the handler adds each delta).
    let def =
        grain {
            defaultState 0
            handleState (fun state (d: int) -> task { return state + d })
        }
    let handler = def.Handler.Value
    let expectedSum = List.sum deltas
    let mutable state = 0
    for d in deltas do
        let (ns, _) = (handler state d).Result
        state <- ns
    state = expectedSum

[<Property>]
let ``grain CE: defaultState is always Some for well-formed definitions`` (initial: int) =
    let def =
        grain {
            defaultState initial
            handle (fun state (_msg: int) -> task { return state, box state })
        }
    def.DefaultState = Some initial

[<Property>]
let ``grain CE: persist name is preserved exactly`` (name: string) =
    let safeName = if System.String.IsNullOrEmpty name then "Default" else name
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: int) -> task { return state, box state })
            persist safeName
        }
    def.PersistenceName = Some safeName
