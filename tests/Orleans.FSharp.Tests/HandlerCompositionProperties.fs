/// <summary>
/// FsCheck property-based tests for grain handler composition invariants.
///
/// These tests verify algebraic properties of the <c>grain {}</c> CE handler
/// variants: idempotency, associativity of sequential application, result
/// consistency between handler types, and context-handler equivalence.
/// </summary>
module Orleans.FSharp.Tests.HandlerCompositionProperties

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

// ── Shared helpers ────────────────────────────────────────────────────────────

/// Build a task that resolves immediately.
let inline taskOf v = Task.FromResult v

// ── handleState: result equals new state ─────────────────────────────────────

[<Property>]
let ``handleState: boxed result always equals new state`` (init: int) (delta: int) =
    let def =
        grain {
            defaultState init
            handleState (fun state (d: int) -> task { return state + d })
        }
    let handler = def.Handler.Value
    let (newState, boxedResult) = (handler init delta).Result
    newState = init + delta && unbox<int> boxedResult = newState

[<Property>]
let ``handleState: any sequence of additions equals List.sum`` (deltas: int list) =
    let def =
        grain {
            defaultState 0
            handleState (fun state (d: int) -> task { return state + d })
        }
    let handler = def.Handler.Value
    let expected = List.sum deltas
    let mutable state = 0
    for d in deltas do
        let (ns, _) = (handler state d).Result
        state <- ns
    state = expected

// ── handleTyped: typed result is independently boxed ─────────────────────────

[<Property>]
let ``handleTyped: result is unboxable to declared result type`` (n: int) (m: int) =
    let def =
        grain {
            defaultState n
            handleTyped (fun state (delta: int) ->
                task { return state + delta, string (state + delta) })
        }
    let handler = def.Handler.Value
    let (_newState, boxedResult) = (handler n m).Result
    let unboxed = unbox<string> boxedResult
    unboxed = string (n + m)

[<Property>]
let ``handleTyped: state and result evolve independently`` (words: string list) =
    // State accumulates word lengths; result is the word itself
    let def =
        grain {
            defaultState 0
            handleTyped (fun state (word: string) ->
                task { return state + word.Length, word.ToUpperInvariant() })
        }
    let handler = def.Handler.Value
    let mutable state = 0
    let results = System.Collections.Generic.List<string>()
    for w in words do
        let (ns, boxedResult) = (handler state w).Result
        state <- ns
        results.Add(unbox<string> boxedResult)
    // State = sum of word lengths
    let expectedLen = words |> List.sumBy (fun w -> w.Length)
    state = expectedLen
    && (words |> List.forall (fun w ->
        results |> Seq.exists (fun r -> r = w.ToUpperInvariant())))

// ── handleWithContext: context is threaded faithfully ─────────────────────────

[<Property>]
let ``handleWithContext: context reference is passed unchanged to handler`` (n: int) =
    let mutable capturedCtxRef: GrainContext option = None
    let mockCtx: GrainContext =
        { GrainFactory = null; ServiceProvider = null; States = Map.empty
          DeactivateOnIdle = None; DelayDeactivation = None; GrainId = None; PrimaryKey = None }
    let def =
        grain {
            defaultState 0
            handleWithContext (fun ctx state (msg: int) ->
                task {
                    capturedCtxRef <- Some ctx
                    return state + msg, box (state + msg)
                })
        }
    let handler = GrainDefinition.getContextHandler def
    let (_ns, _) = (handler mockCtx 0 n).Result
    capturedCtxRef.IsSome && obj.ReferenceEquals(capturedCtxRef.Value.GrainFactory, mockCtx.GrainFactory)

[<Property>]
let ``handleWithContext: equivalent to handle when context not used`` (n: PositiveInt) =
    // A handler that ignores context should behave identically to a plain handle.
    let plain =
        grain {
            defaultState 0
            handle (fun state (msg: int) -> task { return state + msg, box (state + msg) })
        }
    let withCtx =
        grain {
            defaultState 0
            handleWithContext (fun _ctx state (msg: int) ->
                task { return state + msg, box (state + msg) })
        }
    let plainHandler = GrainDefinition.getContextHandler plain
    let ctxHandler   = GrainDefinition.getContextHandler withCtx
    let ctx = Unchecked.defaultof<GrainContext>
    let (ns1, r1) = (plainHandler ctx 0 n.Get).Result
    let (ns2, r2) = (ctxHandler   ctx 0 n.Get).Result
    ns1 = ns2 && unbox<int> r1 = unbox<int> r2

// ── handleCancellable: CancellationToken is threaded ─────────────────────────

[<Property>]
let ``handleCancellable: cancellation token is passed to handler`` (n: int) =
    let mutable ctReceived: CancellationToken option = None
    let def =
        grain {
            defaultState 0
            handleCancellable (fun state (msg: int) ct ->
                task {
                    ctReceived <- Some ct
                    return state + msg, box (state + msg)
                })
        }
    use cts = new CancellationTokenSource()
    let handler = GrainDefinition.getCancellableContextHandler def
    let ctx = Unchecked.defaultof<GrainContext>
    let (_ns, _) = (handler ctx 0 n cts.Token).Result
    ctReceived.IsSome && ctReceived.Value = cts.Token

// ── getCancellableContextHandler fallback chain ───────────────────────────────

[<Property>]
let ``plain handle is reachable via getCancellableContextHandler`` (n: PositiveInt) =
    // A plain handler should be accessible through the full fallback chain.
    let def =
        grain {
            defaultState 0
            handle (fun state (msg: int) -> task { return state * msg, box (state * msg) })
        }
    let handler = GrainDefinition.getCancellableContextHandler def
    let ctx = Unchecked.defaultof<GrainContext>
    let (ns, _) = (handler ctx n.Get n.Get CancellationToken.None).Result
    ns = n.Get * n.Get

// ── defaultState round-trip ───────────────────────────────────────────────────

[<Property>]
let ``defaultState round-trips through grain CE for any value type`` (value: int) =
    let def =
        grain {
            defaultState value
            handle (fun state ((): unit) -> task { return state, box state })
        }
    def.DefaultState = Some value

[<Property>]
let ``defaultState round-trips for string values`` (value: NonNull<string>) =
    let def =
        grain {
            defaultState value.Get
            handle (fun state ((): unit) -> task { return state, box state })
        }
    def.DefaultState = Some value.Get

// ── PersistenceName round-trip ────────────────────────────────────────────────

[<Property>]
let ``persist name round-trips for non-whitespace strings`` (value: NonEmptyString) =
    // NonEmptyString guarantees at least one character, but may be whitespace-only (e.g. "\t").
    // persist validates that the name is non-whitespace, so skip whitespace-only inputs.
    System.String.IsNullOrWhiteSpace(value.Get)
    ||
    let def =
        grain {
            defaultState 0
            handle (fun state ((): unit) -> task { return state, box state })
            persist value.Get
        }
    def.PersistenceName = Some value.Get

// ── hasAnyHandler property ────────────────────────────────────────────────────

[<Property>]
let ``hasAnyHandler is true for handle`` (n: int) =
    let def =
        grain {
            defaultState n
            handle (fun state ((): unit) -> task { return state, box state })
        }
    GrainDefinition.hasAnyHandler def

[<Property>]
let ``hasAnyHandler is true for handleWithContext`` (n: int) =
    let def =
        grain {
            defaultState n
            handleWithContext (fun _ctx state ((): unit) ->
                task { return state, box state })
        }
    GrainDefinition.hasAnyHandler def

[<Property>]
let ``hasAnyHandler is true for handleCancellable`` (n: int) =
    let def =
        grain {
            defaultState n
            handleCancellable (fun state ((): unit) _ct ->
                task { return state, box state })
        }
    GrainDefinition.hasAnyHandler def
