/// <summary>
/// The production two-silo fixture for spec 003 Phase 4: real persistent facets on two named
/// storage providers, every storage call observed by an instrumenting <c>IGrainStorage</c>
/// decorator, and real activation/deactivation ordering.
/// </summary>
/// <remarks>
/// The decorator wraps stock memory storage rather than replacing it, so the states still make
/// a real round trip through an Orleans storage provider — ETags, serialization and all — while
/// every read, write, and clear is counted. That is what lets the tests prove the runtime adds
/// no write, clear, or reload beyond the stock SetupState reads.
/// </remarks>
module Orleans.FSharp.Integration.FunctionalStateFixture

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Storage
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Storage instrumentation
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>One observed storage call.</summary>
type StorageCall =
    { Provider: string
      Operation: string
      StateName: string
      GrainId: string }

/// <summary>
/// Every storage call made through the instrumented providers, in order, plus the switches the
/// tests use to make one provider fail.
/// </summary>
[<RequireQualifiedAccess>]
module StorageLog =
    let calls = ConcurrentQueue<StorageCall>()

    /// <summary>Providers whose writes must fail, used by the partial-success test.</summary>
    let failingWrites = ConcurrentDictionary<string, bool>()

    let clear () =
        calls.Clear()
        failingWrites.Clear()

    let forGrain (grainId: string) =
        calls |> Seq.filter (fun call -> call.GrainId = grainId) |> Seq.toList

    let countFor (grainId: string) (operation: string) =
        forGrain grainId
        |> List.filter (fun call -> call.Operation = operation)
        |> List.length

/// <summary>An <c>IGrainStorage</c> decorator which records every call and then delegates.</summary>
[<Sealed>]
type InstrumentingGrainStorage(inner: IGrainStorage, provider: string) =

    let record operation (stateName: string) (grainId: GrainId) =
        StorageLog.calls.Enqueue
            { Provider = provider
              Operation = operation
              StateName = stateName
              GrainId = string grainId }

    interface IGrainStorage with
        member _.ReadStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            record "read" stateName grainId
            inner.ReadStateAsync<'T>(stateName, grainId, grainState)

        member _.WriteStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            record "write" stateName grainId

            match StorageLog.failingWrites.TryGetValue provider with
            | true, true ->
                Task.FromException(InvalidOperationException $"storage provider '{provider}' is configured to fail writes")
            | _ -> inner.WriteStateAsync<'T>(stateName, grainId, grainState)

        member _.ClearStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            record "clear" stateName grainId
            inner.ClearStateAsync<'T>(stateName, grainId, grainState)

// ──────────────────────────────────────────────────────────────────────────────
// Domain
// ──────────────────────────────────────────────────────────────────────────────

type LedgerActor = private LedgerActor of unit

type LedgerState =
    { entries: string list
      version: int }

type AuditState = { writes: int }

[<NoEquality; NoComparison>]
type LedgerApi =
    { /// Replaces the primary state in memory only.
      append: string -> Task<int>
      /// The current primary state as the handler sees it on entry.
      snapshot: unit -> Task<string>
      /// Sets and writes the primary holder explicitly.
      writeNow: string -> Task<unit>
      /// Enters with A, explicitly writes snapshot X, and returns Y.
      writeThenReturn: string -> Task<string>
      /// Writes X explicitly and then fails, so nothing is published and nothing rolls back.
      writeThenFail: string -> Task<string>
      /// Explicitly reloads the primary holder and reports the bound value beside the holder.
      reload: unit -> Task<string>
      /// Clears the primary record.
      clearNow: unit -> Task<unit>
      /// Writes the additional holder on its own provider.
      auditWrite: int -> Task<unit>
      /// Reads the additional holder.
      auditPeek: unit -> Task<int>
      /// Writes the primary holder and then the audit holder, in that order.
      orderedWrites: string -> Task<string>
      /// A read-only operation which tries the whole mutation surface and reports the outcome.
      readOnlyProbe: unit -> Task<string>
      /// The same probe under readOnly + alwaysInterleave.
      interleavedProbe: unit -> Task<string>
      /// Captures its facade so a later call can prove the facade expired.
      escapeFacade: unit -> Task<unit>
      /// Uses the captured facade and reports what happened.
      useEscapedFacade: unit -> Task<string>
      /// Looks up a descriptor which is not attached to this definition.
      unattached: unit -> Task<string>
      /// Tries to create a brand-new persistent facet after activation has started.
      createFacetNow: unit -> Task<string>
      /// The silo hosting this activation.
      whereAmI: unit -> Task<string>
      /// Arms the deactivation hook of this activation to fail.
      armFailingDeactivation: unit -> Task<unit>
      /// Arms the ACTIVATION hook of every later activation of this grain to fail.
      armFailingActivation: unit -> Task<unit>
      /// Requests deactivation once this turn completes.
      goAway: unit -> Task<unit> }

// ──────────────────────────────────────────────────────────────────────────────
// Cross-activation observation
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Facts which cannot travel in a reply: what the activation hook observed, what the
/// deactivation hook did, and how often an activation happened.
/// </summary>
[<RequireQualifiedAccess>]
module StateProbe =
    let observations = ConcurrentDictionary<string, string>()

    /// <summary>A process-wide monotonic tick, so observations can be ordered against each other.</summary>
    let private ticks = ref 0

    let tick () = Interlocked.Increment ticks
    let activations = ConcurrentDictionary<string, int>()
    let failingDeactivations = ConcurrentDictionary<string, bool>()
    let failingActivations = ConcurrentDictionary<string, bool>()

    /// <summary>The facade one handler deliberately let escape its invocation.</summary>
    let escaped = ConcurrentDictionary<string, IPersistentState<LedgerState>>()

    let record key value = observations.[key] <- value

    let tryGet key =
        match observations.TryGetValue key with
        | true, value -> Some value
        | _ -> None

    let activationCount key =
        match activations.TryGetValue key with
        | true, value -> value
        | _ -> 0

    let clear () =
        observations.Clear()
        activations.Clear()
        failingDeactivations.Clear()
        failingActivations.Clear()
        escaped.Clear()

let private describe (state: LedgerState) =
    $"v{state.version}:[{String.Join(',', state.entries)}]"

// ──────────────────────────────────────────────────────────────────────────────
// Contract, persistent state, and definition
// ──────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module FunctionalStateGrainTypes =
    [<Literal>]
    let Ledger = "functional.ledger"

    [<Literal>]
    let Ephemeral = "functional.ephemeral"

[<RequireQualifiedAccess>]
module FunctionalStateProviders =
    [<Literal>]
    let Ledger = "LedgerStore"

    [<Literal>]
    let Audit = "AuditStore"

/// <summary>The primary holder: same stored type as the definition's state.</summary>
let ledgerState =
    PersistentState.create<LedgerState> "ledger" FunctionalStateProviders.Ledger

/// <summary>An additional holder of an independent type on an independent provider.</summary>
let auditState =
    PersistentState.create<AuditState> "audit" FunctionalStateProviders.Audit

/// <summary>Never attached to any definition: the deterministic-failure arm of the lookup.</summary>
let detachedState =
    PersistentState.create<AuditState> "detached" FunctionalStateProviders.Audit

let ledgerContract =
    grainContract<LedgerActor, string, LedgerApi> () {
        grainType FunctionalStateGrainTypes.Ledger
        version 1
        stringKey

        readOnly (_.snapshot)
        readOnly (_.auditPeek)
        readOnly (_.readOnlyProbe)
        readOnly (_.unattached)
        readOnly (_.whereAmI)

        readOnly (_.interleavedProbe)
        alwaysInterleave (_.interleavedProbe)
    }

let private siloOf (context: FunctionalGrainContext<'Actor, string>) =
    match context.services.GetService typeof<ILocalSiloDetails> with
    | :? ILocalSiloDetails as details -> string details.SiloAddress
    | _ -> "?"

/// <summary>
/// Try one mutating member of a facade and report whether it was rejected. Used by the
/// read-only and expired-facade probes, which must cover the setter plus BOTH overloads of
/// read, write, and clear.
/// </summary>
let private probeMutations (facade: IPersistentState<LedgerState>) =
    let attempt (name: string) (action: unit -> Task) =
        task {
            try
                do! action ()
                return $"{name}=allowed"
            with error ->
                return $"{name}={error.GetType().Name}"
        }

    task {
        let results = ResizeArray<string>()

        let! setter =
            attempt "set" (fun () ->
                facade.State <- { entries = [ "hacked" ]; version = -1 }
                Task.CompletedTask)

        results.Add setter
        let! read = attempt "read" (fun () -> facade.ReadStateAsync())
        results.Add read
        let! write = attempt "write" (fun () -> facade.WriteStateAsync())
        results.Add write
        let! clear = attempt "clear" (fun () -> facade.ClearStateAsync())
        results.Add clear
        let! readToken = attempt "readToken" (fun () -> facade.ReadStateAsync CancellationToken.None)
        results.Add readToken
        let! writeToken = attempt "writeToken" (fun () -> facade.WriteStateAsync CancellationToken.None)
        results.Add writeToken
        let! clearToken = attempt "clearToken" (fun () -> facade.ClearStateAsync CancellationToken.None)
        results.Add clearToken

        return String.Join('|', results)
    }

let ledgerDefinition =
    grainFor ledgerContract {
        defaultState (fun () -> { entries = []; version = 0 })

        stateFrom ledgerState
        usePersistentState auditState (fun _ -> { writes = 0 })

        onActivate (fun context state ->
            task {
                let key = string context.grainId

                StateProbe.activations.AddOrUpdate(key, 1, fun _ current -> current + 1)
                |> ignore

                match StateProbe.failingActivations.TryGetValue key with
                | true, true -> raise (ApplicationException $"activation hook of {key} failed on purpose")
                | _ -> ()

                // The hook observes the state AFTER durable loading…
                StateProbe.record $"activate:{key}" (describe state)

                let audit = context.persistentState auditState
                StateProbe.record $"activate-audit:{key}" (string audit.State.writes)

                // …and its replacement is published in memory only.
                return
                    { state with
                        entries = state.entries @ [ "activated" ] }
            })

        onDeactivate (fun context reason state ->
            task {
                let key = string context.grainId
                StateProbe.record $"deactivate:{key}" $"{reason.ReasonCode}|{describe state}"
                StateProbe.record $"deactivate-tick:{key}" (string (StateProbe.tick ()))

                match StateProbe.failingDeactivations.TryGetValue key with
                | true, true -> raise (ApplicationException $"deactivation hook of {key} failed on purpose")
                | _ -> ()
            })

        handle (_.append) (fun _ state (entry: string) ->
            task {
                let next =
                    { entries = state.entries @ [ entry ]
                      version = state.version + 1 }

                return next, next.version
            })

        handle (_.snapshot) (fun _ state () -> task { return state, describe state })

        handle (_.writeNow) (fun context state (entry: string) ->
            task {
                let next =
                    { entries = state.entries @ [ entry ]
                      version = state.version + 1 }

                let facade = context.persistentState ledgerState
                facade.State <- next
                do! facade.WriteStateAsync()
                return next, ()
            })

        handle (_.writeThenReturn) (fun context state (argument: string) ->
            task {
                // "<written>|<returned>"
                let parts = argument.Split '|'

                let written =
                    { entries = state.entries @ [ parts.[0] ]
                      version = state.version + 1 }

                let facade = context.persistentState ledgerState
                facade.State <- written
                do! facade.WriteStateAsync()

                let returned =
                    { entries = state.entries @ [ parts.[1] ]
                      version = state.version + 2 }

                return returned, describe written
            })

        handle (_.writeThenFail) (fun context state (entry: string) ->
            task {
                let written =
                    { entries = state.entries @ [ entry ]
                      version = state.version + 1 }

                let facade = context.persistentState ledgerState
                facade.State <- written
                do! facade.WriteStateAsync()
                raise (ApplicationException $"handler failed after writing {entry}")
                return state, ""
            })

        handle (_.reload) (fun context state () ->
            task {
                let facade = context.persistentState ledgerState
                do! facade.ReadStateAsync()

                // The bound argument keeps its turn-entry value while the authoritative holder
                // has already been replaced by the reload.
                let report = $"bound={describe state}|holder={describe facade.State}"

                // Returning the turn-entry value publishes it again, over the reloaded one.
                return state, report
            })

        handle (_.clearNow) (fun context state () ->
            task {
                let facade = context.persistentState ledgerState
                do! facade.ClearStateAsync()
                return state, ()
            })

        handle (_.auditWrite) (fun context state (count: int) ->
            task {
                let audit = context.persistentState auditState
                audit.State <- { writes = count }
                do! audit.WriteStateAsync()
                return state, ()
            })

        handle (_.auditPeek) (fun context state () ->
            task {
                let audit = context.persistentState auditState
                return state, audit.State.writes
            })

        handle (_.orderedWrites) (fun context state (entry: string) ->
            task {
                let ledger = context.persistentState ledgerState

                ledger.State <-
                    { entries = state.entries @ [ entry ]
                      version = state.version + 1 }

                do! ledger.WriteStateAsync()

                // No cross-provider transaction is claimed: if this one fails, the first write
                // stays committed and the failure reaches the caller.
                let audit = context.persistentState auditState
                audit.State <- { writes = audit.State.writes + 1 }
                do! audit.WriteStateAsync()

                return state, "both"
            })

        handle (_.readOnlyProbe) (fun context state () ->
            task {
                let facade = context.persistentState ledgerState
                // Getters stay available in a read-only callback.
                let getters =
                    $"state={describe facade.State}|recordExists={facade.RecordExists}|etag={not (isNull facade.Etag)}"

                let! mutations = probeMutations facade
                return { state with version = 999 }, $"{getters}|{mutations}"
            })

        handle (_.interleavedProbe) (fun context state () ->
            task {
                let facade = context.persistentState ledgerState
                let! mutations = probeMutations facade
                return { state with version = 999 }, mutations
            })

        handle (_.escapeFacade) (fun context state () ->
            task {
                StateProbe.escaped.[string context.grainId] <- context.persistentState ledgerState
                return state, ()
            })

        handle (_.useEscapedFacade) (fun context state () ->
            task {
                match StateProbe.escaped.TryGetValue(string context.grainId) with
                | true, facade ->
                    let getter =
                        try
                            $"state={describe facade.State}"
                        with error ->
                            $"state={error.GetType().Name}"

                    let! mutations = probeMutations facade
                    return state, $"{getter}|{mutations}"
                | _ -> return state, "no escaped facade"
            })

        handle (_.unattached) (fun context state () ->
            task {
                try
                    let facade = context.persistentState detachedState
                    return state, $"resolved:{facade.RecordExists}"
                with error ->
                    return state, $"{error.GetType().Name}:{error.Message}"
            })

        handle (_.createFacetNow) (fun context state () ->
            task {
                // The decisive negative control for activation-order step 1: Orleans rejects
                // IPersistentStateFactory.Create once the activation lifecycle has started, so
                // the functional activator has to create every facet before it returns.
                let factory = context.services.GetRequiredService<IPersistentStateFactory>()

                match context.services.GetService typeof<IGrainContext> with
                | :? IGrainContext as grainContext ->
                    try
                        factory.Create<AuditState>(
                            grainContext,
                            FunctionalPersistentStateConfiguration("too-late", FunctionalStateProviders.Audit)
                        )
                        |> ignore

                        return state, "created"
                    with error ->
                        return state, $"rejected:{error.GetType().Name}:{error.Message}"
                | _ -> return state, "no grain context in the activation services"
            })

        handle (_.whereAmI) (fun context state () -> task { return state, siloOf context })

        handle (_.armFailingDeactivation) (fun context state () ->
            task {
                StateProbe.failingDeactivations.[string context.grainId] <- true
                return state, ()
            })

        handle (_.armFailingActivation) (fun context state () ->
            task {
                StateProbe.failingActivations.[string context.grainId] <- true
                return state, ()
            })

        handle (_.goAway) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return state, ()
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// An ephemeral twin: the same API with no persistent attachment at all
// ──────────────────────────────────────────────────────────────────────────────

type EphemeralActor = private EphemeralActor of unit

let ephemeralContract =
    grainContract<EphemeralActor, string, LedgerApi> () {
        grainType FunctionalStateGrainTypes.Ephemeral
        version 1
        stringKey

        readOnly (_.snapshot)
        readOnly (_.auditPeek)
        readOnly (_.readOnlyProbe)
        readOnly (_.unattached)
        readOnly (_.whereAmI)

        readOnly (_.interleavedProbe)
        alwaysInterleave (_.interleavedProbe)
    }

let private notAttached<'T> () : 'T =
    failwith "the ephemeral definition attaches no persistent state"

let ephemeralDefinition =
    grainFor ephemeralContract {
        initialState (fun (key: string) -> { entries = [ key ]; version = 0 })

        handle (_.append) (fun _ state (entry: string) ->
            task {
                let next =
                    { entries = state.entries @ [ entry ]
                      version = state.version + 1 }

                return next, next.version
            })

        handle (_.snapshot) (fun _ state () -> task { return state, describe state })
        handle (_.writeNow) (fun _ state (_: string) -> task { return state, notAttached () })
        handle (_.writeThenReturn) (fun _ state (_: string) -> task { return state, notAttached () })
        handle (_.writeThenFail) (fun _ state (_: string) -> task { return state, notAttached () })
        handle (_.reload) (fun _ state () -> task { return state, notAttached () })
        handle (_.clearNow) (fun _ state () -> task { return state, notAttached () })
        handle (_.auditWrite) (fun _ state (_: int) -> task { return state, notAttached () })
        handle (_.auditPeek) (fun _ state () -> task { return state, notAttached () })
        handle (_.orderedWrites) (fun _ state (_: string) -> task { return state, notAttached () })
        handle (_.readOnlyProbe) (fun _ state () -> task { return state, notAttached () })
        handle (_.interleavedProbe) (fun _ state () -> task { return state, notAttached () })
        handle (_.escapeFacade) (fun _ state () -> task { return state, notAttached () })
        handle (_.useEscapedFacade) (fun _ state () -> task { return state, notAttached () })

        handle (_.unattached) (fun context state () ->
            task {
                try
                    let facade = context.persistentState ledgerState
                    return state, $"resolved:{facade.RecordExists}"
                with error ->
                    return state, $"{error.GetType().Name}:{error.Message}"
            })

        handle (_.createFacetNow) (fun _ state () -> task { return state, notAttached () })
        handle (_.whereAmI) (fun context state () -> task { return state, siloOf context })
        handle (_.armFailingDeactivation) (fun _ state () -> task { return state, () })
        handle (_.armFailingActivation) (fun _ state () -> task { return state, () })

        handle (_.goAway) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return state, ()
            })
    }

let ledgerRef = FunctionalGrain.ref ledgerContract
let ephemeralRef = FunctionalGrain.ref ephemeralContract

// ──────────────────────────────────────────────────────────────────────────────
// A stop-stage witness
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A grain-lifecycle observer subscribed at <c>GrainLifecycleStage.SetupState</c>. Stop stages
/// run in reverse order, so its stop callback runs AFTER the stage which invokes
/// <c>IGrainBase.OnDeactivateAsync</c> — which is where the functional <c>onDeactivate</c> hook
/// lives. Comparing the two recorded ticks therefore proves both that the functional hook runs
/// before the remaining stop stages and that those stages still run when the hook fails.
/// </summary>
[<Sealed>]
type StopStageWitness() =

    interface IConfigureGrainContextProvider with
        member this.TryGetConfigurator
            (_grainType: GrainType, _properties: Orleans.Metadata.GrainProperties, configurator: byref<IConfigureGrainContext>)
            =
            configurator <- this
            true

    interface IConfigureGrainContext with
        member _.Configure(context: IGrainContext) =
            context.ObservableLifecycle.Subscribe(
                "Orleans.FSharp.Integration.StopStageWitness",
                GrainLifecycleStage.SetupState,
                Func<CancellationToken, Task>(fun _ -> Task.CompletedTask),
                Func<CancellationToken, Task>(fun _ ->
                    StateProbe.record $"stop-tick:{context.GrainId}" (string (StateProbe.tick ()))
                    Task.CompletedTask)
            )
            |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// Silo log capture
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Warnings and errors the functional runtime logs. A failing deactivation hook and a skipped
/// one both land here, which is how the tests tell the two apart.
/// </summary>
[<RequireQualifiedAccess>]
module StateLogCapture =
    let entries = ConcurrentQueue<string>()

    let clear () = entries.Clear()

    let contains (fragment: string) =
        entries |> Seq.exists (fun entry -> entry.Contains(fragment, StringComparison.Ordinal))

[<Sealed>]
type private StateCaptureLogger(category: string) =
    interface ILogger with
        member _.BeginScope<'TState>(_state: 'TState) =
            { new IDisposable with
                member _.Dispose() = () }

        member _.IsEnabled(level: LogLevel) = level >= LogLevel.Warning

        member _.Log<'TState>(level, _eventId, state: 'TState, error: exn, formatter: Func<'TState, exn, string>) =
            if level >= LogLevel.Warning then
                let rendered = formatter.Invoke(state, error)

                StateLogCapture.entries.Enqueue(
                    match error with
                    | null -> $"{category}|{rendered}"
                    | value -> $"{category}|{rendered}|{value}"
                )

[<Sealed>]
type FunctionalStateLogProvider() =
    interface ILoggerProvider with
        member _.CreateLogger(category: string) = StateCaptureLogger category :> ILogger
        member _.Dispose() = ()

// ──────────────────────────────────────────────────────────────────────────────
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

type FunctionalStateSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            // Real memory storage under a private name, wrapped by the instrumenting decorator
            // under the name the definitions actually reference.
            siloBuilder.AddMemoryGrainStorage $"{FunctionalStateProviders.Ledger}.inner" |> ignore
            siloBuilder.AddMemoryGrainStorage $"{FunctionalStateProviders.Audit}.inner" |> ignore

            let instrument (name: string) =
                siloBuilder.Services.AddKeyedSingleton<IGrainStorage>(
                    name,
                    Func<IServiceProvider, obj, IGrainStorage>(fun services _ ->
                        InstrumentingGrainStorage(
                            services.GetRequiredKeyedService<IGrainStorage>($"{name}.inner"),
                            name
                        )
                        :> IGrainStorage)
                )
                |> ignore

            instrument FunctionalStateProviders.Ledger
            instrument FunctionalStateProviders.Audit

            siloBuilder.AddFunctionalGrain ledgerDefinition |> ignore
            siloBuilder.AddFunctionalGrain ephemeralDefinition |> ignore

            siloBuilder.Services.AddSingleton<ILoggerProvider, FunctionalStateLogProvider>()
            |> ignore

            // Per-activation lifecycle observer used to order the functional deactivation hook
            // against the remaining Orleans stop stages.
            siloBuilder.Services.AddSingleton<IConfigureGrainContextProvider, StopStageWitness>()
            |> ignore

type FunctionalStateClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

[<Sealed>]
type FunctionalStateClusterFixture() =
    let cluster =
        let builder = TestClusterBuilder 2s
        builder.AddSiloBuilderConfigurator<FunctionalStateSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<FunctionalStateClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    let waitForManifestPropagation () =
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        let propagated () =
            cluster.Silos
            |> Seq.forall (fun handle ->
                let services = (handle :?> InProcessSiloHandle).SiloHost.Services
                let current = services.GetRequiredService<IClusterManifestProvider>().Current

                current.Silos.Count = cluster.Silos.Count
                && current.Silos
                   |> Seq.forall (fun pair ->
                       pair.Value.Grains.ContainsKey(GrainType.Create FunctionalStateGrainTypes.Ledger)))

        while not (propagated ()) && DateTime.UtcNow < deadline do
            Thread.Sleep 200

        if not (propagated ()) then
            failwith "cluster manifests did not propagate to every silo"

    do waitForManifestPropagation ()

    member _.Cluster = cluster
    member _.Client = cluster.Client

    /// <summary>The bound ledger API of one key, from the external client.</summary>
    member _.Ledger(key: string) = ledgerRef cluster.Client key

    member _.Ephemeral(key: string) = ephemeralRef cluster.Client key

    /// <summary>The Orleans grain identity string of one ledger key.</summary>
    member _.LedgerId(key: string) = $"{FunctionalStateGrainTypes.Ledger}/{key}"

    /// <summary>
    /// Deactivate the activation of <paramref name="key"/> and wait until a new activation has
    /// run its <c>onActivate</c> hook, so the next call observes freshly loaded state.
    /// </summary>
    member this.Recycle(key: string) =
        task {
            let grainId = this.LedgerId key
            let before = StateProbe.activationCount grainId
            let api = this.Ledger key
            do! api.goAway ()

            let deadline = DateTime.UtcNow.AddSeconds 30.0
            let mutable activated = false

            while not activated && DateTime.UtcNow < deadline do
                do! Task.Delay 100
                let! _ = api.snapshot ()
                activated <- StateProbe.activationCount grainId > before

            if not activated then
                failwith $"grain {grainId} did not reactivate"
        }

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("FunctionalStateCluster")>]
type FunctionalStateClusterCollection() =
    interface ICollectionFixture<FunctionalStateClusterFixture>
