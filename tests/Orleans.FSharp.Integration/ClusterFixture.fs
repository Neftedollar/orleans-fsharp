namespace Orleans.FSharp.Integration

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.Hosting
open Orleans.TestingHost
open Xunit
open Orleans.FSharp
open Orleans.FSharp.Runtime

// ── Test grain types for the universal IFSharpGrain pattern ──────────────────

/// <summary>State for the integration-test ping grain (universal pattern).</summary>
[<Orleans.GenerateSerializer>]
type PingState = { [<Orleans.Id(0u)>] Count: int }

/// <summary>Commands for the integration-test ping grain.</summary>
[<Orleans.GenerateSerializer>]
type PingCommand =
    | [<Orleans.Id(0u)>] Ping
    | [<Orleans.Id(1u)>] GetCount

/// <summary>State for the text-accumulator grain (tests field-carrying DU cases).</summary>
[<Orleans.GenerateSerializer>]
type TextState = { [<Orleans.Id(0u)>] Text: string }

/// <summary>
/// Commands for the text-accumulator grain.
/// <c>Append</c> carries a payload (a field-carrying DU case), exercising the
/// nested-type dispatch fix in <c>UniversalGrainHandlerRegistry</c>.
/// </summary>
[<Orleans.GenerateSerializer>]
type TextCommand =
    | [<Orleans.Id(0u)>] Append of string
    | [<Orleans.Id(1u)>] GetText

/// <summary>Definition of the ping grain used to test <c>FSharpGrain.ref</c>.</summary>
[<AutoOpen>]
module TestGrains =
    let pingGrain =
        grain {
            defaultState { Count = 0 }
            handle (fun state cmd ->
                task {
                    match cmd with
                    | Ping      ->
                        let ns = { Count = state.Count + 1 }
                        // Return the new state as both the persisted state and the caller result
                        // so that FSharpGrain.send<PingState, PingCommand> can cast the result.
                        return ns, box ns
                    | GetCount  -> return state, box state
                })
        }

    /// <summary>
    /// Text-accumulator grain definition used to verify that field-carrying F# DU cases
    /// (e.g. <c>Append of string</c>) are correctly dispatched by <c>UniversalGrainHandlerRegistry</c>.
    /// </summary>
    let textGrain =
        grain {
            defaultState { Text = "" }
            handle (fun state cmd ->
                task {
                    match cmd with
                    | Append s  ->
                        let ns = { Text = state.Text + s }
                        return ns, box ns
                    | GetText   -> return state, box state
                })
        }

/// <summary>
/// State for the query grain used to test <c>FSharpGrain.ask</c> — the handler returns typed
/// values (<c>int</c>, <c>string</c>) that are distinct from the state type.
/// </summary>
[<Orleans.GenerateSerializer>]
type QueryState =
    { [<Orleans.Id(0u)>] Value: int
      [<Orleans.Id(1u)>] Label: string }

/// <summary>Commands for the query grain.</summary>
[<Orleans.GenerateSerializer>]
type QueryCommand =
    | [<Orleans.Id(0u)>] SetValue of int
    | [<Orleans.Id(1u)>] SetLabel of string
    | [<Orleans.Id(2u)>] GetValue      // returns int, not QueryState
    | [<Orleans.Id(3u)>] GetLabel      // returns string, not QueryState
    | [<Orleans.Id(4u)>] GetSnapshot   // returns (int * string), not QueryState

module TestGrains2 =
    /// <summary>
    /// Query grain used to verify <c>FSharpGrain.ask</c>: every command returns a typed result
    /// that differs from the state, exercising the <c>'Result</c> type parameter of <c>ask</c>.
    /// Uses the low-level <c>handle</c> CE variant with manual <c>box</c>.
    /// </summary>
    let queryGrain =
        grain {
            defaultState { Value = 0; Label = "" }
            handle (fun state cmd ->
                task {
                    match cmd with
                    | SetValue n   ->
                        let ns = { state with Value = n }
                        // Return new state so FSharpGrain.send callers can cast to QueryState
                        return ns, box ns
                    | SetLabel lbl ->
                        let ns = { state with Label = lbl }
                        return ns, box ns
                    | GetValue     -> return state, box state.Value    // int ← ask target
                    | GetLabel     -> return state, box state.Label    // string ← ask target
                    | GetSnapshot  -> return state, box (state.Value, state.Label)  // tuple ← ask target
                })
        }


/// <summary>State for the calculator grain used to test <c>handleTyped</c>.</summary>
[<Orleans.GenerateSerializer>]
type CalcState =
    { [<Orleans.Id(0u)>] LastResult: int
      [<Orleans.Id(1u)>] OpCount: int }

/// <summary>Commands for the calculator grain. Every command returns an <c>int</c> result.</summary>
[<Orleans.GenerateSerializer>]
type CalcCommand =
    | [<Orleans.Id(0u)>] AddValues of int * int
    | [<Orleans.Id(1u)>] MultiplyValues of int * int
    | [<Orleans.Id(2u)>] GetLastResult
    | [<Orleans.Id(3u)>] GetOpCount

module TestGrains3 =
    /// <summary>
    /// Calculator grain defined with <c>handleTyped</c>: every command returns an <c>int</c>
    /// result without any manual <c>box</c> call. Demonstrates the clean
    /// <c>handleTyped</c> + <c>FSharpGrain.ask&lt;'S,'C,int&gt;</c> end-to-end pattern.
    /// </summary>
    let calcGrain =
        grain {
            defaultState { LastResult = 0; OpCount = 0 }
            handleTyped (fun state (cmd: CalcCommand) ->
                task {
                    match cmd with
                    | AddValues(a, b) ->
                        let r = a + b
                        return { LastResult = r; OpCount = state.OpCount + 1 }, r
                    | MultiplyValues(a, b) ->
                        let r = a * b
                        return { LastResult = r; OpCount = state.OpCount + 1 }, r
                    | GetLastResult ->
                        return state, state.LastResult
                    | GetOpCount ->
                        return state, state.OpCount
                })
        }

/// <summary>State for the handleState score accumulator grain.</summary>
[<Orleans.GenerateSerializer>]
type ScoreState =
    { [<Orleans.Id(0u)>] Score: int
      [<Orleans.Id(1u)>] Moves: int }

/// <summary>Commands for the handleState score accumulator grain.</summary>
[<Orleans.GenerateSerializer>]
type ScoreCommand =
    | [<Orleans.Id(0u)>] AddPoints of int
    | [<Orleans.Id(1u)>] SubtractPoints of int
    | [<Orleans.Id(2u)>] ResetScore

module TestGrains4 =
    /// <summary>
    /// Score accumulator defined with <c>handleState</c>: every command returns only the
    /// updated state — no result value, no manual <c>box</c>.
    /// Used to verify that <c>FSharpGrain.send</c> returns the updated state correctly
    /// when using the <c>handleState</c> CE variant.
    /// </summary>
    let scoreGrain =
        grain {
            defaultState { Score = 0; Moves = 0 }
            handleState (fun state (cmd: ScoreCommand) ->
                task {
                    match cmd with
                    | AddPoints n      -> return { Score = state.Score + n; Moves = state.Moves + 1 }
                    | SubtractPoints n -> return { Score = state.Score - n; Moves = state.Moves + 1 }
                    | ResetScore       -> return { Score = 0; Moves = state.Moves + 1 }
                })
        }

// ── handleWithContext test grain ──────────────────────────────────────────────

/// <summary>
/// State for the relay grain that uses <c>handleWithContext</c> to call another grain
/// via <c>ctx.GrainFactory</c> — the core test for context-aware grain handlers.
/// </summary>
[<Orleans.GenerateSerializer>]
type RelayState =
    { [<Orleans.Id(0u)>] PingsSent: int
      [<Orleans.Id(1u)>] LastPeerCount: int }

/// <summary>Commands for the relay grain.</summary>
[<Orleans.GenerateSerializer>]
type RelayCommand =
    /// <summary>Calls a peer PingGrain by key via grain-factory, sends Ping, records the result.</summary>
    | [<Orleans.Id(0u)>] ForwardPing of peerKey: string
    /// <summary>Returns the relay grain's current state snapshot.</summary>
    | [<Orleans.Id(1u)>] GetRelayState

module TestGrains5 =
    /// <summary>
    /// Relay grain defined with <c>handleWithContext</c>.  On <c>ForwardPing peerKey</c>,
    /// it resolves a peer <c>PingGrain</c> via <c>ctx.GrainFactory</c>, sends it a
    /// <c>Ping</c> command, and stores the returned peer count in its own state.
    /// This verifies that <c>GrainContext.GrainFactory</c> is correctly threaded through
    /// the universal-handler dispatch chain.
    /// </summary>
    let relayGrain =
        grain {
            defaultState { PingsSent = 0; LastPeerCount = 0 }
            handleWithContext (fun ctx state (cmd: RelayCommand) ->
                task {
                    match cmd with
                    | ForwardPing peerKey ->
                        let peer =
                            FSharpGrain.ref<PingState, PingCommand> ctx.GrainFactory peerKey
                        let! peerState = FSharpGrain.send Ping peer
                        let ns = { PingsSent = state.PingsSent + 1; LastPeerCount = peerState.Count }
                        return ns, box ns
                    | GetRelayState ->
                        return state, box state
                })
        }

// ── handleStateCancellable test grain ────────────────────────────────────────

/// <summary>State for the cancellable accumulator grain.</summary>
[<Orleans.GenerateSerializer>]
type CancellableAccState =
    { [<Orleans.Id(0u)>] Sum: int
      [<Orleans.Id(1u)>] Steps: int }

/// <summary>Commands for the cancellable accumulator grain.</summary>
[<Orleans.GenerateSerializer>]
type CancellableAccCommand =
    | [<Orleans.Id(0u)>] Accumulate of int
    | [<Orleans.Id(1u)>] GetAcc

module TestGrains6 =
    /// <summary>
    /// Cancellable accumulator using <c>handleStateCancellable</c>.
    /// Verifies that CancellationToken is threaded through the universal handler chain.
    /// </summary>
    let cancellableAccGrain =
        grain {
            defaultState { Sum = 0; Steps = 0 }
            handleStateCancellable (fun state (cmd: CancellableAccCommand) _ct ->
                task {
                    match cmd with
                    | Accumulate n ->
                        return { Sum = state.Sum + n; Steps = state.Steps + 1 }
                    | GetAcc ->
                        return state
                })
        }

// ── handleStateWithContext test grain ────────────────────────────────────────

/// <summary>State for the state-with-context grain.</summary>
[<Orleans.GenerateSerializer>]
type StateWithCtxState =
    { [<Orleans.Id(0u)>] SWCSum: int
      [<Orleans.Id(1u)>] SWCSteps: int
      [<Orleans.Id(2u)>] SWCPeerPings: int }

/// <summary>Commands for the state-with-context grain.</summary>
[<Orleans.GenerateSerializer>]
type StateWithCtxCommand =
    | [<Orleans.Id(0u)>] SWCAdd of int
    | [<Orleans.Id(1u)>] SWCForwardPing of peerKey: string
    | [<Orleans.Id(2u)>] GetSWCState

module TestGrains10 =
    /// <summary>
    /// State-with-context grain defined with <c>handleStateWithContext</c>.
    /// The handler receives a GrainContext and returns only the new state — no manual
    /// <c>box</c> required. Tests that <c>ContextHandler</c> is correctly populated and
    /// that <c>GrainFactory</c> is accessible from inside the handler.
    /// </summary>
    let stateWithCtxGrain =
        grain {
            defaultState { SWCSum = 0; SWCSteps = 0; SWCPeerPings = 0 }
            handleStateWithContext (fun ctx state (cmd: StateWithCtxCommand) ->
                task {
                    match cmd with
                    | SWCAdd n ->
                        return { state with SWCSum = state.SWCSum + n; SWCSteps = state.SWCSteps + 1 }
                    | SWCForwardPing peerKey ->
                        let peer = FSharpGrain.ref<PingState, PingCommand> ctx.GrainFactory peerKey
                        let! _ = FSharpGrain.send Ping peer
                        return { state with SWCPeerPings = state.SWCPeerPings + 1; SWCSteps = state.SWCSteps + 1 }
                    | GetSWCState ->
                        return state
                })
        }

// ── handleTypedWithContext test grain ────────────────────────────────────────

/// <summary>State for the typed-with-context calculator grain.</summary>
[<Orleans.GenerateSerializer>]
type TypedWithCtxState =
    { [<Orleans.Id(0u)>] TWCLastResult: int
      [<Orleans.Id(1u)>] TWCOps: int }

/// <summary>Commands for the typed-with-context calculator grain.</summary>
[<Orleans.GenerateSerializer>]
type TypedWithCtxCommand =
    | [<Orleans.Id(0u)>] TWCAdd of int * int
    | [<Orleans.Id(1u)>] TWCMul of int * int
    | [<Orleans.Id(2u)>] GetTWCLastResult
    | [<Orleans.Id(3u)>] GetTWCOps

module TestGrains11 =
    /// <summary>
    /// Typed-with-context calculator defined with <c>handleTypedWithContext</c>.
    /// The handler receives a GrainContext, returns a typed <c>int</c> result without
    /// manual <c>box</c>. Tests that <c>ContextHandler</c> correctly wraps typed results
    /// when dispatched through <c>FSharpGrain.ask</c>.
    /// </summary>
    let typedWithCtxGrain =
        grain {
            defaultState { TWCLastResult = 0; TWCOps = 0 }
            handleTypedWithContext (fun _ctx state (cmd: TypedWithCtxCommand) ->
                task {
                    match cmd with
                    | TWCAdd(a, b) ->
                        let r = a + b
                        return { TWCLastResult = r; TWCOps = state.TWCOps + 1 }, r
                    | TWCMul(a, b) ->
                        let r = a * b
                        return { TWCLastResult = r; TWCOps = state.TWCOps + 1 }, r
                    | GetTWCLastResult ->
                        return state, state.TWCLastResult
                    | GetTWCOps ->
                        return state, state.TWCOps
                })
        }

// ── handleStateWithContextCancellable test grain ─────────────────────────────

/// <summary>State for the state-with-context-cancellable grain.</summary>
[<Orleans.GenerateSerializer>]
type SWCCState =
    { [<Orleans.Id(0u)>] SWCCSum: int
      [<Orleans.Id(1u)>] SWCCPeerPings: int }

/// <summary>Commands for the state-with-context-cancellable grain.</summary>
[<Orleans.GenerateSerializer>]
type SWCCCommand =
    | [<Orleans.Id(0u)>] SWCCAdd of int
    | [<Orleans.Id(1u)>] SWCCForwardPing of peerKey: string
    | [<Orleans.Id(2u)>] GetSWCCState

module TestGrains12 =
    /// <summary>
    /// State-with-context-cancellable grain using <c>handleStateWithContextCancellable</c>.
    /// Returns only the new state — no manual <c>box</c> needed. Exercises the full
    /// context + CancellationToken combination via <c>CancellableContextHandler</c>.
    /// </summary>
    let swccGrain =
        grain {
            defaultState { SWCCSum = 0; SWCCPeerPings = 0 }
            handleStateWithContextCancellable (fun ctx state (cmd: SWCCCommand) _ct ->
                task {
                    match cmd with
                    | SWCCAdd n ->
                        return { state with SWCCSum = state.SWCCSum + n }
                    | SWCCForwardPing peerKey ->
                        let peer = FSharpGrain.ref<PingState, PingCommand> ctx.GrainFactory peerKey
                        let! _ = FSharpGrain.send Ping peer
                        return { state with SWCCPeerPings = state.SWCCPeerPings + 1 }
                    | GetSWCCState ->
                        return state
                })
        }

// ── handleTypedWithContextCancellable test grain ──────────────────────────────

/// <summary>State for the typed-with-context-cancellable calculator grain.</summary>
[<Orleans.GenerateSerializer>]
type TWCCState =
    { [<Orleans.Id(0u)>] TWCCLastResult: int
      [<Orleans.Id(1u)>] TWCCOps: int }

/// <summary>Commands for the typed-with-context-cancellable calculator grain.</summary>
[<Orleans.GenerateSerializer>]
type TWCCCommand =
    | [<Orleans.Id(0u)>] TWCCAdd of int * int
    | [<Orleans.Id(1u)>] TWCCMul of int * int
    | [<Orleans.Id(2u)>] GetTWCCLastResult
    | [<Orleans.Id(3u)>] GetTWCCOps

module TestGrains13 =
    /// <summary>
    /// Typed-with-context-cancellable calculator using <c>handleTypedWithContextCancellable</c>.
    /// Returns a typed <c>int</c> result — no manual <c>box</c> needed. Tests the full
    /// context + CT + typed-result combination via <c>CancellableContextHandler</c>.
    /// </summary>
    let twccGrain =
        grain {
            defaultState { TWCCLastResult = 0; TWCCOps = 0 }
            handleTypedWithContextCancellable (fun _ctx state (cmd: TWCCCommand) _ct ->
                task {
                    match cmd with
                    | TWCCAdd(a, b) ->
                        let r = a + b
                        return { TWCCLastResult = r; TWCCOps = state.TWCCOps + 1 }, r
                    | TWCCMul(a, b) ->
                        let r = a * b
                        return { TWCCLastResult = r; TWCCOps = state.TWCCOps + 1 }, r
                    | GetTWCCLastResult ->
                        return state, state.TWCCLastResult
                    | GetTWCCOps ->
                        return state, state.TWCCOps
                })
        }

// ── handleCancellable (raw) test grain ───────────────────────────────────────

/// <summary>State for the raw cancellable accumulator grain.</summary>
[<Orleans.GenerateSerializer>]
type RawCancState =
    { [<Orleans.Id(0u)>] RawSum: int
      [<Orleans.Id(1u)>] RawSteps: int }

/// <summary>Commands for the raw cancellable accumulator grain.</summary>
[<Orleans.GenerateSerializer>]
type RawCancCommand =
    | [<Orleans.Id(0u)>] RawCancAdd of int
    | [<Orleans.Id(1u)>] GetRawCancState

module TestGrains8 =
    /// <summary>
    /// Raw cancellable accumulator defined with <c>handleCancellable</c>.
    /// Unlike <c>handleStateCancellable</c>, this variant requires manual <c>box</c>.
    /// Verifies that the base <c>CancellableHandler</c> slot is dispatched correctly.
    /// </summary>
    let rawCancGrain =
        grain {
            defaultState { RawSum = 0; RawSteps = 0 }
            handleCancellable (fun state (cmd: RawCancCommand) _ct ->
                task {
                    match cmd with
                    | RawCancAdd n ->
                        let ns = { RawSum = state.RawSum + n; RawSteps = state.RawSteps + 1 }
                        return ns, box ns
                    | GetRawCancState ->
                        return state, box state
                })
        }

// ── handleTypedCancellable test grain ────────────────────────────────────────

/// <summary>State for the typed-cancellable calculator grain.</summary>
[<Orleans.GenerateSerializer>]
type TypedCancState =
    { [<Orleans.Id(0u)>] TypedLastResult: int
      [<Orleans.Id(1u)>] TypedOps: int }

/// <summary>Commands for the typed-cancellable calculator grain.</summary>
[<Orleans.GenerateSerializer>]
type TypedCancCommand =
    | [<Orleans.Id(0u)>] TypedCancAdd of int * int
    | [<Orleans.Id(1u)>] TypedCancMul of int * int
    | [<Orleans.Id(2u)>] GetTypedCancLastResult
    | [<Orleans.Id(3u)>] GetTypedCancOps

module TestGrains9 =
    /// <summary>
    /// Typed-cancellable calculator defined with <c>handleTypedCancellable</c>.
    /// Returns a typed <c>int</c> result without manual <c>box</c>, combining the
    /// convenience of <c>handleTyped</c> with CancellationToken support.
    /// </summary>
    let typedCancGrain =
        grain {
            defaultState { TypedLastResult = 0; TypedOps = 0 }
            handleTypedCancellable (fun state (cmd: TypedCancCommand) _ct ->
                task {
                    match cmd with
                    | TypedCancAdd(a, b) ->
                        let r = a + b
                        return { TypedLastResult = r; TypedOps = state.TypedOps + 1 }, r
                    | TypedCancMul(a, b) ->
                        let r = a * b
                        return { TypedLastResult = r; TypedOps = state.TypedOps + 1 }, r
                    | GetTypedCancLastResult ->
                        return state, state.TypedLastResult
                    | GetTypedCancOps ->
                        return state, state.TypedOps
                })
        }

// ── handleWithContextCancellable test grain ──────────────────────────────────

/// <summary>State for the context-cancellable accumulator grain.</summary>
[<Orleans.GenerateSerializer>]
type CtxCancAccState =
    { [<Orleans.Id(0u)>] Sum: int
      [<Orleans.Id(1u)>] Steps: int
      [<Orleans.Id(2u)>] PeerPings: int }

/// <summary>Commands for the context-cancellable accumulator grain.</summary>
[<Orleans.GenerateSerializer>]
type CtxCancAccCommand =
    /// <summary>Adds a value to the sum, exercising the basic cancellable-context path.</summary>
    | [<Orleans.Id(0u)>] CtxCancAdd of int
    /// <summary>Calls a peer PingGrain via <c>ctx.GrainFactory</c>, incrementing PeerPings.</summary>
    | [<Orleans.Id(1u)>] CtxCancForwardPing of peerKey: string
    /// <summary>Returns current state without side effects.</summary>
    | [<Orleans.Id(2u)>] GetCtxCancState

module TestGrains7 =
    /// <summary>
    /// Context-cancellable accumulator defined with <c>handleWithContextCancellable</c>.
    /// Exercises both the <c>GrainContext</c> (for grain-to-grain calls via
    /// <c>ctx.GrainFactory</c>) and the <c>CancellationToken</c> threaded through
    /// the universal handler dispatch chain.
    /// </summary>
    let ctxCancAccGrain =
        grain {
            defaultState { Sum = 0; Steps = 0; PeerPings = 0 }
            handleWithContextCancellable (fun ctx state (cmd: CtxCancAccCommand) _ct ->
                task {
                    match cmd with
                    | CtxCancAdd n ->
                        let ns = { state with Sum = state.Sum + n; Steps = state.Steps + 1 }
                        return ns, box ns
                    | CtxCancForwardPing peerKey ->
                        let peer = FSharpGrain.ref<PingState, PingCommand> ctx.GrainFactory peerKey
                        let! _peerState = FSharpGrain.send Ping peer
                        let ns = { state with PeerPings = state.PeerPings + 1; Steps = state.Steps + 1 }
                        return ns, box ns
                    | GetCtxCancState ->
                        return state, box state
                })
        }

/// <summary>
/// Silo configurator that adds memory grain storage and ensures the CodeGen assembly is loaded
/// for grain discovery by Orleans.
/// </summary>
type TestSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.AddMemoryStreams("StreamProvider") |> ignore
            siloBuilder.AddMemoryGrainStorage("PubSubStore") |> ignore
            siloBuilder.UseInMemoryReminderService() |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProviderAsDefault() |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider("LogStorage") |> ignore
            siloBuilder.AddBroadcastChannel("BroadcastProvider") |> ignore

            siloBuilder.Services.Configure<Orleans.Hosting.ReminderOptions>(fun (options: Orleans.Hosting.ReminderOptions) ->
                options.MinimumReminderPeriod <- TimeSpan.FromSeconds(1.0))
            |> ignore

            // Register the universal-pattern ping grain for FSharpGrain.ref tests.
            // FSharpBinaryCodec is registered automatically by AddFSharpGrain — no manual
            // FSharpBinaryCodecRegistration.addToSerializerBuilder call needed here.
            siloBuilder.Services.AddFSharpGrain<PingState, PingCommand>(pingGrain) |> ignore
            // Register the text-accumulator grain for field-carrying DU case dispatch tests
            siloBuilder.Services.AddFSharpGrain<TextState, TextCommand>(textGrain) |> ignore
            // Register the query grain for FSharpGrain.ask typed-result tests
            siloBuilder.Services.AddFSharpGrain<QueryState, QueryCommand>(TestGrains2.queryGrain) |> ignore
            // Register the calculator grain for handleTyped + ask end-to-end tests
            siloBuilder.Services.AddFSharpGrain<CalcState, CalcCommand>(TestGrains3.calcGrain) |> ignore
            // Register the score grain for handleState end-to-end tests
            siloBuilder.Services.AddFSharpGrain<ScoreState, ScoreCommand>(TestGrains4.scoreGrain) |> ignore
            // Register the relay grain for handleWithContext (grain-to-grain) tests
            siloBuilder.Services.AddFSharpGrain<RelayState, RelayCommand>(TestGrains5.relayGrain) |> ignore
            // Register the cancellable accumulator for handleStateCancellable tests
            siloBuilder.Services.AddFSharpGrain<CancellableAccState, CancellableAccCommand>(TestGrains6.cancellableAccGrain) |> ignore
            // Register the state-with-context-cancellable grain for handleStateWithContextCancellable tests
            siloBuilder.Services.AddFSharpGrain<SWCCState, SWCCCommand>(TestGrains12.swccGrain) |> ignore
            // Register the typed-with-context-cancellable grain for handleTypedWithContextCancellable tests
            siloBuilder.Services.AddFSharpGrain<TWCCState, TWCCCommand>(TestGrains13.twccGrain) |> ignore
            // Register the state-with-context grain for handleStateWithContext tests
            siloBuilder.Services.AddFSharpGrain<StateWithCtxState, StateWithCtxCommand>(TestGrains10.stateWithCtxGrain) |> ignore
            // Register the typed-with-context grain for handleTypedWithContext tests
            siloBuilder.Services.AddFSharpGrain<TypedWithCtxState, TypedWithCtxCommand>(TestGrains11.typedWithCtxGrain) |> ignore
            // Register the raw cancellable accumulator for handleCancellable tests
            siloBuilder.Services.AddFSharpGrain<RawCancState, RawCancCommand>(TestGrains8.rawCancGrain) |> ignore
            // Register the typed-cancellable calculator for handleTypedCancellable tests
            siloBuilder.Services.AddFSharpGrain<TypedCancState, TypedCancCommand>(TestGrains9.typedCancGrain) |> ignore
            // Register the context-cancellable accumulator for handleWithContextCancellable tests
            siloBuilder.Services.AddFSharpGrain<CtxCancAccState, CtxCancAccCommand>(TestGrains7.ctxCancAccGrain) |> ignore

/// <summary>
/// Client configurator that ensures the CodeGen assembly is loaded on the client side
/// for type alias resolution.
/// </summary>
type TestClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration, clientBuilder: IClientBuilder) =
            clientBuilder.AddMemoryStreams("StreamProvider") |> ignore

            // Register F# binary serialization on the client so the proxy can deep-copy
            // F# types passed as `object` to IFSharpGrain.HandleMessage.
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                clientBuilder.Services,
                System.Action<Orleans.Serialization.ISerializerBuilder>(fun b ->
                    FSharpBinaryCodecRegistration.addToSerializerBuilder b |> ignore))
            |> ignore

/// <summary>
/// Shared xUnit fixture that starts a TestCluster for integration tests.
/// Implements IAsyncLifetime for async setup and teardown.
/// </summary>
type ClusterFixture() =
    let mutable cluster: TestCluster = Unchecked.defaultof<TestCluster>

    /// <summary>Gets the running TestCluster instance.</summary>
    member _.Cluster = cluster

    /// <summary>Gets the GrainFactory for creating grain references.</summary>
    member _.GrainFactory = cluster.GrainFactory

    /// <summary>Gets the cluster client for advanced operations like streaming.</summary>
    member _.Client = cluster.Client

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                // Force Orleans assemblies to be loaded into the current AppDomain.
                // Orleans discovers grains by scanning loaded assemblies for ApplicationPartAttribute.
                // - CodeGen: per-grain proxies for Sample grains (legacy ICounterGrain etc.)
                // - Abstractions: universal IFSharpGrain proxies (new pattern)
                let codeGenAssembly = typeof<Orleans.FSharp.CodeGen.CodeGenAssemblyMarker>.Assembly
                let _ = codeGenAssembly.GetTypes()
                let abstractionsAssembly = typeof<Orleans.FSharp.IFSharpGrain>.Assembly
                let _ = abstractionsAssembly.GetTypes()

                let builder = TestClusterBuilder()
                builder.Options.InitialSilosCount <- 1s
                builder.AddSiloBuilderConfigurator<TestSiloConfigurator>() |> ignore
                builder.AddClientBuilderConfigurator<TestClientConfigurator>() |> ignore
                cluster <- builder.Build()
                do! cluster.DeployAsync()
            }

        member _.DisposeAsync() =
            task {
                if not (isNull (box cluster)) then
                    do! cluster.StopAllSilosAsync()
                    cluster.Dispose()
            }

/// <summary>
/// xUnit collection definition that shares a single ClusterFixture across all integration tests.
/// </summary>
[<CollectionDefinition("ClusterCollection")>]
type ClusterCollection() =
    interface ICollectionFixture<ClusterFixture>
