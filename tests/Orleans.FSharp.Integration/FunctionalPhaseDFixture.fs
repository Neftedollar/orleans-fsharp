/// <summary>
/// The spec 004 Phase D fixture: a two-silo cluster with Orleans transactions enabled, hosting
/// functional definitions that attach <c>transactionalStateFrom</c> facets and declare
/// <c>transactional</c> operations, plus a state-free orchestrator that drives two participants
/// inside one transaction.
/// </summary>
/// <remarks>
/// The silos use stock <c>AddMemoryGrainStorage</c> under the transactional storage names.
/// <c>NamedTransactionalStateStorageFactory.Create</c> looks for a keyed
/// <c>ITransactionalStateStorageFactory</c> first and falls back to a keyed
/// <c>IGrainStorage</c> wrapped in <c>TransactionalStateStorageProviderWrapper</c>, so a memory
/// storage provider is a real transactional store, ETags and all — not a shortcut around the
/// commit protocol.
/// </remarks>
module Orleans.FSharp.Integration.FunctionalPhaseDFixture

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Grain types and storage names
// ──────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module PhaseDGrainTypes =
    /// A transactional account: one transactional facet, three transactional operations.
    [<Literal>]
    let Account = "phased.account"

    /// A state-free orchestrator: transactional operations, no transactional facet at all.
    [<Literal>]
    let Atm = "phased.atm"

    /// A definition that mixes a transactional facet with an ordinary persistent facet.
    [<Literal>]
    let Mixed = "phased.mixed"

[<RequireQualifiedAccess>]
module PhaseDStorage =
    [<Literal>]
    let Transactional = "PhaseDTransactionStore"

    [<Literal>]
    let Persistent = "PhaseDPersistentStore"

// ──────────────────────────────────────────────────────────────────────────────
// Out-of-band observation
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// How many times each handler body has been entered, keyed by an arbitrary label. Silos of a
/// <c>TestCluster</c> share one process, so a test reads these counters directly instead of
/// calling the grain — which matters for the re-execution question: a call that aborted cannot
/// report anything back.
/// </summary>
[<RequireQualifiedAccess>]
module PhaseDCounters =
    let private counters = ConcurrentDictionary<string, StrongBox<int>>()

    let private cell (label: string) =
        counters.GetOrAdd(label, fun _ -> StrongBox<int> 0)

    /// <summary>Record one entry into a handler body.</summary>
    let enter (label: string) =
        let box = cell label
        Interlocked.Increment(&box.Value) |> ignore

    /// <summary>How many times that body has been entered.</summary>
    let count (label: string) =
        let box = cell label
        Volatile.Read(&box.Value)

    /// <summary>Forget one label, so a test starts from zero.</summary>
    let reset (label: string) = counters.TryRemove label |> ignore

/// <summary>
/// A one-shot gate a handler can park on, used to force two transactions to overlap on the same
/// transactional state.
/// </summary>
[<RequireQualifiedAccess>]
module PhaseDGates =
    let private gates =
        ConcurrentDictionary<string, TaskCompletionSource<bool>>()

    let private cell (label: string) =
        gates.GetOrAdd(
            label,
            fun _ -> TaskCompletionSource<bool> TaskCreationOptions.RunContinuationsAsynchronously
        )

    /// <summary>Wait on the gate, or give up after the timeout.</summary>
    let wait (label: string) (timeoutMilliseconds: int) =
        task {
            let gate = cell label
            let! finished = Task.WhenAny(gate.Task, Task.Delay timeoutMilliseconds)
            return obj.ReferenceEquals(finished, gate.Task)
        }

    /// <summary>Release everything parked on the gate.</summary>
    let release (label: string) = (cell label).TrySetResult true |> ignore

    /// <summary>Forget one gate, so a test starts from a fresh one.</summary>
    let reset (label: string) = gates.TryRemove label |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// Item 2 — transactional account
// ──────────────────────────────────────────────────────────────────────────────

type AccountActor = private AccountActor of unit

/// <summary>
/// An ordinary immutable F# record as transactional state. It has no parameterless constructor
/// and no setter, which is exactly what Orleans' <c>TState : class, new()</c> constraint and its
/// mutate-in-place update model would normally rule out; the runtime's
/// <c>FunctionalTransactionalBox</c> is what makes it usable.
/// </summary>
type Ledger =
    { balance: decimal
      entries: string list }

[<NoEquality; NoComparison>]
type AccountApi =
    { /// Adds to the balance inside the caller's transaction.
      deposit: decimal -> Task<unit>
      /// Subtracts from the balance, throwing (and so aborting) on an overdraft.
      withdraw: decimal -> Task<unit>
      /// Reads the balance inside the caller's transaction.
      balance: unit -> Task<decimal>
      /// Reads the balance under a read-only transaction.
      peek: unit -> Task<decimal>
      /// Parks inside the transaction after taking a write lock, to force an overlap.
      slowDeposit: decimal * string -> Task<unit>
      /// Reads the balance with no transaction at all: the negative control.
      unsafeBalance: unit -> Task<decimal>
      /// Attempts an update from a read-only transactional operation, reporting what happened.
      peekAndWrite: unit -> Task<string>
      /// The address of the silo this activation lives on.
      whereAmI: unit -> Task<string>
      /// Declared 'Join': Orleans must refuse it outside a transaction.
      joinOnly: unit -> Task<decimal>
      /// Declared 'NotAllowed': Orleans must refuse it inside a transaction.
      notAllowed: unit -> Task<string>
      /// Reads the transactional value twice and reports whether the two are the same instance.
      readTwice: unit -> Task<bool>
      /// Declared readOnly + transactional: asks ANOTHER account to write inside the same
      /// transaction, and reports what happened.
      readOnlyDelegate: string -> Task<string> }

let accountLedger =
    TransactionalState.create<Ledger> "ledger" PhaseDStorage.Transactional

let accountContract =
    grainContract<AccountActor, string, AccountApi> {
        grainType PhaseDGrainTypes.Account
        version 1
        stringKey
        transactional Orleans.TransactionOption.CreateOrJoin (_.deposit)
        transactional Orleans.TransactionOption.CreateOrJoin (_.withdraw)
        transactional Orleans.TransactionOption.CreateOrJoin (_.balance)
        transactional Orleans.TransactionOption.CreateOrJoin (_.peek)
        transactional Orleans.TransactionOption.CreateOrJoin (_.slowDeposit)
        transactional Orleans.TransactionOption.CreateOrJoin (_.peekAndWrite)
        transactional Orleans.TransactionOption.Join (_.joinOnly)
        transactional Orleans.TransactionOption.NotAllowed (_.notAllowed)
        transactional Orleans.TransactionOption.CreateOrJoin (_.readTwice)
        transactional Orleans.TransactionOption.CreateOrJoin (_.readOnlyDelegate)
        readOnly (_.peek)
        readOnly (_.readOnlyDelegate)
        readOnly (_.peekAndWrite)
        readOnly (_.whereAmI)
    }

let accountDefinition =
    grainFor accountContract {
        defaultState (fun () -> ())

        transactionalStateFrom accountLedger (fun _ -> { balance = 0m; entries = [] })

        handle (_.deposit) (fun context state (amount: decimal) ->
            task {
                PhaseDCounters.enter $"deposit:{context.key}"

                do!
                    (context.transactionalState accountLedger)
                        .update (fun ledger ->
                            { ledger with
                                balance = ledger.balance + amount
                                entries = ledger.entries @ [ $"+{amount}" ] })

                return state, ()
            })

        handle (_.withdraw) (fun context state (amount: decimal) ->
            task {
                PhaseDCounters.enter $"withdraw:{context.key}"
                let ledger = context.transactionalState accountLedger
                let! current = ledger.read ()

                if current.balance < amount then
                    failwith $"insufficient funds: {current.balance} < {amount}"

                do!
                    ledger.update (fun value ->
                        { value with
                            balance = value.balance - amount
                            entries = value.entries @ [ $"-{amount}" ] })

                return state, ()
            })

        handle (_.balance) (fun context state () ->
            task {
                let! value = (context.transactionalState accountLedger).readWith (fun ledger -> ledger.balance)
                return state, value
            })

        handle (_.peek) (fun context state () ->
            task {
                let! value = (context.transactionalState accountLedger).readWith (fun ledger -> ledger.balance)
                return state, value
            })

        handle (_.slowDeposit) (fun context state ((amount: decimal), (gate: string)) ->
            task {
                PhaseDCounters.enter $"slowDeposit:{context.key}"
                let ledger = context.transactionalState accountLedger

                do!
                    ledger.update (fun value ->
                        { value with
                            balance = value.balance + amount
                            entries = value.entries @ [ $"+{amount}" ] })

                let! _ = PhaseDGates.wait gate 5000
                return state, ()
            })

        // The negative control: no 'transactional' declaration at all, so the facade must refuse
        // rather than silently read outside a transaction.
        handle (_.unsafeBalance) (fun context state () ->
            task {
                let! value = (context.transactionalState accountLedger).read ()
                return state, value.balance
            })

        // A read-only transactional operation: Orleans starts the transaction read-only, so an
        // update is refused. The runtime refuses it first, with a message naming 'readOnly'.
        handle (_.peekAndWrite) (fun context state () ->
            task {
                let outcome =
                    try
                        (context.transactionalState accountLedger)
                            .update (fun ledger -> { ledger with balance = ledger.balance + 1m })
                        |> ignore

                        "written"
                    with error ->
                        error.Message

                return state, outcome
            })

        handle (_.whereAmI) (fun context state () ->
            task {
                let details = context.services.GetRequiredService<ILocalSiloDetails>()
                return state, details.SiloAddress.ToString()
            })

        handle (_.joinOnly) (fun context state () ->
            task {
                let! value = (context.transactionalState accountLedger).readWith (fun ledger -> ledger.balance)
                return state, value
            })

        // 'NotAllowed' is not transaction-scoped, so the facade refuses the facet here; the
        // operation exists to prove Orleans refuses the CALL when one is ambient.
        handle (_.notAllowed) (fun _ state () -> task { return state, "reached" })

        // The runtime's own guard only covers THIS grain's facets. Whether the transaction itself
        // was started read-only is Orleans' business, and this is what asks it: a second
        // participant joining the same transaction and trying to write.
        handle (_.readOnlyDelegate) (fun context state (other: string) ->
            task {
                let target = FunctionalGrain.ref accountContract context.grainFactory other

                let! outcome =
                    task {
                        try
                            do! target.deposit 1m
                            return "the write was ACCEPTED"
                        with error ->
                            return error.GetType().Name
                    }

                return state, outcome
            })

        handle (_.readTwice) (fun context state () ->
            task {
                let ledger = context.transactionalState accountLedger
                let! first = ledger.read ()
                let! second = ledger.read ()
                return state, obj.ReferenceEquals(first, second)
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Item 2 — state-free orchestrator
// ──────────────────────────────────────────────────────────────────────────────

type AtmActor = private AtmActor of unit

[<NoEquality; NoComparison>]
type AtmApi =
    { /// Moves funds between two accounts in ONE transaction it creates itself.
      transfer: string * string * decimal -> Task<unit>
      /// Deposits into two accounts in one transaction, then throws: the abort control.
      failAfterDeposits: string * string * decimal -> Task<unit>
      /// Reads both balances in one transaction.
      totals: string * string -> Task<decimal * decimal>
      /// Calls a 'Join' operation from inside a transaction it created: must succeed.
      callJoin: string -> Task<decimal>
      /// Calls a 'NotAllowed' operation from inside a transaction: must be refused.
      callNotAllowed: string -> Task<string> }

let atmContract =
    grainContract<AtmActor, string, AtmApi> {
        grainType PhaseDGrainTypes.Atm
        version 1
        stringKey
        transactional Orleans.TransactionOption.Create (_.transfer)
        transactional Orleans.TransactionOption.Create (_.failAfterDeposits)
        transactional Orleans.TransactionOption.Create (_.totals)
        transactional Orleans.TransactionOption.Create (_.callJoin)
        transactional Orleans.TransactionOption.Create (_.callNotAllowed)
    }

/// <summary>
/// The orchestrator has NO transactional facet of its own — Orleans allows a state-free
/// participant, and its own <c>FSharpAtmGrain</c> in this repository's classic KEEP-path is the
/// same shape. It exists to prove that <c>transactional</c> without
/// <c>transactionalStateFrom</c> is a supported combination.
/// </summary>
let atmDefinition =
    grainFor atmContract {
        defaultState (fun () -> ())

        handle (_.transfer) (fun context state ((from: string), (into: string), (amount: decimal)) ->
            task {
                PhaseDCounters.enter $"transfer:{context.key}"
                let source = FunctionalGrain.ref accountContract context.grainFactory from
                let target = FunctionalGrain.ref accountContract context.grainFactory into

                do! source.withdraw amount
                do! target.deposit amount
                return state, ()
            })

        handle (_.failAfterDeposits) (fun context state ((left: string), (right: string), (amount: decimal)) ->
            task {
                let first = FunctionalGrain.ref accountContract context.grainFactory left
                let second = FunctionalGrain.ref accountContract context.grainFactory right

                do! first.deposit amount
                do! second.deposit amount
                return failwith "the orchestrator failed after both participants had written"
            })

        handle (_.totals) (fun context state ((left: string), (right: string)) ->
            task {
                let first = FunctionalGrain.ref accountContract context.grainFactory left
                let second = FunctionalGrain.ref accountContract context.grainFactory right

                let! leftBalance = first.balance ()
                let! rightBalance = second.balance ()
                return state, (leftBalance, rightBalance)
            })

        handle (_.callJoin) (fun context state (key: string) ->
            task {
                let account = FunctionalGrain.ref accountContract context.grainFactory key
                let! value = account.joinOnly ()
                return state, value
            })

        handle (_.callNotAllowed) (fun context state (key: string) ->
            task {
                let account = FunctionalGrain.ref accountContract context.grainFactory key

                let! outcome =
                    task {
                        try
                            let! reached = account.notAllowed ()
                            return reached
                        with error ->
                            return error.Message
                    }

                return state, outcome
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Item 2 — transactional and persistent facets on one definition
// ──────────────────────────────────────────────────────────────────────────────

type MixedActor = private MixedActor of unit

type Counter = { hits: int }

[<NoEquality; NoComparison>]
type MixedApi =
    { /// Transaction-scoped: writes the transactional facet only.
      bump: int -> Task<unit>
      /// Transaction-scoped: attempts to write the persistent facet, which must be refused.
      bumpPersistent: int -> Task<string>
      /// Not transactional: writes the persistent facet normally.
      note: string -> Task<unit>
      /// Not transactional: reads the persistent facet.
      notes: unit -> Task<string list>
      /// Transaction-scoped: returns a replacement primary state, which must be discarded.
      bumpAndPublish: int -> Task<unit>
      /// Not transactional: reads the primary state.
      published: unit -> Task<int>
      /// Transaction-scoped: reads the transactional facet.
      total: unit -> Task<int>
      /// Transaction-scoped: writes BOTH transactional facets in one transaction.
      bumpBoth: int * string -> Task<unit>
      /// Transaction-scoped: reads the second transactional facet.
      trail: unit -> Task<string list> }

let mixedCounter =
    TransactionalState.create<Counter> "counter" PhaseDStorage.Transactional

let mixedTrail =
    TransactionalState.create<Ledger> "trail" PhaseDStorage.Transactional

let mixedNotes =
    PersistentState.create<string list> "notes" PhaseDStorage.Persistent

let mixedContract =
    grainContract<MixedActor, string, MixedApi> {
        grainType PhaseDGrainTypes.Mixed
        version 1
        stringKey
        transactional Orleans.TransactionOption.CreateOrJoin (_.bump)
        transactional Orleans.TransactionOption.CreateOrJoin (_.bumpPersistent)
        transactional Orleans.TransactionOption.CreateOrJoin (_.bumpAndPublish)
        transactional Orleans.TransactionOption.CreateOrJoin (_.total)
        transactional Orleans.TransactionOption.CreateOrJoin (_.bumpBoth)
        transactional Orleans.TransactionOption.CreateOrJoin (_.trail)
    }

let mixedDefinition =
    grainFor mixedContract {
        defaultState (fun () -> 0)

        transactionalStateFrom mixedCounter (fun _ -> { hits = 0 })
        transactionalStateFrom mixedTrail (fun _ -> { balance = 0m; entries = [] })
        usePersistentState mixedNotes (fun _ -> ([]: string list))

        handle (_.bump) (fun context state (by: int) ->
            task {
                do! (context.transactionalState mixedCounter).update (fun counter -> { hits = counter.hits + by })
                return state, ()
            })

        handle (_.bumpPersistent) (fun context state (by: int) ->
            task {
                do! (context.transactionalState mixedCounter).update (fun counter -> { hits = counter.hits + by })

                let outcome =
                    try
                        let facet = context.persistentState mixedNotes
                        facet.State <- facet.State @ [ "written from a transaction" ]
                        "written"
                    with error ->
                        error.Message

                return state, outcome
            })

        handle (_.note) (fun context state (text: string) ->
            task {
                let facet = context.persistentState mixedNotes
                facet.State <- facet.State @ [ text ]
                do! facet.WriteStateAsync()
                return state, ()
            })

        handle (_.notes) (fun context state () ->
            task { return state, (context.persistentState mixedNotes).State })

        handle (_.bumpAndPublish) (fun context state (by: int) ->
            task {
                do! (context.transactionalState mixedCounter).update (fun counter -> { hits = counter.hits + by })
                return state + by, ()
            })

        handle (_.published) (fun _ state () -> task { return state, state })

        handle (_.total) (fun context state () ->
            task {
                let! counter = (context.transactionalState mixedCounter).read ()
                return state, counter.hits
            })

        // Two transactional facets of DIFFERENT stored types, written in one transaction: each
        // registers its own exact-type ITransactionDataCopier, and both are participants of the
        // same transaction on the same activation.
        handle (_.bumpBoth) (fun context state ((by: int), (note: string)) ->
            task {
                do! (context.transactionalState mixedCounter).update (fun counter -> { hits = counter.hits + by })

                do!
                    (context.transactionalState mixedTrail)
                        .update (fun value ->
                            { value with
                                balance = value.balance + decimal by
                                entries = value.entries @ [ note ] })

                return state, ()
            })

        handle (_.trail) (fun context state () ->
            task {
                let! value = (context.transactionalState mixedTrail).readWith (fun ledger -> ledger.entries)
                return state, value
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Bound references
// ──────────────────────────────────────────────────────────────────────────────

let accountRef = FunctionalGrain.ref accountContract
let atmRef = FunctionalGrain.ref atmContract
let mixedRef = FunctionalGrain.ref mixedContract

// ──────────────────────────────────────────────────────────────────────────────
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

type PhaseDSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.UseTransactions() |> ignore

            // A short lock-acquire timeout so the contention test resolves in seconds rather than
            // in Orleans' 10-second default, while staying far below the transaction timeout
            // TransactionRequestBase.Invoke applies (also 10 seconds).
            siloBuilder.Configure<TransactionalStateOptions>(fun (options: TransactionalStateOptions) ->
                options.LockAcquireTimeout <- TimeSpan.FromSeconds 2.0)
            |> ignore

            siloBuilder.AddMemoryGrainStorage PhaseDStorage.Transactional |> ignore
            siloBuilder.AddMemoryGrainStorage PhaseDStorage.Persistent |> ignore
            siloBuilder.AddFunctionalGrain accountDefinition |> ignore
            siloBuilder.AddFunctionalGrain atmDefinition |> ignore
            siloBuilder.AddFunctionalGrain mixedDefinition |> ignore

type PhaseDClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

/// <summary>
/// Two silos, so a transaction whose participants land on different silos is the ordinary case
/// rather than a special one. Orleans places the two account activations by its default strategy;
/// the cross-silo test picks keys until it finds a pair that really is split.
/// </summary>
[<Sealed>]
type FunctionalPhaseDFixture() =
    let cluster =
        let builder = TestClusterBuilder 2s
        builder.AddSiloBuilderConfigurator<PhaseDSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<PhaseDClientConfigurator>() |> ignore
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

[<CollectionDefinition("FunctionalPhaseD")>]
type FunctionalPhaseDCollection() =
    interface ICollectionFixture<FunctionalPhaseDFixture>
