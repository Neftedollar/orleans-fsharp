/// <summary>
/// The spec 004 Phase E fixture: a two-silo cluster hosting the same journaled definition twice,
/// once on Orleans' LogStorage log-consistency provider and once on its StateStorage one, so every
/// behavioural test runs against both providers from one body.
/// </summary>
/// <remarks>
/// <para>
/// The journal storage is the process-wide <c>RetainedGrainStorage</c> the Phase-4 restart tests
/// introduced rather than stock memory storage: memory storage keeps its records in ordinary
/// grains, which die with the silo that hosts them, so a "the journal follows the grain to another
/// silo" test on top of it would be proving nothing.
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
open Orleans.FSharp.Integration.FunctionalStateRestartTests
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

    let reset (label: string) = folds.TryRemove label |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// The domain
// ──────────────────────────────────────────────────────────────────────────────

/// The state: the fold of the journal, never written anywhere by the application.
type AccountState =
    { balance: decimal
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
      /// The address of the silo this activation lives on.
      whereAmI: unit -> Task<string>
      /// Deactivates the activation, so the next call replays from the journal.
      recycle: unit -> Task<unit> }

/// <summary>
/// One journaled definition, over any actor brand and any provider. It exists once and is
/// instantiated twice so the two providers cannot drift apart in the test bodies.
/// </summary>
let accountDefinitionFor (contract: GrainContract<'Actor, string, AccountApi>) (providerName: string) =
    journaledGrainFor contract {
        initialEventState (fun key -> { balance = 0m; history = [ $"opened:{key}" ] })

        apply (fun state event ->
            // Test instrumentation, and the one impure thing in this file's fold: it is how the
            // tests observe how much of a journal each provider actually replays. `apply` receives
            // no key by design, so the counter is keyed by provider — safe because xUnit does not
            // run the tests of one collection in parallel.
            PhaseECounters.fold providerName

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

        handle (_.whereAmI) (fun context state () ->
            task {
                let details = context.services.GetRequiredService<ILocalSiloDetails>()
                return [], details.SiloAddress.ToString()
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
    grainContract<LogAccountActor, string, AccountApi> () {
        grainType PhaseEGrainTypes.LogAccount
        version 1
        stringKey
        readOnly (_.balance)
        readOnly (_.history)
        readOnly (_.whereAmI)
        readOnly (_.readOnlyRaise)
    }

let stateAccountContract =
    grainContract<StateAccountActor, string, AccountApi> () {
        grainType PhaseEGrainTypes.StateAccount
        version 1
        stringKey
        readOnly (_.balance)
        readOnly (_.history)
        readOnly (_.whereAmI)
        readOnly (_.readOnlyRaise)
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
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

type PhaseESiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.Services.AddKeyedSingleton<IGrainStorage>(
                PhaseEProviders.JournalStore,
                Func<IServiceProvider, obj, IGrainStorage>(fun _ _ ->
                    RetainedGrainStorage PhaseEProviders.JournalStore :> IGrainStorage)
            )
            |> ignore

            siloBuilder.AddLogStorageBasedLogConsistencyProvider PhaseEProviders.LogStorage
            |> ignore

            siloBuilder.AddStateStorageBasedLogConsistencyProvider PhaseEProviders.StateStorage
            |> ignore

            siloBuilder.AddFunctionalJournaledGrain logAccountDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain stateAccountDefinition |> ignore

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
