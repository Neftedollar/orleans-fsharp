/// <summary>
/// The production single-silo fixture for spec 003 Phase 5: collection age, declared reminders,
/// declared timers, deactivation ordering with timer disposal, and functional-context
/// completion (TimeProvider, RequestContext accessors, tracing).
/// </summary>
/// <remarks>
/// <c>GrainCollectionOptions.CollectionQuantum</c> and every grain's <c>collectionAge</c> are
/// configured short here so collection-eligibility tests stay fast and bounded, per the task
/// brief. A single silo is enough for every test in this fixture; heterogeneous and multi-silo
/// routing are already covered by the Phase 3/4 fixtures.
/// </remarks>
module Orleans.FSharp.Integration.FunctionalPhase5Fixture

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.Timers
open Orleans.FSharp
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Shared probe: monotonic ticks, activation counts, and named "stage" observations
// ──────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module Phase5Probe =
    let private clock = ref 0

    /// <summary>The next value of a process-wide monotonic tick, used to order observations.</summary>
    let tick () = Interlocked.Increment clock

    let private stageTicks = ConcurrentDictionary<string, int>()
    let private stageCounts = ConcurrentDictionary<string, int>()
    let private disposedTimers = ConcurrentDictionary<string, int>()
    let private reminderCounts = ConcurrentDictionary<string, int>()
    let private reminderTokens = ConcurrentDictionary<string, bool>()
    let private activations = ConcurrentDictionary<string, int>()
    let private timerTicks = ConcurrentDictionary<string, int>()
    let private timerTokens = ConcurrentDictionary<string, bool>()
    let private inFlight = ConcurrentDictionary<string, int>()
    let private callOutcomes = ConcurrentDictionary<string, int>()

    /// <summary>
    /// Record one occurrence of a named ordering stage <b>for one specific grain</b>, keeping its
    /// first tick and a count.
    /// </summary>
    /// <remarks>
    /// Keying on the stage name alone would make every ordering assertion cluster-wide rather
    /// than grain-scoped: this fixture's silo is shared by every test in the collection, the
    /// runtime emits its timer-disposal and DisposeInstance traces for <b>every</b> functional
    /// activation it tears down (including collected grains from other tests, and including
    /// activations that own no timers at all), and the deactivation hook fires for every instance
    /// of the timer grain type. A background collection landing inside another test's poll window
    /// would then both break "exactly once" and be able to satisfy stage presence and ordering
    /// without the grain under test doing anything. Both trace lines already carry the grain id,
    /// so every stage is recorded under it.
    /// </remarks>
    let recordStage (name: string) (grainId: string) =
        let key = $"{name}|{grainId}"
        stageTicks.TryAdd(key, tick ()) |> ignore
        stageCounts.AddOrUpdate(key, 1, fun _ current -> current + 1) |> ignore

    let stageTick (name: string) (grainId: string) =
        match stageTicks.TryGetValue $"{name}|{grainId}" with
        | true, value -> Some value
        | _ -> None

    let stageCount (name: string) (grainId: string) =
        match stageCounts.TryGetValue $"{name}|{grainId}" with
        | true, value -> value
        | _ -> 0

    let resetStages () =
        stageTicks.Clear()
        stageCounts.Clear()
        disposedTimers.Clear()

    /// <summary>How many timer handles the runtime reported disposing for one grain.</summary>
    let recordDisposedTimers (grainId: string) (count: int) =
        disposedTimers.AddOrUpdate(grainId, count, fun _ current -> current + count) |> ignore

    let disposedTimerCount (grainId: string) =
        match disposedTimers.TryGetValue grainId with
        | true, value -> value
        | _ -> 0

    /// <summary>Count one handler entry, used to gate on "every concurrent call is really in flight".</summary>
    let enterCall (grainId: string) =
        inFlight.AddOrUpdate(grainId, 1, fun _ current -> current + 1) |> ignore

    let inFlightCount (grainId: string) =
        match inFlight.TryGetValue grainId with
        | true, value -> value
        | _ -> 0

    /// <summary>
    /// What one handler invocation observed on its own context token, recorded server-side.
    /// The caller's own task cannot tell "the target's token fired" from "my client-side wait
    /// was abandoned", so cancellation propagation and cancellation isolation are both judged
    /// from what the handlers themselves saw.
    /// </summary>
    let recordCallOutcome (grainId: string) (outcome: string) =
        callOutcomes.AddOrUpdate($"{grainId}:{outcome}", 1, fun _ current -> current + 1)
        |> ignore

    let callOutcomeCount (grainId: string) (outcome: string) =
        match callOutcomes.TryGetValue $"{grainId}:{outcome}" with
        | true, value -> value
        | _ -> 0

    let recordActivation (grainId: string) =
        activations.AddOrUpdate(grainId, 1, fun _ current -> current + 1) |> ignore

    let activationCount (grainId: string) =
        match activations.TryGetValue grainId with
        | true, value -> value
        | _ -> 0

    let recordReminderTick (grainId: string) (reminderName: string) (canBeCancelled: bool) =
        let key = $"{grainId}:{reminderName}"
        reminderCounts.AddOrUpdate(key, 1, fun _ current -> current + 1) |> ignore
        reminderTokens.[key] <- canBeCancelled

    let reminderTickCount (grainId: string) (reminderName: string) =
        match reminderCounts.TryGetValue $"{grainId}:{reminderName}" with
        | true, value -> value
        | _ -> 0

    let reminderTokenCouldCancel (grainId: string) (reminderName: string) =
        match reminderTokens.TryGetValue $"{grainId}:{reminderName}" with
        | true, value -> Some value
        | _ -> None

    let recordTimerTick (grainId: string) (timerName: string) (canBeCancelled: bool) =
        let key = $"{grainId}:{timerName}"
        timerTicks.AddOrUpdate(key, 1, fun _ current -> current + 1) |> ignore
        timerTokens.[key] <- canBeCancelled

    let timerTickCount (grainId: string) (timerName: string) =
        match timerTicks.TryGetValue $"{grainId}:{timerName}" with
        | true, value -> value
        | _ -> 0

    let timerTokenCouldCancel (grainId: string) (timerName: string) =
        match timerTokens.TryGetValue $"{grainId}:{timerName}" with
        | true, value -> Some value
        | _ -> None

// ──────────────────────────────────────────────────────────────────────────────
// Log capture: the runtime's own Debug-level dispatch/timer-disposal/DisposeInstance traces
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Captures every log line at Debug level or above. Unlike the Warning-and-up capture used by
/// the Phase 4 fixture, Phase 5's deactivation-ordering and tracing tests need the runtime's
/// Debug-level "timers disposed" / "DisposeInstance completed" / "dispatch ... outcome=" lines,
/// none of which are warnings or errors.
/// </summary>
[<RequireQualifiedAccess>]
module Phase5LogCapture =
    let entries = ConcurrentQueue<string>()

    let clear () = entries.Clear()

    let contains (fragment: string) =
        entries |> Seq.exists (fun entry -> entry.Contains(fragment, StringComparison.Ordinal))

    let entriesContaining (fragment: string) =
        entries |> Seq.filter (fun entry -> entry.Contains(fragment, StringComparison.Ordinal)) |> Seq.toList

/// <summary>
/// Both runtime trace lines this fixture reads stages from end with the grain identity
/// ("... for grain {GrainId}"), which is what lets an ordering stage be attributed to the grain
/// under test instead of to whatever else the shared silo happened to tear down at the time.
/// </summary>
[<RequireQualifiedAccess>]
module Phase5Trace =
    [<Literal>]
    let private GrainMarker = "for grain "

    let tryGrainId (rendered: string) =
        match rendered.LastIndexOf(GrainMarker, StringComparison.Ordinal) with
        | -1 -> None
        | index -> Some(rendered.Substring(index + GrainMarker.Length).Trim())

    /// <summary>The handle count out of "... disposed: {TimerCount} handle(s) for grain ...".</summary>
    let tryDisposedCount (rendered: string) =
        let opening = rendered.IndexOf("disposed: ", StringComparison.Ordinal)
        let closing = rendered.IndexOf(" handle(s)", StringComparison.Ordinal)

        if opening < 0 || closing <= opening then
            None
        else
            let start = opening + "disposed: ".Length

            match Int32.TryParse(rendered.Substring(start, closing - start)) with
            | true, value -> Some value
            | _ -> None

[<Sealed>]
type private Phase5CaptureLogger(category: string) =
    interface ILogger with
        member _.BeginScope<'TState>(_state: 'TState) =
            { new IDisposable with
                member _.Dispose() = () }

        member _.IsEnabled(level: LogLevel) = level >= LogLevel.Debug

        member _.Log<'TState>(level, _eventId, state: 'TState, error: exn, formatter: Func<'TState, exn, string>) =
            if level >= LogLevel.Debug then
                let rendered = formatter.Invoke(state, error)
                Phase5LogCapture.entries.Enqueue $"{category}|{rendered}"

                // Deactivation-ordering stages 3 and 4 are otherwise opaque runtime internals:
                // this is the only place outside the runtime itself that can observe them, since
                // they are not exposed through any typed API. Both are attributed to the grain
                // named in the trace line, never recorded cluster-wide.
                if rendered.Contains("Functional timers of grain type", StringComparison.Ordinal) then
                    match Phase5Trace.tryGrainId rendered with
                    | Some grainId ->
                        Phase5Probe.recordStage "timers-disposed" grainId

                        match Phase5Trace.tryDisposedCount rendered with
                        | Some count -> Phase5Probe.recordDisposedTimers grainId count
                        | None -> ()
                    | None -> ()
                elif rendered.Contains("Functional DisposeInstance completed for grain", StringComparison.Ordinal) then
                    match Phase5Trace.tryGrainId rendered with
                    | Some grainId -> Phase5Probe.recordStage "dispose-instance" grainId
                    | None -> ()

[<Sealed>]
type Phase5LogProvider() =
    interface ILoggerProvider with
        member _.CreateLogger(category: string) = Phase5CaptureLogger category :> ILogger
        member _.Dispose() = ()

// ──────────────────────────────────────────────────────────────────────────────
// A stop-stage witness: the "lifecycle OnStop" stage of the deactivation order
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A grain-lifecycle observer subscribed at <c>GrainLifecycleStage.SetupState</c>. Stop stages
/// run in reverse order, so its stop callback runs AFTER the stage which invokes
/// <c>IGrainBase.OnDeactivateAsync</c> — which is where the functional <c>onDeactivate</c> hook
/// lives — and before <c>IGrainActivator.DisposeInstance</c> disposes the instance. That makes
/// it the observation point for stage 2 of the four-stage deactivation order, which is otherwise
/// invisible to the Phase 5 suite (the Task-5 witness lives on a different fixture, whose cluster
/// hosts no timers at all, so the "OnStop before timer disposal" link has no observer there).
/// </summary>
[<Sealed>]
type Phase5StopStageWitness() =

    interface IConfigureGrainContextProvider with
        member this.TryGetConfigurator
            (_grainType: GrainType, _properties: Orleans.Metadata.GrainProperties, configurator: byref<IConfigureGrainContext>)
            =
            configurator <- this
            true

    interface IConfigureGrainContext with
        member _.Configure(context: IGrainContext) =
            context.ObservableLifecycle.Subscribe(
                "Orleans.FSharp.Integration.Phase5StopStageWitness",
                GrainLifecycleStage.SetupState,
                Func<CancellationToken, Task>(fun _ -> Task.CompletedTask),
                Func<CancellationToken, Task>(fun _ ->
                    Phase5Probe.recordStage "lifecycle-stop" (string context.GrainId)
                    Task.CompletedTask)
            )
            |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// A settable TimeProvider: proves a custom application clock stays authoritative
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// Tracks real time by delegating to <c>TimeProvider.System</c> until briefly overridden. This
/// same instance is registered for the whole silo, and Orleans' own internal scheduling (the
/// activation collector, timers, reminders) resolves the identical registered <c>TimeProvider</c>
/// — "silo registration ... so an application-provided clock remains authoritative" is exactly
/// what makes it authoritative for a grain's <c>utcNow</c>. A permanently frozen fake clock would
/// therefore also freeze collection-age and timer scheduling for the rest of the fixture, so the
/// override here is deliberately momentary: set immediately before the one call under test, reset
/// immediately after, so every other subsystem in the shared silo keeps advancing in real time.
/// </remarks>
[<Sealed>]
type SettableTimeProvider() =
    inherit TimeProvider()
    let mutable overridden: DateTimeOffset option = None
    member _.Set(value: DateTimeOffset) = overridden <- Some value
    member _.Reset() = overridden <- None

    override _.GetUtcNow() =
        match overridden with
        | Some value -> value
        | None -> TimeProvider.System.GetUtcNow()

// ──────────────────────────────────────────────────────────────────────────────
// Reminder grain: reconciliation, survival across deactivation/reactivation, token = None
// ──────────────────────────────────────────────────────────────────────────────

type ReminderActor = private ReminderActor of unit

[<NoEquality; NoComparison>]
type ReminderApi =
    { touch: unit -> Task<unit>
      ticks: unit -> Task<int * int>
      goAway: unit -> Task<unit> }

type ReminderState = { tickA: int; tickB: int }

[<RequireQualifiedAccess>]
module Phase5GrainTypes =
    [<Literal>]
    let Reminder = "phase5.reminder"

    [<Literal>]
    let Timer = "phase5.timer"

    [<Literal>]
    let TimerKeepAlive = "phase5.timer.keepalive"

    [<Literal>]
    let Collection = "phase5.collection"

    [<Literal>]
    let CollectionEphemeral = "phase5.collection.ephemeral"

    [<Literal>]
    let Context = "phase5.context"

    /// <summary>
    /// A definition that declares no reminders at all — the "redeployed definition which dropped
    /// the declaration" half of the rename/removal migration.
    /// </summary>
    [<Literal>]
    let StaleReminder = "phase5.reminder.stale"

/// <summary>The reminder name a previous deployment is imagined to have declared.</summary>
[<Literal>]
let GhostReminderName = "ghost"

/// <summary>Every declared reminder period on this fixture; short enough to observe fast.</summary>
[<RequireQualifiedAccess>]
module Phase5Timing =
    let ReminderPeriod = TimeSpan.FromSeconds 1.0
    let MinimumReminderPeriod = TimeSpan.FromMilliseconds 200.0
    let CollectionQuantum = TimeSpan.FromMilliseconds 500.0
    let CollectionAge = TimeSpan.FromSeconds 1.5
    let HostCollectionAge = TimeSpan.FromSeconds 6.0
    let TimerPeriod = TimeSpan.FromMilliseconds 200.0

let private reminderContract =
    grainContract<ReminderActor, string, ReminderApi> () {
        grainType Phase5GrainTypes.Reminder
        stringKey
        readOnly (_.ticks)
    }

let private reminderState = PersistentState.create<ReminderState> "reminder" "Phase5Default"

let private reminderDefinition =
    grainFor reminderContract {
        defaultState (fun () -> { tickA = 0; tickB = 0 })
        stateFrom reminderState

        onActivate (fun context state ->
            task {
                Phase5Probe.recordActivation (string context.grainId)
                return state
            })

        // Two declared reminders: reconciliation must register both, in declaration order.
        onReminder "tick-a" TimeSpan.Zero Phase5Timing.ReminderPeriod (fun context state status ->
            task {
                Phase5Probe.recordReminderTick
                    (string context.grainId)
                    "tick-a"
                    context.cancellationToken.CanBeCanceled

                ignore status
                return { state with tickA = state.tickA + 1 }
            })

        onReminder "tick-b" TimeSpan.Zero Phase5Timing.ReminderPeriod (fun context state status ->
            task {
                Phase5Probe.recordReminderTick
                    (string context.grainId)
                    "tick-b"
                    context.cancellationToken.CanBeCanceled

                ignore status
                return { state with tickB = state.tickB + 1 }
            })

        handle (_.touch) (fun _ state () -> task { return state, () })
        handle (_.ticks) (fun _ state () -> task { return state, (state.tickA, state.tickB) })

        handle (_.goAway) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return state, ()
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Stale-reminder grain: the rename/removal migration
// ──────────────────────────────────────────────────────────────────────────────

type StaleReminderActor = private StaleReminderActor of unit

[<NoEquality; NoComparison>]
type StaleReminderApi =
    { ping: unit -> Task<unit>
      /// Registers the durable reminder the *previous* deployment declared.
      registerGhost: unit -> Task<unit>
      /// The documented migration: unregister the stale name explicitly.
      unregisterGhost: unit -> Task<bool>
      /// The reminder names still registered for this grain in the reminder table.
      registeredNames: unit -> Task<string list> }

type StaleReminderState = { pings: int }

let private staleReminderContract =
    grainContract<StaleReminderActor, string, StaleReminderApi> () {
        grainType Phase5GrainTypes.StaleReminder
        stringKey
        readOnly (_.registeredNames)
    }

/// <remarks>
/// This definition declares NO reminder, which is exactly the state of the world after a
/// redeployment that dropped an <c>onReminder</c> declaration: the durable registration in the
/// reminder table outlives the code that declared it. The functional context surface exposes no
/// reminder API by design, so the migration goes through the stock
/// <see cref="T:Orleans.Timers.IReminderRegistry"/> resolved from <c>context.services</c> — the
/// same registry the runtime's own <c>RegisterOrUpdateReminder</c> reconciliation uses.
/// </remarks>
let private staleReminderDefinition =
    grainFor staleReminderContract {
        defaultState (fun () -> { pings = 0 })

        onActivate (fun context state ->
            task {
                Phase5Probe.recordActivation (string context.grainId)
                return state
            })

        handle (_.ping) (fun _ state () -> task { return { pings = state.pings + 1 }, () })

        handle (_.registerGhost) (fun context state () ->
            task {
                let registry = context.services.GetRequiredService<IReminderRegistry>()

                let! _ =
                    registry.RegisterOrUpdateReminder(
                        context.grainId,
                        GhostReminderName,
                        TimeSpan.Zero,
                        Phase5Timing.ReminderPeriod
                    )

                return state, ()
            })

        handle (_.unregisterGhost) (fun context state () ->
            task {
                let registry = context.services.GetRequiredService<IReminderRegistry>()
                let! existing = registry.GetReminder(context.grainId, GhostReminderName)

                if obj.ReferenceEquals(existing, null) then
                    return state, false
                else
                    do! registry.UnregisterReminder(context.grainId, existing)
                    return state, true
            })

        handle (_.registeredNames) (fun context state () ->
            task {
                let registry = context.services.GetRequiredService<IReminderRegistry>()
                let! reminders = registry.GetReminders context.grainId
                return state, [ for reminder in reminders -> reminder.ReminderName ]
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Timer grains: creation, whole-state replacement, KeepAlive, and 4-stage deactivation order
// ──────────────────────────────────────────────────────────────────────────────

[<NoEquality; NoComparison>]
type TimerApi =
    { touch: unit -> Task<unit>
      snapshot: unit -> Task<int>
      goAway: unit -> Task<unit> }

type TimerState = { polls: int }

type TimerActor = private TimerActor of unit
type TimerKeepAliveActor = private TimerKeepAliveActor of unit

let private timerContract =
    grainContract<TimerActor, string, TimerApi> () {
        grainType Phase5GrainTypes.Timer
        stringKey
        readOnly (_.snapshot)
    }

let private timerDefinition =
    grainFor timerContract {
        defaultState (fun () -> { polls = 0 })

        onActivate (fun context state ->
            task {
                Phase5Probe.recordActivation (string context.grainId)
                return state
            })

        onDeactivate (fun context _reason _state ->
            task { Phase5Probe.recordStage "deactivate-hook" (string context.grainId) })

        onTimer
            "poll"
            (GrainTimerCreationOptions(DueTime = TimeSpan.Zero, Period = Phase5Timing.TimerPeriod, KeepAlive = false))
            (fun context state ->
                task {
                    Phase5Probe.recordTimerTick (string context.grainId) "poll" context.cancellationToken.CanBeCanceled
                    return { state with polls = state.polls + 1 }
                })

        handle (_.touch) (fun _ state () -> task { return state, () })
        handle (_.snapshot) (fun _ state () -> task { return state, state.polls })

        handle (_.goAway) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return state, ()
            })
    }

let private timerKeepAliveContract =
    grainContract<TimerKeepAliveActor, string, TimerApi> () {
        grainType Phase5GrainTypes.TimerKeepAlive
        stringKey
        readOnly (_.snapshot)
    }

let private timerKeepAliveDefinition =
    grainFor timerKeepAliveContract {
        defaultState (fun () -> { polls = 0 })
        collectionAge Phase5Timing.CollectionAge

        onActivate (fun context state ->
            task {
                Phase5Probe.recordActivation (string context.grainId)
                return state
            })

        onTimer
            "poll"
            (GrainTimerCreationOptions(DueTime = TimeSpan.Zero, Period = Phase5Timing.TimerPeriod, KeepAlive = true))
            (fun context state ->
                task {
                    Phase5Probe.recordTimerTick (string context.grainId) "poll" context.cancellationToken.CanBeCanceled
                    return { state with polls = state.polls + 1 }
                })

        handle (_.touch) (fun _ state () -> task { return state, () })
        handle (_.snapshot) (fun _ state () -> task { return state, state.polls })
        handle (_.goAway) (fun context state () -> task { context.deactivateOnIdle (); return state, () })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Collection grain: collectionAge override produces stock eligibility + durable reactivation
// ──────────────────────────────────────────────────────────────────────────────

type CollectionActor = private CollectionActor of unit

[<NoEquality; NoComparison>]
type CollectionApi =
    { writeNow: string -> Task<unit>
      snapshot: unit -> Task<string> }

type CollectionState = { marker: string }

let private collectionContract =
    grainContract<CollectionActor, string, CollectionApi> () {
        grainType Phase5GrainTypes.Collection
        stringKey
        readOnly (_.snapshot)
    }

let private collectionState = PersistentState.create<CollectionState> "collection" "Phase5Default"

let private collectionDefinition =
    grainFor collectionContract {
        defaultState (fun () -> { marker = "" })
        stateFrom collectionState
        collectionAge Phase5Timing.CollectionAge

        onActivate (fun context state ->
            task {
                Phase5Probe.recordActivation (string context.grainId)
                return state
            })

        handle (_.writeNow) (fun context state (value: string) ->
            task {
                let next = { marker = value }
                let facade = context.persistentState collectionState
                facade.State <- next
                do! facade.WriteStateAsync()
                return next, ()
            })

        handle (_.snapshot) (fun _ state () -> task { return state, state.marker })
    }

/// <summary>
/// An ephemeral twin of <see cref="T:Orleans.FSharp.Integration.FunctionalPhase5Fixture.CollectionActor"/>:
/// same collectionAge override, but no attached persistent state at all, so a collected and
/// reactivated instance has nothing to reload and must re-run its initializer instead.
/// </summary>
type CollectionEphemeralActor = private CollectionEphemeralActor of unit

let private collectionEphemeralContract =
    grainContract<CollectionEphemeralActor, string, CollectionApi> () {
        grainType Phase5GrainTypes.CollectionEphemeral
        stringKey
        readOnly (_.snapshot)
    }

let private collectionEphemeralDefinition =
    grainFor collectionEphemeralContract {
        defaultState (fun () -> { marker = "" })
        collectionAge Phase5Timing.CollectionAge

        onActivate (fun context state ->
            task {
                Phase5Probe.recordActivation (string context.grainId)
                return state
            })

        // An in-memory-only mutation, never written anywhere: it can only survive if the SAME
        // activation is still alive. A reactivation always re-runs the initializer and observes
        // the pristine "" marker, never this value.
        handle (_.writeNow) (fun _ state (value: string) -> task { return { marker = value }, () })
        handle (_.snapshot) (fun _ state () -> task { return state, state.marker })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Context grain: TimeProvider, RequestContext accessors, concurrency isolation, tracing
// ──────────────────────────────────────────────────────────────────────────────

type ContextActor = private ContextActor of unit

[<NoEquality; NoComparison>]
type ContextApi =
    { clock: unit -> Task<DateTimeOffset>
      echoRequestContext: unit -> Task<string>
      roundTripRequestContext: unit -> Task<string * string>
      /// Waits for the supplied number of milliseconds on the invocation's own token.
      waitCancel: int -> Task<string>
      boom: unit -> Task<unit> }

type ContextState = { touched: bool }

let private contextContract =
    grainContract<ContextActor, string, ContextApi> () {
        grainType Phase5GrainTypes.Context
        stringKey
        readOnly (_.clock)
        readOnly (_.echoRequestContext)
        readOnly (_.roundTripRequestContext)
        // Read-only requests interleave with each other (proven in SeamProof item 7), which is
        // what lets several waitCancel calls be genuinely in flight on ONE activation at once —
        // the only arrangement in which a cancellation could leak between invocation contexts.
        readOnly (_.waitCancel)
    }

let private contextDefinition =
    grainFor contextContract {
        defaultState (fun () -> { touched = false })

        handle (_.clock) (fun context state () -> task { return state, context.utcNow })

        handle (_.echoRequestContext) (fun context state () ->
            task {
                let value =
                    match context.tryGetRequestContext<string> "phase5-probe" with
                    | Some value -> value
                    | None -> "<none>"

                return state, value
            })

        handle (_.roundTripRequestContext) (fun context state () ->
            task {
                let before =
                    match context.tryGetRequestContext<string> "phase5-roundtrip" with
                    | Some value -> value
                    | None -> "<none>"

                context.setRequestContext "phase5-roundtrip" "set-by-target"
                context.removeRequestContext "phase5-roundtrip"

                let after =
                    match context.tryGetRequestContext<string> "phase5-roundtrip" with
                    | Some value -> value
                    | None -> "<none>"

                return state, (before, after)
            })

        handle (_.waitCancel) (fun context state (milliseconds: int) ->
            task {
                Phase5Probe.enterCall (string context.grainId)

                try
                    do! Task.Delay(milliseconds, context.cancellationToken)
                    Phase5Probe.recordCallOutcome (string context.grainId) "completed"
                    return state, "completed"
                with :? OperationCanceledException ->
                    Phase5Probe.recordCallOutcome (string context.grainId) "cancelled"
                    return state, "cancelled"
            })

        handle (_.boom) (fun _ state () -> task { return raise (ApplicationException "boom-for-tracing") })
    }

let reminderRef = FunctionalGrain.ref reminderContract
let staleReminderRef = FunctionalGrain.ref staleReminderContract
let timerRef = FunctionalGrain.ref timerContract
let timerKeepAliveRef = FunctionalGrain.ref timerKeepAliveContract
let collectionRef = FunctionalGrain.ref collectionContract
let collectionEphemeralRef = FunctionalGrain.ref collectionEphemeralContract
let contextRef = FunctionalGrain.ref contextContract

/// <summary>The raw reference exposes <c>callCancellable</c>, which the cancellation-leak test needs.</summary>
let contextRawRef = FunctionalGrain.rawRef contextContract

// ──────────────────────────────────────────────────────────────────────────────
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

let settableTimeProvider = SettableTimeProvider()

type Phase5SiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage "Phase5Default" |> ignore

            siloBuilder.UseInMemoryReminderService() |> ignore

            siloBuilder.Services.Configure<ReminderOptions>(fun (options: ReminderOptions) ->
                options.MinimumReminderPeriod <- Phase5Timing.MinimumReminderPeriod)
            |> ignore

            siloBuilder.Services.Configure<GrainCollectionOptions>(fun (options: GrainCollectionOptions) ->
                options.CollectionQuantum <- Phase5Timing.CollectionQuantum
                options.CollectionAge <- Phase5Timing.HostCollectionAge)
            |> ignore

            // A custom application TimeProvider registered before AddFunctionalGrain's
            // TryAddSingleton<TimeProvider> stays authoritative.
            siloBuilder.Services.AddSingleton<TimeProvider>(settableTimeProvider :> TimeProvider)
            |> ignore

            siloBuilder.AddFunctionalGrain reminderDefinition |> ignore
            siloBuilder.AddFunctionalGrain staleReminderDefinition |> ignore
            siloBuilder.AddFunctionalGrain timerDefinition |> ignore
            siloBuilder.AddFunctionalGrain timerKeepAliveDefinition |> ignore
            siloBuilder.AddFunctionalGrain collectionDefinition |> ignore
            siloBuilder.AddFunctionalGrain collectionEphemeralDefinition |> ignore
            siloBuilder.AddFunctionalGrain contextDefinition |> ignore

            siloBuilder.Services.AddSingleton<ILoggerProvider, Phase5LogProvider>() |> ignore

            // Stage 2 of the deactivation order: a per-activation lifecycle observer whose stop
            // callback runs after the stage that invokes the functional onDeactivate hook.
            siloBuilder.Services.AddSingleton<IConfigureGrainContextProvider, Phase5StopStageWitness>()
            |> ignore

            // Debug-level messages are filtered out by the default Information minimum, and
            // Phase5LogCapture specifically needs the runtime's Debug-level dispatch/timer-
            // disposal/DisposeInstance traces.
            siloBuilder.ConfigureLogging(fun logging -> logging.SetMinimumLevel LogLevel.Debug |> ignore)
            |> ignore

type Phase5ClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

[<Sealed>]
type Phase5ClusterFixture() =
    let cluster =
        let builder = TestClusterBuilder 1s
        builder.AddSiloBuilderConfigurator<Phase5SiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<Phase5ClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Cluster = cluster
    member _.Client = cluster.Client

    member _.Reminder(key: string) = reminderRef cluster.Client key
    member _.StaleReminder(key: string) = staleReminderRef cluster.Client key
    member _.Timer(key: string) = timerRef cluster.Client key
    member _.TimerKeepAlive(key: string) = timerKeepAliveRef cluster.Client key
    member _.Collection(key: string) = collectionRef cluster.Client key
    member _.CollectionEphemeral(key: string) = collectionEphemeralRef cluster.Client key
    member _.Context(key: string) = contextRef cluster.Client key
    member _.ContextRaw(key: string) = contextRawRef cluster.Client key

    member _.ReminderId(key: string) = $"{Phase5GrainTypes.Reminder}/{key}"
    member _.StaleReminderId(key: string) = $"{Phase5GrainTypes.StaleReminder}/{key}"
    member _.ContextId(key: string) = $"{Phase5GrainTypes.Context}/{key}"
    member _.TimerId(key: string) = $"{Phase5GrainTypes.Timer}/{key}"
    member _.TimerKeepAliveId(key: string) = $"{Phase5GrainTypes.TimerKeepAlive}/{key}"
    member _.CollectionId(key: string) = $"{Phase5GrainTypes.Collection}/{key}"
    member _.CollectionEphemeralId(key: string) = $"{Phase5GrainTypes.CollectionEphemeral}/{key}"

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("Phase5Cluster")>]
type Phase5ClusterCollection() =
    interface ICollectionFixture<Phase5ClusterFixture>
