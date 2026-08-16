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
    let private reminderCounts = ConcurrentDictionary<string, int>()
    let private reminderTokens = ConcurrentDictionary<string, bool>()
    let private activations = ConcurrentDictionary<string, int>()
    let private timerTicks = ConcurrentDictionary<string, int>()
    let private timerTokens = ConcurrentDictionary<string, bool>()

    /// <summary>Record one occurrence of a named ordering stage, keeping its first tick and a count.</summary>
    let recordStage (name: string) =
        stageTicks.TryAdd(name, tick ()) |> ignore
        stageCounts.AddOrUpdate(name, 1, fun _ current -> current + 1) |> ignore

    let stageTick (name: string) =
        match stageTicks.TryGetValue name with
        | true, value -> Some value
        | _ -> None

    let stageCount (name: string) =
        match stageCounts.TryGetValue name with
        | true, value -> value
        | _ -> 0

    let resetStages () =
        stageTicks.Clear()
        stageCounts.Clear()

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
                // they are not exposed through any typed API.
                if rendered.Contains("Functional timers of grain type", StringComparison.Ordinal) then
                    Phase5Probe.recordStage "timers-disposed"
                elif rendered.Contains("Functional DisposeInstance completed for grain", StringComparison.Ordinal) then
                    Phase5Probe.recordStage "dispose-instance"

[<Sealed>]
type Phase5LogProvider() =
    interface ILoggerProvider with
        member _.CreateLogger(category: string) = Phase5CaptureLogger category :> ILogger
        member _.Dispose() = ()

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

        onDeactivate (fun _context _reason _state ->
            task { Phase5Probe.recordStage "deactivate-hook" })

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
      boom: unit -> Task<unit> }

type ContextState = { touched: bool }

let private contextContract =
    grainContract<ContextActor, string, ContextApi> () {
        grainType Phase5GrainTypes.Context
        stringKey
        readOnly (_.clock)
        readOnly (_.echoRequestContext)
        readOnly (_.roundTripRequestContext)
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

        handle (_.boom) (fun _ state () -> task { return raise (ApplicationException "boom-for-tracing") })
    }

let reminderRef = FunctionalGrain.ref reminderContract
let timerRef = FunctionalGrain.ref timerContract
let timerKeepAliveRef = FunctionalGrain.ref timerKeepAliveContract
let collectionRef = FunctionalGrain.ref collectionContract
let collectionEphemeralRef = FunctionalGrain.ref collectionEphemeralContract
let contextRef = FunctionalGrain.ref contextContract

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
            siloBuilder.AddFunctionalGrain timerDefinition |> ignore
            siloBuilder.AddFunctionalGrain timerKeepAliveDefinition |> ignore
            siloBuilder.AddFunctionalGrain collectionDefinition |> ignore
            siloBuilder.AddFunctionalGrain collectionEphemeralDefinition |> ignore
            siloBuilder.AddFunctionalGrain contextDefinition |> ignore

            siloBuilder.Services.AddSingleton<ILoggerProvider, Phase5LogProvider>() |> ignore

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
    member _.Timer(key: string) = timerRef cluster.Client key
    member _.TimerKeepAlive(key: string) = timerKeepAliveRef cluster.Client key
    member _.Collection(key: string) = collectionRef cluster.Client key
    member _.CollectionEphemeral(key: string) = collectionEphemeralRef cluster.Client key
    member _.Context(key: string) = contextRef cluster.Client key

    member _.ReminderId(key: string) = $"{Phase5GrainTypes.Reminder}/{key}"
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
