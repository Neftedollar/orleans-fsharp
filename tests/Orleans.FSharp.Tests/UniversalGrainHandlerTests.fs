module Orleans.FSharp.Tests.UniversalGrainHandlerTests

/// <summary>
/// Unit tests for UniversalGrainHandlerRegistry (Orleans.FSharp.Runtime) and
/// GrainDispatchResult / FSharpGrainImpl (Orleans.FSharp.Abstractions).
/// These test the dispatch layer that powers the FSharpGrain.ref universal pattern.
/// </summary>

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Runtime

// ── Test types ──────────────────────────────────────────────────────────────

type CountState = { N: int }

type CountMsg =
    | Inc
    | Dec
    | GetN

type StringState = { Text: string }

type StringMsg =
    | Append of string
    | GetText

// ── GrainDispatchResult ──────────────────────────────────────────────────────

[<Fact>]
let ``GrainDispatchResult stores NewState and Result`` () =
    let result = GrainDispatchResult(box 42, box "hello")
    test <@ result.NewState :?> int = 42 @>
    test <@ result.Result :?> string = "hello" @>

[<Fact>]
let ``GrainDispatchResult allows null NewState`` () =
    let result = GrainDispatchResult(null, box 1)
    test <@ result.NewState = null @>
    test <@ result.Result :?> int = 1 @>

[<Fact>]
let ``GrainDispatchResult allows null Result`` () =
    let result = GrainDispatchResult(box "state", null)
    test <@ result.NewState :?> string = "state" @>
    test <@ result.Result = null @>

[<Fact>]
let ``GrainDispatchResult allows both null`` () =
    let result = GrainDispatchResult(null, null)
    test <@ result.NewState = null @>
    test <@ result.Result = null @>

// ── FSharpGrainImpl metadata ─────────────────────────────────────────────────

[<Fact>]
let ``FSharpGrainImpl is a sealed non-abstract class`` () =
    let t = typeof<FSharpGrainImpl>
    test <@ not t.IsAbstract @>
    test <@ t.IsSealed @>

[<Fact>]
let ``FSharpGrainImpl implements IFSharpGrain`` () =
    test <@ typeof<IFSharpGrain>.IsAssignableFrom(typeof<FSharpGrainImpl>) @>

[<Fact>]
let ``FSharpGrainImpl extends Orleans.Grain`` () =
    test <@ typeof<Orleans.Grain>.IsAssignableFrom(typeof<FSharpGrainImpl>) @>

[<Fact>]
let ``FSharpGrainImpl is in Orleans.FSharp namespace`` () =
    test <@ typeof<FSharpGrainImpl>.Namespace = "Orleans.FSharp" @>

[<Fact>]
let ``FSharpGrainImpl has exactly one constructor taking IUniversalGrainHandler`` () =
    let ctors = typeof<FSharpGrainImpl>.GetConstructors()
    test <@ ctors.Length = 1 @>
    let ps = ctors.[0].GetParameters()
    test <@ ps.Length = 1 @>
    test <@ ps.[0].ParameterType = typeof<IUniversalGrainHandler> @>

// ── FSharpGrainGuidImpl metadata ─────────────────────────────────────────────

[<Fact>]
let ``FSharpGrainGuidImpl is a sealed non-abstract class`` () =
    let t = typeof<FSharpGrainGuidImpl>
    test <@ not t.IsAbstract @>
    test <@ t.IsSealed @>

[<Fact>]
let ``FSharpGrainGuidImpl implements IFSharpGrainWithGuidKey`` () =
    test <@ typeof<IFSharpGrainWithGuidKey>.IsAssignableFrom(typeof<FSharpGrainGuidImpl>) @>

[<Fact>]
let ``FSharpGrainGuidImpl extends Orleans.Grain`` () =
    test <@ typeof<Orleans.Grain>.IsAssignableFrom(typeof<FSharpGrainGuidImpl>) @>

[<Fact>]
let ``FSharpGrainGuidImpl has exactly one constructor taking IUniversalGrainHandler`` () =
    let ctors = typeof<FSharpGrainGuidImpl>.GetConstructors()
    test <@ ctors.Length = 1 @>
    let ps = ctors.[0].GetParameters()
    test <@ ps.Length = 1 @>
    test <@ ps.[0].ParameterType = typeof<IUniversalGrainHandler> @>

// ── FSharpGrainIntImpl metadata ──────────────────────────────────────────────

[<Fact>]
let ``FSharpGrainIntImpl is a sealed non-abstract class`` () =
    let t = typeof<FSharpGrainIntImpl>
    test <@ not t.IsAbstract @>
    test <@ t.IsSealed @>

[<Fact>]
let ``FSharpGrainIntImpl implements IFSharpGrainWithIntKey`` () =
    test <@ typeof<IFSharpGrainWithIntKey>.IsAssignableFrom(typeof<FSharpGrainIntImpl>) @>

[<Fact>]
let ``FSharpGrainIntImpl extends Orleans.Grain`` () =
    test <@ typeof<Orleans.Grain>.IsAssignableFrom(typeof<FSharpGrainIntImpl>) @>

[<Fact>]
let ``FSharpGrainIntImpl has exactly one constructor taking IUniversalGrainHandler`` () =
    let ctors = typeof<FSharpGrainIntImpl>.GetConstructors()
    test <@ ctors.Length = 1 @>
    let ps = ctors.[0].GetParameters()
    test <@ ps.Length = 1 @>
    test <@ ps.[0].ParameterType = typeof<IUniversalGrainHandler> @>

// ── IUniversalGrainHandler interface ────────────────────────────────────────

[<Fact>]
let ``IUniversalGrainHandler is an interface`` () =
    test <@ typeof<IUniversalGrainHandler>.IsInterface @>

[<Fact>]
let ``IUniversalGrainHandler.GetDefaultState takes Type`` () =
    let m = typeof<IUniversalGrainHandler>.GetMethod("GetDefaultState")
    test <@ m <> null @>
    test <@ m.GetParameters().Length = 1 @>
    test <@ m.GetParameters().[0].ParameterType = typeof<Type> @>

[<Fact>]
let ``IUniversalGrainHandler.Handle takes nullable object and object`` () =
    let m = typeof<IUniversalGrainHandler>.GetMethod("Handle")
    test <@ m <> null @>
    let ps = m.GetParameters()
    test <@ ps.Length = 2 @>

// ── UniversalGrainHandlerRegistry — dispatch ─────────────────────────────────

[<Fact>]
let ``Registry dispatches handler and returns updated state`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()
        let def =
            grain {
                defaultState { N = 0 }
                handle (fun state (msg: CountMsg) ->
                    task {
                        match msg with
                        | Inc -> let ns = { N = state.N + 1 } in return ns, box ns
                        | Dec -> let ns = { N = state.N - 1 } in return ns, box ns
                        | GetN -> return state, box state
                    })
            }
        registry.Register<CountState, CountMsg>(def)

        let handler = registry :> IUniversalGrainHandler
        let! result = handler.Handle(null, box Inc)
        let ns = result.NewState :?> CountState
        test <@ ns.N = 1 @>
    }

[<Fact>]
let ``Registry uses default state on first call (null current)`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()
        let def =
            grain {
                defaultState { N = 10 }
                handle (fun state (_msg: CountMsg) ->
                    task { return state, box state })
            }
        registry.Register<CountState, CountMsg>(def)

        let handler = registry :> IUniversalGrainHandler
        let! result = handler.Handle(null, box GetN)
        let ns = result.NewState :?> CountState
        test <@ ns.N = 10 @>
    }

[<Fact>]
let ``Registry passes current state when non-null`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()
        let def =
            grain {
                defaultState { N = 0 }
                handle (fun state (msg: CountMsg) ->
                    task {
                        match msg with
                        | Inc -> let ns = { N = state.N + 1 } in return ns, box ns
                        | _ -> return state, box state
                    })
            }
        registry.Register<CountState, CountMsg>(def)

        let handler = registry :> IUniversalGrainHandler
        let existingState = box { N = 5 }
        let! result = handler.Handle(existingState, box Inc)
        let ns = result.NewState :?> CountState
        test <@ ns.N = 6 @>
    }

[<Fact>]
let ``Registry GetDefaultState returns boxed default for registered type`` () =
    let registry = UniversalGrainHandlerRegistry()
    let def =
        grain {
            defaultState { N = 99 }
            handle (fun state _ -> task { return state, box state })
        }
    registry.Register<CountState, CountMsg>(def)

    let handler = registry :> IUniversalGrainHandler
    let d = handler.GetDefaultState(typeof<CountMsg>)
    test <@ (d :?> CountState).N = 99 @>

[<Fact>]
let ``Registry GetDefaultState returns null for unregistered type`` () =
    let registry = UniversalGrainHandlerRegistry()
    let handler = registry :> IUniversalGrainHandler
    let d = handler.GetDefaultState(typeof<CountMsg>)
    test <@ d = null @>

[<Fact>]
let ``Registry throws InvalidOperationException for unregistered message type`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()
        let handler = registry :> IUniversalGrainHandler

        let! ex = Assert.ThrowsAsync<InvalidOperationException>(fun () ->
            handler.Handle(null, box Inc) :> Task)
        test <@ ex.Message.Contains("CountMsg") @>
    }

[<Fact>]
let ``Registry throws InvalidOperationException on duplicate message type registration`` () =
    let registry = UniversalGrainHandlerRegistry()
    let def =
        grain {
            defaultState { N = 0 }
            handle (fun state _ -> task { return state, box state })
        }
    registry.Register<CountState, CountMsg>(def)
    // Second registration for the same message type should throw
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        registry.Register<CountState, CountMsg>(def))
    test <@ ex.Message.Contains("CountMsg") @>

[<Fact>]
let ``Registry supports multiple distinct (State, Message) pairs`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()

        let countDef =
            grain {
                defaultState { N = 0 }
                handle (fun state (msg: CountMsg) ->
                    task {
                        match msg with
                        | Inc -> let ns = { N = state.N + 1 } in return ns, box ns
                        | _ -> return state, box state
                    })
            }

        let stringDef =
            grain {
                defaultState { Text = "" }
                handle (fun state (msg: StringMsg) ->
                    task {
                        match msg with
                        | Append s -> let ns = { Text = state.Text + s } in return ns, box ns
                        | GetText -> return state, box state
                    })
            }

        registry.Register<CountState, CountMsg>(countDef)
        registry.Register<StringState, StringMsg>(stringDef)

        let handler = registry :> IUniversalGrainHandler
        let! r1 = handler.Handle(null, box Inc)
        let! r2 = handler.Handle(null, box (Append "hello"))

        test <@ (r1.NewState :?> CountState).N = 1 @>
        test <@ (r2.NewState :?> StringState).Text = "hello" @>
    }

[<Fact>]
let ``Registry state is accumulated across multiple Handle calls`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()
        let def =
            grain {
                defaultState { N = 0 }
                handle (fun state (msg: CountMsg) ->
                    task {
                        match msg with
                        | Inc -> let ns = { N = state.N + 1 } in return ns, box ns
                        | Dec -> let ns = { N = state.N - 1 } in return ns, box ns
                        | GetN -> return state, box state
                    })
            }
        registry.Register<CountState, CountMsg>(def)
        let handler = registry :> IUniversalGrainHandler

        let! r1 = handler.Handle(null, box Inc)
        let! r2 = handler.Handle(r1.NewState, box Inc)
        let! r3 = handler.Handle(r2.NewState, box Inc)
        let! r4 = handler.Handle(r3.NewState, box Dec)
        let! r5 = handler.Handle(r4.NewState, box GetN)

        test <@ (r5.NewState :?> CountState).N = 2 @>
    }

// ── Property: dispatch is pure — same inputs give same result ────────────────

[<Fact>]
let ``Registry dispatch is deterministic for same (state, message) inputs`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()
        let def =
            grain {
                defaultState { N = 0 }
                handle (fun state (msg: CountMsg) ->
                    task {
                        match msg with
                        | Inc -> let ns = { N = state.N + 1 } in return ns, box ns
                        | _ -> return state, box state
                    })
            }
        registry.Register<CountState, CountMsg>(def)

        let handler = registry :> IUniversalGrainHandler
        let state = box { N = 7 }
        let! r1 = handler.Handle(state, box Inc)
        let! r2 = handler.Handle(state, box Inc)

        test <@ (r1.NewState :?> CountState).N = (r2.NewState :?> CountState).N @>
    }

// ── FsCheck-style property: repeated increments never go negative ────────────

open FsCheck
open FsCheck.Xunit

[<Property>]
let ``Repeated Inc operations produce monotonically increasing N`` (n: PositiveInt) =
    let count = n.Get
    let registry = UniversalGrainHandlerRegistry()
    let def =
        grain {
            defaultState { N = 0 }
            handle (fun state (msg: CountMsg) ->
                task {
                    match msg with
                    | Inc -> let ns = { N = state.N + 1 } in return ns, box ns
                    | _ -> return state, box state
                })
        }
    registry.Register<CountState, CountMsg>(def)

    let handler = registry :> IUniversalGrainHandler
    let mutable st: obj = null
    for _ in 1..count do
        let r = handler.Handle(st, box Inc).GetAwaiter().GetResult()
        st <- r.NewState

    let final = (st :?> CountState).N
    final = count

[<Property>]
let ``GetDefaultState returns the value set via defaultState CE keyword`` (seed: int) =
    let registry = UniversalGrainHandlerRegistry()
    let def =
        grain {
            defaultState { N = seed }
            handle (fun state _ -> task { return state, box state })
        }
    registry.Register<CountState, CountMsg>(def)
    let handler = registry :> IUniversalGrainHandler
    let d = handler.GetDefaultState(typeof<CountMsg>)
    (d :?> CountState).N = seed

// ── Error propagation ────────────────────────────────────────────────────────

[<Fact>]
let ``Registry propagates exceptions thrown by the handler`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()
        let def =
            grain {
                defaultState { N = 0 }
                handle (fun _state (msg: CountMsg) ->
                    task {
                        match msg with
                        | Inc -> return failwith "intentional handler error"
                        | _ -> return _state, box _state
                    })
            }
        registry.Register<CountState, CountMsg>(def)
        let handler = registry :> IUniversalGrainHandler

        let! ex = Assert.ThrowsAnyAsync<exn>(fun () ->
            handler.Handle(null, box Inc) :> System.Threading.Tasks.Task)
        test <@ ex.Message.Contains("intentional handler error") @>
    }

[<Fact>]
let ``Registry propagates exceptions without swallowing cause`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()
        let def =
            grain {
                defaultState { N = 0 }
                handle (fun _state (_msg: CountMsg) ->
                    task {
                        raise (System.ArgumentOutOfRangeException("N", "out of range"))
                        return _state, box _state
                    })
            }
        registry.Register<CountState, CountMsg>(def)
        let handler = registry :> IUniversalGrainHandler

        let! ex = Assert.ThrowsAsync<System.ArgumentOutOfRangeException>(fun () ->
            handler.Handle(null, box Inc) :> System.Threading.Tasks.Task)
        test <@ ex.ParamName = "N" @>
    }

// ── GrainDispatchResult edge cases ───────────────────────────────────────────

[<Fact>]
let ``GrainDispatchResult preserves reference equality for boxed state`` () =
    let state = box { N = 42 }
    let result = GrainDispatchResult(state, null)
    test <@ obj.ReferenceEquals(result.NewState, state) @>

[<Fact>]
let ``GrainDispatchResult preserves reference equality for boxed result`` () =
    let r = box "hello"
    let result = GrainDispatchResult(null, r)
    test <@ obj.ReferenceEquals(result.Result, r) @>

// ── FsCheck: Inc/Dec sequence maintains correct count ───────────────────────

[<Property>]
let ``Inc/Dec sequences maintain correct net count`` (ops: PositiveInt * PositiveInt) =
    let incCount = (fst ops).Get
    let decCount = (snd ops).Get

    let registry = UniversalGrainHandlerRegistry()
    let def =
        grain {
            defaultState { N = 0 }
            handle (fun state (msg: CountMsg) ->
                task {
                    match msg with
                    | Inc -> let ns = { N = state.N + 1 } in return ns, box ns
                    | Dec -> let ns = { N = state.N - 1 } in return ns, box ns
                    | GetN -> return state, box state
                })
        }
    registry.Register<CountState, CountMsg>(def)
    let handler = registry :> IUniversalGrainHandler

    let mutable st: obj = null

    for _ in 1..incCount do
        let r = handler.Handle(st, box Inc).GetAwaiter().GetResult()
        st <- r.NewState

    for _ in 1..decCount do
        let r = handler.Handle(st, box Dec).GetAwaiter().GetResult()
        st <- r.NewState

    let final = (st :?> CountState).N
    final = (incCount - decCount)
