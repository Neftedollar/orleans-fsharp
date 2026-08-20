/// <summary>
/// The spec 004 Phase E fixture: a two-silo cluster hosting the same journaled definition twice,
/// once on Orleans' LogStorage log-consistency provider and once on its StateStorage one, so every
/// behavioural test runs against both providers from one body.
/// </summary>
/// <remarks>
/// <para>
/// The journal storage is a process-wide table rather than stock memory storage: memory storage
/// keeps its records in ordinary grains, which die with the silo that hosts them, so a "the journal
/// follows the grain to another silo" test on top of it would be proving nothing. The same store
/// injects write faults on demand, which is the only way to observe what Orleans' adaptor does with
/// a storage failure.
/// </para>
/// <para>
/// <c>PhaseECounters</c> counts fold invocations from inside <c>apply</c>. That is deliberately
/// impure — in a test probe, not in application code — and it is the only way to observe how much
/// of a journal each provider actually replays: LogStorage stores the whole log and folds all of
/// it on every activation, StateStorage stores the folded view and replays nothing.
/// </para>
/// </remarks>
module Orleans.FSharp.Integration.FunctionalPhaseEFixture

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Storage
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Names
// ──────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module PhaseEGrainTypes =
    /// The journaled account whose journal lives in Orleans' LogStorage provider.
    [<Literal>]
    let LogAccount = "phasee.log.account"

    /// The same definition over Orleans' StateStorage provider.
    [<Literal>]
    let StateAccount = "phasee.state.account"

    /// A journaled definition on a native int64 key.
    [<Literal>]
    let Counter = "phasee.counter"

[<RequireQualifiedAccess>]
module PhaseEProviders =
    [<Literal>]
    let LogStorage = "PhaseELogStorage"

    [<Literal>]
    let StateStorage = "PhaseEStateStorage"

    [<Literal>]
    let JournalStore = "PhaseEJournalStore"

// ──────────────────────────────────────────────────────────────────────────────
// Out-of-band observation
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>How many times the replay fold has run, per grain key.</summary>
[<RequireQualifiedAccess>]
module PhaseECounters =
    let private folds = ConcurrentDictionary<string, StrongBox<int>>()

    let private cell (label: string) =
        folds.GetOrAdd(label, fun _ -> StrongBox<int> 0)

    /// <summary>Record one invocation of a definition's replay fold.</summary>
    let fold (label: string) =
        let box = cell label
        Interlocked.Increment(&box.Value) |> ignore

    let count (label: string) =
        let box = cell label
        Volatile.Read(&box.Value)


/// <summary>
/// Write faults the journal store injects, per grain identity.
/// </summary>
/// <remarks>
/// Failing the FIRST N writes and then succeeding is the shape that matters: Orleans' adaptor
/// treats a storage failure as something to retry rather than something to report, so the only way
/// to observe that behaviour is to let it eventually win.
/// </remarks>
[<RequireQualifiedAccess>]
module PhaseEFaults =
    let private remaining = ConcurrentDictionary<string, StrongBox<int>>()
    let private attempts = ConcurrentDictionary<string, StrongBox<int>>()

    let private cell (table: ConcurrentDictionary<string, StrongBox<int>>) (key: string) =
        table.GetOrAdd(key, fun _ -> StrongBox<int> 0)

    /// <summary>Make the next <paramref name="count"/> writes of this grain's journal fail.</summary>
    let arm (grainId: string) (count: int) =
        Volatile.Write(&(cell attempts grainId).Value, 0)
        Volatile.Write(&(cell remaining grainId).Value, count)

    /// <summary>How many write attempts this grain's journal has made since it was armed.</summary>
    let writeAttempts (grainId: string) = Volatile.Read(&(cell attempts grainId).Value)

    /// <summary>Whether this write must fail. Counts the attempt either way.</summary>
    let shouldFail (grainId: string) =
        Interlocked.Increment(&(cell attempts grainId).Value) |> ignore
        let box = cell remaining grainId

        if Volatile.Read(&box.Value) > 0 then
            Interlocked.Decrement(&box.Value) |> ignore
            true
        else
            false

/// <summary>
/// The journal store: a process-wide table, so a journal outlives the silo that wrote it, plus the
/// write-fault injection above.
/// </summary>
/// <remarks>
/// A failing write throws BEFORE storing anything, so the adaptor's "did my apparently-failed write
/// actually succeed" bit check correctly concludes it did not and re-submits the same batch.
/// </remarks>
[<Sealed>]
type PhaseEJournalStorage() =
    static let records = ConcurrentDictionary<string, obj * string>()

    static let recordKey (stateName: string) (grainId: GrainId) = $"{stateName}/{grainId}"

    interface IGrainStorage with
        member _.ReadStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            match records.TryGetValue(recordKey stateName grainId) with
            | true, (state, etag) ->
                grainState.State <- unbox<'T> state
                grainState.ETag <- etag
                grainState.RecordExists <- true
            | _ ->
                // Orleans' own providers REPLACE the state with a fresh instance when no record
                // exists (MemoryStorage: `grainState.State = CreateInstance<T>()`), and conforming
                // to that is load-bearing here rather than cosmetic. The log-view adaptor decides
                // whether an apparently-failed write actually landed by re-reading and comparing a
                // write bit it had already flipped in the in-memory instance — so a provider that
                // leaves the caller's instance alone hands back the flipped bit and the adaptor
                // concludes the failed write succeeded. That silently turned the first version of
                // the fault-injection test into a no-op.
                if not (isNull (typeof<'T>.GetConstructor Type.EmptyTypes)) then
                    grainState.State <- Activator.CreateInstance<'T>()

                grainState.ETag <- null
                grainState.RecordExists <- false

            Task.CompletedTask

        member _.WriteStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            if PhaseEFaults.shouldFail (string grainId) then
                raise (
                    InvalidOperationException
                        $"PhaseE fault injection: the journal store refused a write for '{grainId}'"
                )

            let etag = Guid.NewGuid().ToString "N"
            records.[recordKey stateName grainId] <- (box grainState.State, etag)
            grainState.ETag <- etag
            grainState.RecordExists <- true
            Task.CompletedTask

        member _.ClearStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            records.TryRemove(recordKey stateName grainId) |> ignore
            grainState.ETag <- null
            grainState.RecordExists <- false
            Task.CompletedTask

/// <summary>
/// One escaped invocation context per grain key. A context is an ordinary F# value, so a handler
/// can stash it and a later turn can use it — which is exactly the misuse the journal facade's
/// scope binding exists to refuse.
/// </summary>
[<RequireQualifiedAccess>]
module PhaseEEscapes =
    let private contexts = ConcurrentDictionary<string, obj>()

    let keep (key: string) (context: obj) = contexts.[key] <- context

    let tryTake (key: string) =
        match contexts.TryGetValue key with
        | true, context -> Some context
        | _ -> None

// ──────────────────────────────────────────────────────────────────────────────
// The domain
// ──────────────────────────────────────────────────────────────────────────────

/// The state: the fold of the journal, never written anywhere by the application.
type AccountState =
    { key: string
      balance: decimal
      history: string list }

/// The events. A handler raises these; only <c>apply</c> turns them into state.
type AccountEvent =
    | Deposited of amount: decimal
    | Withdrawn of amount: decimal
    | Noted of note: string
    /// Folds into an exception, to pin what a failing <c>apply</c> does.
    | Poisoned

[<NoEquality; NoComparison>]
type AccountApi =
    { /// Raises Deposited and replies with the balance the handler computed itself.
      deposit: decimal -> Task<decimal>
      /// Raises Withdrawn only when the funds are there; replies whether it did.
      withdraw: decimal -> Task<bool>
      /// A query: no events at all.
      balance: unit -> Task<decimal>
      /// A query over the folded history.
      history: unit -> Task<string list>
      /// The journal version the handler was handed.
      version: unit -> Task<int>
      /// The version AFTER a deposit in the same turn, to pin when confirmation happens.
      depositAndReadVersion: decimal -> Task<int * int>
      /// Raises several events in one atomic batch.
      batch: decimal list -> Task<int>
      /// A conditional append; replies whether it was accepted.
      conditional: decimal -> Task<bool>
      /// Raises the poison event, whose fold throws.
      poison: unit -> Task<unit>
      /// Declared readOnly and yet raises an event: the negative control.
      readOnlyRaise: unit -> Task<unit>
      /// Declared readOnly and yet appends conditionally: the second negative control.
      readOnlyConditional: unit -> Task<string>
      /// Stashes this invocation's context for a later turn to misuse.
      escape: unit -> Task<unit>
      /// Appends through the context an earlier turn stashed; reports what happened.
      useEscaped: unit -> Task<string>
      /// The address of the silo this activation lives on.
      whereAmI: unit -> Task<string>
      /// Raises an event from a ONE-WAY operation: the caller learns nothing about the outcome.
      noteOneWay: string -> Task<unit>
      /// Throws after deciding on an event: nothing may be appended.
      throwAfterDeciding: unit -> Task<unit>
      /// Deactivates the activation, so the next call replays from the journal.
      recycle: unit -> Task<unit> }

/// <summary>
/// One journaled definition, over any actor brand and any provider. It exists once and is
/// instantiated twice so the two providers cannot drift apart in the test bodies.
/// </summary>
let accountDefinitionFor (contract: GrainContract<'Actor, string, AccountApi>) (providerName: string) =
    journaledGrainFor contract {
        initialEventState (fun key ->
            { key = key
              balance = 0m
              history = [ $"opened:{key}" ] })

        apply (fun state event ->
            // Test instrumentation, and the one impure thing in this file's fold: it is how the
            // tests observe how much of a journal each provider actually replays. The label
            // carries the GRAIN KEY (seeded into the state by initialEventState) as well as the
            // provider, because the counter is a process-wide static and xUnit runs OTHER test
            // collections hosting these same definitions in parallel — a provider-only label
            // absorbs their folds and flakes (observed: StateStorage read 4 where its own grain
            // folded 0). Per-key labels make each test's counters its own.
            PhaseECounters.fold $"{providerName}|{state.key}"

            match event with
            | Deposited amount ->
                { state with
                    balance = state.balance + amount
                    history = state.history @ [ $"+{amount}" ] }
            | Withdrawn amount ->
                { state with
                    balance = state.balance - amount
                    history = state.history @ [ $"-{amount}" ] }
            | Noted note ->
                { state with
                    history = state.history @ [ note ] }
            | Poisoned -> failwith "the poison event cannot be folded")

        logProvider providerName
        journalStorage PhaseEProviders.JournalStore

        handle (_.deposit) (fun _ state (amount: decimal) ->
            task { return [ Deposited amount ], state.balance + amount })

        handle (_.withdraw) (fun _ state (amount: decimal) ->
            task {
                if state.balance < amount then
                    return [], false
                else
                    return [ Withdrawn amount ], true
            })

        handle (_.balance) (fun _ state () -> task { return [], state.balance })

        handle (_.history) (fun _ state () -> task { return [], state.history })

        handle (_.version) (fun context state () -> task { return [], context.journalVersion })

        handle (_.depositAndReadVersion) (fun context state (amount: decimal) ->
            task {
                // The version the handler observes is the one it started from: the runtime
                // confirms the returned events only after the handler has returned.
                let before = context.journalVersion
                return [ Deposited amount ], (before, context.journalVersion)
            })

        handle (_.batch) (fun _ state (amounts: decimal list) ->
            task { return (amounts |> List.map Deposited), List.length amounts })

        handle (_.conditional) (fun context state (amount: decimal) ->
            task {
                let! accepted = context.raiseConditional [ Deposited amount ]
                return [], accepted
            })

        handle (_.poison) (fun _ state () -> task { return [ Poisoned ], () })

        handle (_.readOnlyRaise) (fun _ state () -> task { return [ Noted "should never be appended" ], () })

        handle (_.readOnlyConditional) (fun context state () ->
            task {
                let! outcome =
                    task {
                        try
                            let! accepted = context.raiseConditional [ Noted "should never be appended" ]
                            return $"the append was ACCEPTED ({accepted})"
                        with error ->
                            return error.Message
                    }

                return [], outcome
            })

        handle (_.escape) (fun context state () ->
            task {
                PhaseEEscapes.keep context.key (box context)
                return [], ()
            })

        handle (_.useEscaped) (fun context state () ->
            task {
                let! outcome =
                    task {
                        match PhaseEEscapes.tryTake context.key with
                        | None -> return "nothing was stashed"
                        | Some stashed ->
                            let escaped = stashed :?> FunctionalGrainContext<'Actor, string>

                            try
                                let! accepted = escaped.raiseConditional [ Noted "escaped" ]
                                return $"the append was ACCEPTED ({accepted})"
                            with error ->
                                return error.Message
                    }

                return [], outcome
            })

        handle (_.whereAmI) (fun context state () ->
            task {
                let details = context.services.GetRequiredService<ILocalSiloDetails>()
                return [], details.SiloAddress.ToString()
            })

        handle (_.noteOneWay) (fun _ state (note: string) -> task { return [ Noted note ], () })

        handle (_.throwAfterDeciding) (fun _ state () ->
            task {
                let _decided = [ Deposited 999m ]
                return failwith "the handler failed after deciding on its events"
            })

        handle (_.recycle) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return [], ()
            })
    }

type LogAccountActor = private LogAccountActor of unit
type StateAccountActor = private StateAccountActor of unit

let logAccountContract =
    grainContract<LogAccountActor, string, AccountApi> {
        grainType PhaseEGrainTypes.LogAccount
        version 1
        stringKey
        readOnly (_.balance)
        readOnly (_.history)
        readOnly (_.whereAmI)
        readOnly (_.readOnlyRaise)
        readOnly (_.readOnlyConditional)
        oneWay (_.noteOneWay)
    }

let stateAccountContract =
    grainContract<StateAccountActor, string, AccountApi> {
        grainType PhaseEGrainTypes.StateAccount
        version 1
        stringKey
        readOnly (_.balance)
        readOnly (_.history)
        readOnly (_.whereAmI)
        readOnly (_.readOnlyRaise)
        readOnly (_.readOnlyConditional)
        oneWay (_.noteOneWay)
    }

let logAccountDefinition =
    accountDefinitionFor logAccountContract PhaseEProviders.LogStorage

let stateAccountDefinition =
    accountDefinitionFor stateAccountContract PhaseEProviders.StateStorage

/// <summary>
/// A C#-shaped facade over the journaled contract. It names no journal concept at all, which is
/// the claim under test: the definition kind is invisible across the interop boundary.
/// </summary>
type IAccountFacade =
    abstract Deposit: amount: decimal -> Task<decimal>
    abstract Balance: unit -> Task<decimal>
    abstract Version: unit -> Task<int>

let logAccountRef = FunctionalGrain.ref logAccountContract
let stateAccountRef = FunctionalGrain.ref stateAccountContract

// ──────────────────────────────────────────────────────────────────────────────
// A journaled definition on a NON-string key
// ──────────────────────────────────────────────────────────────────────────────

type CounterActor = private CounterActor of unit

/// <summary>The seed records the key it was derived from, so a wrong decode is visible in state.</summary>
type CounterState = { total: int64; seededFrom: int64 }

type CounterEvent = Added of int64

[<NoEquality; NoComparison>]
type CounterApi =
    { add: int64 -> Task<int64>
      total: unit -> Task<int64>
      /// The key `initialEventState` was handed, folded into the state at seed time.
      seededFrom: unit -> Task<int64>
      /// The decoded domain key this activation sees, and its raw Orleans grain identity.
      identity: unit -> Task<int64 * string>
      recycle: unit -> Task<unit> }

let counterContract =
    grainContract<CounterActor, int64, CounterApi> {
        grainType PhaseEGrainTypes.Counter
        version 1
        int64Key
        readOnly (_.total)
        readOnly (_.seededFrom)
        readOnly (_.identity)
    }

let counterDefinition =
    journaledGrainFor counterContract {
        initialEventState (fun (key: int64) -> { total = 0L; seededFrom = key })
        apply (fun state (Added amount) -> { state with total = state.total + amount })

        logProvider PhaseEProviders.LogStorage
        journalStorage PhaseEProviders.JournalStore

        handle (_.add) (fun _ state (amount: int64) -> task { return [ Added amount ], state.total + amount })

        handle (_.total) (fun _ state () -> task { return ([]: CounterEvent list), state.total })

        handle (_.seededFrom) (fun _ state () -> task { return ([]: CounterEvent list), state.seededFrom })

        handle (_.identity) (fun context state () ->
            task { return ([]: CounterEvent list), (context.key, string context.grainId) })

        handle (_.recycle) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return [], ()
            })
    }

let counterRef = FunctionalGrain.ref counterContract

// ──────────────────────────────────────────────────────────────────────────────
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

type PhaseESiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.Services.AddKeyedSingleton<IGrainStorage>(
                PhaseEProviders.JournalStore,
                Func<IServiceProvider, obj, IGrainStorage>(fun _ _ -> PhaseEJournalStorage() :> IGrainStorage)
            )
            |> ignore

            siloBuilder.AddLogStorageBasedLogConsistencyProvider PhaseEProviders.LogStorage
            |> ignore

            siloBuilder.AddStateStorageBasedLogConsistencyProvider PhaseEProviders.StateStorage
            |> ignore

            siloBuilder.AddFunctionalJournaledGrain logAccountDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain stateAccountDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain counterDefinition |> ignore

type PhaseEClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

/// <summary>
/// Two silos, so "the journal follows the grain" is a statement about a silo that never saw the
/// write rather than about one activation of one process.
/// </summary>
[<Sealed>]
type FunctionalPhaseEFixture() =
    let cluster =
        let builder = TestClusterBuilder 2s
        builder.AddSiloBuilderConfigurator<PhaseESiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<PhaseEClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Cluster = cluster
    member _.Client = cluster.Client

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("FunctionalPhaseE")>]
type FunctionalPhaseECollection() =
    interface ICollectionFixture<FunctionalPhaseEFixture>
