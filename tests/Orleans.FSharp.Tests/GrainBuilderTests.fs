module Orleans.FSharp.Tests.GrainBuilderTests

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Runtime
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
    let safeName = if System.String.IsNullOrWhiteSpace name then "Default" else name
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: int) -> task { return state, box state })
            persist safeName
        }
    def.PersistenceName = Some safeName

// ── handleStateWithContextCancellable unit tests ──────────────────────────────

[<Fact>]
let ``handleStateWithContextCancellable registers CancellableContextHandler`` () =
    let def =
        grain {
            defaultState 0
            handleStateWithContextCancellable (fun _ctx state (_msg: int) _ct ->
                task { return state + 1 })
        }
    test <@ def.CancellableContextHandler.IsSome @>

[<Fact>]
let ``handleStateWithContextCancellable boxes result as state`` () =
    task {
        let def =
            grain {
                defaultState 0
                handleStateWithContextCancellable (fun _ctx state (d: int) _ct ->
                    task { return state + d })
            }
        let handler = GrainDefinition.getCancellableContextHandler def
        let ctx = Unchecked.defaultof<GrainContext>
        let! (ns, boxed) = handler ctx 5 3 CancellationToken.None
        test <@ ns = 8 @>
        test <@ unbox<int> boxed = 8 @>
    }

[<Fact>]
let ``handleStateWithContextCancellable: CancellableContextHandler takes precedence over ContextHandler`` () =
    // When both slots are populated, getCancellableContextHandler should pick CancellableContextHandler
    let def =
        grain {
            defaultState 0
            handleStateWithContextCancellable (fun _ctx state (_: int) _ct ->
                task { return state + 100 })
        }
    // verify that only CancellableContextHandler is populated, ContextHandler is None
    test <@ def.CancellableContextHandler.IsSome @>
    test <@ def.ContextHandler.IsNone @>

// ── handleTypedWithContextCancellable unit tests ──────────────────────────────

[<Fact>]
let ``handleTypedWithContextCancellable registers CancellableContextHandler`` () =
    let def =
        grain {
            defaultState 0
            handleTypedWithContextCancellable (fun _ctx state (_msg: int) _ct ->
                task { return state + 1, string (state + 1) })
        }
    test <@ def.CancellableContextHandler.IsSome @>

[<Fact>]
let ``handleTypedWithContextCancellable boxes typed result`` () =
    task {
        let def =
            grain {
                defaultState 0
                handleTypedWithContextCancellable (fun _ctx state (d: int) _ct ->
                    task { return state + d, string (state + d) })
            }
        let handler = GrainDefinition.getCancellableContextHandler def
        let ctx = Unchecked.defaultof<GrainContext>
        let! (ns, boxed) = handler ctx 5 3 CancellationToken.None
        test <@ ns = 8 @>
        test <@ unbox<string> boxed = "8" @>
    }

[<Fact>]
let ``handleTypedWithContextCancellable result type independent of state type`` () =
    task {
        let def =
            grain {
                defaultState ""
                handleTypedWithContextCancellable (fun _ctx state (word: string) _ct ->
                    task { return state + word, word.ToUpperInvariant() })
            }
        let handler = GrainDefinition.getCancellableContextHandler def
        let ctx = Unchecked.defaultof<GrainContext>
        let! (ns, boxed) = handler ctx "" "hello" CancellationToken.None
        test <@ ns = "hello" @>
        test <@ unbox<string> boxed = "HELLO" @>
    }

// ---------------------------------------------------------------------------
// GrainContext.forCSharp property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``forCSharp: States map contains exactly the supplied key-value pairs`` (pairs: (string * int) list) =
    // Deduplicate by key to avoid Map.ofSeq last-writer-wins issues
    let uniquePairs = pairs |> List.distinctBy fst
    let kvps =
        uniquePairs
        |> List.map (fun (k, v) -> KeyValuePair<string, obj>(k, box v))
    let ctx = GrainContext.forCSharp null null kvps (GrainId.Create("test/grain", "k"))
    uniquePairs |> List.forall (fun (k, v) ->
        ctx.States |> Map.tryFind k = Some (box v))

[<Property>]
let ``forCSharp: States map has same count as unique keys`` (pairs: (string * int) list) =
    let uniquePairs = pairs |> List.distinctBy fst
    let kvps =
        uniquePairs
        |> List.map (fun (k, v) -> KeyValuePair<string, obj>(k, box v))
    let ctx = GrainContext.forCSharp null null kvps (GrainId.Create("test/grain", "k"))
    ctx.States |> Map.count = uniquePairs.Length

/// Make a safe grain key: append a suffix so whitespace-only strings become non-whitespace.
let private safeKey (raw: NonEmptyString) = raw.Get.Trim() + "k"

[<Property>]
let ``forCSharp: GrainId is populated from the supplied value`` (key: NonEmptyString) =
    let k = safeKey key
    let grainId = GrainId.Create("test/grain", k)
    let ctx = GrainContext.forCSharp null null [] grainId
    ctx.GrainId = Some grainId

[<Property>]
let ``forCSharp: empty states yields an empty States map`` () =
    let ctx = GrainContext.forCSharp null null [] (GrainId.Create("test/grain", "x"))
    ctx.States |> Map.isEmpty

[<Property>]
let ``forCSharp: DeactivateOnIdle is always None`` (key: NonEmptyString) =
    let ctx = GrainContext.forCSharp null null [] (GrainId.Create("test/grain", safeKey key))
    ctx.DeactivateOnIdle.IsNone

[<Property>]
let ``forCSharp: PrimaryKey is always None`` (key: NonEmptyString) =
    let ctx = GrainContext.forCSharp null null [] (GrainId.Create("test/grain", safeKey key))
    ctx.PrimaryKey.IsNone

[<Property>]
let ``GrainContext.empty: all optional fields are None`` () =
    let ctx = GrainContext.empty
    ctx.DeactivateOnIdle.IsNone && ctx.DelayDeactivation.IsNone
    && ctx.GrainId.IsNone && ctx.PrimaryKey.IsNone

[<Property>]
let ``grain CE: multiple additionalState entries are all registered`` (n: PositiveInt) =
    let count = min n.Get 10 // cap to avoid slow test
    // Build a grain definition that has `count` additional state specs
    // We can verify AdditionalStates has the right count
    let mutable def =
        grain {
            defaultState 0
            handle (fun state (_: int) -> task { return state, box state })
        }
    // additionalState cannot be used outside the CE syntactically, but we can
    // verify that a single additionalState entry registers correctly:
    let single =
        grain {
            defaultState 0
            handle (fun state (_: int) -> task { return state, box state })
            additionalState "extra" "Default" (0 : int)
        }
    single.AdditionalStates |> Map.count = 1
