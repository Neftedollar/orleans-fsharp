/// <summary>
/// Experiment 14 — distributed ACID transactions on the functional runtime: a
/// <c>transactionalStateFrom</c> facet, per-operation <c>transactional</c> policy, and a
/// state-free orchestrator that drives two participants inside one transaction. Both halves are
/// shown: a commit that moves both states together, and an abort that moves neither.
/// </summary>
namespace FeatureTour.Transactions

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Orleans.FSharp

/// <summary>
/// How many times each handler body has been entered, read WITHOUT calling the grain — the
/// transaction that would report it is exactly the one that aborted.
/// </summary>
[<RequireQualifiedAccess>]
module Entries =
    let private counters = ConcurrentDictionary<string, StrongBox<int>>()

    let private cell (label: string) =
        counters.GetOrAdd(label, fun _ -> StrongBox<int> 0)

    let enter (label: string) =
        let box = cell label
        Interlocked.Increment(&box.Value) |> ignore

    let count (label: string) =
        let box = cell label
        Volatile.Read(&box.Value)

// ── The transactional participant ────────────────────────────────────────────

type AccountActor = private AccountActor of unit

/// <summary>
/// An ordinary immutable F# record as transactional state. Orleans requires
/// <c>TState : class, new()</c> and applies an update by mutating the instance it stores, so a
/// record would normally be unusable; the runtime keeps the record inside its own box and hands
/// application code a plain <c>'State -&gt; 'State</c> function.
/// </summary>
type Ledger =
    { balance: decimal
      entries: string list }

/// <summary>A business rejection from the pure ledger core.</summary>
type WithdrawalError =
    | InsufficientFunds of available: decimal * requested: decimal

[<RequireQualifiedAccess>]
module WithdrawalError =
    let describe = function
        | InsufficientFunds(available, requested) ->
            $"insufficient funds: {available} < {requested}"

[<RequireQualifiedAccess>]
module Ledger =
    let deposit amount ledger =
        { ledger with
            balance = ledger.balance + amount
            entries = ledger.entries @ [ $"+{amount}" ] }

    let withdraw amount ledger =
        if ledger.balance < amount then
            Error(InsufficientFunds(ledger.balance, amount))
        else
            Ok
                { ledger with
                    balance = ledger.balance - amount
                    entries = ledger.entries @ [ $"-{amount}" ] }

[<NoEquality; NoComparison>]
type AccountApi =
    { /// Adds to the balance inside the caller's transaction.
      deposit: decimal -> Task<unit>
      /// Subtracts, translating a typed domain rejection into an Orleans transaction abort.
      withdraw: decimal -> Task<unit>
      /// Reads the balance inside the caller's transaction.
      balance: unit -> Task<decimal>
      /// Reads the balance from a READ-ONLY transaction, and tries to write from it.
      peekAndWrite: unit -> Task<string>
      /// Reads the transactional state with NO transaction at all: the negative control.
      unguarded: unit -> Task<string> }

[<RequireQualifiedAccess>]
module AccountApi =
    [<Literal>]
    let GrainType = "tour.account"

    [<Literal>]
    let Storage = "TourTransactionStore"

    let ledger = TransactionalState.create<Ledger> "ledger" Storage

    let contract =
        grainContract<AccountActor, string, AccountApi> {
            grainType GrainType
            version 1
            stringKey
            transactional Orleans.TransactionOption.CreateOrJoin (_.deposit)
            transactional Orleans.TransactionOption.CreateOrJoin (_.withdraw)
            transactional Orleans.TransactionOption.CreateOrJoin (_.balance)
            transactional Orleans.TransactionOption.CreateOrJoin (_.peekAndWrite)
            readOnly (_.peekAndWrite)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module AccountDefinition =

    let definition =
        grainFor AccountApi.contract {
            defaultState (fun () -> ())

            transactionalStateFrom AccountApi.ledger (fun _ -> { balance = 0m; entries = [] })

            handle (_.deposit) (fun context state (amount: decimal) ->
                task {
                    Entries.enter $"deposit:{context.key}"

                    do!
                        (context.transactionalState AccountApi.ledger)
                            .update (Ledger.deposit amount)

                    return state, ()
                })

            handle (_.withdraw) (fun context state (amount: decimal) ->
                task {
                    Entries.enter $"withdraw:{context.key}"
                    let ledger = context.transactionalState AccountApi.ledger
                    let! current = ledger.read ()

                    match Ledger.withdraw amount current with
                    | Error rejection ->
                        // Orleans aborts a transaction on a fault. Keep that framework concern at
                        // the actor boundary; the reusable business rule above remains Result-based.
                        return raise (InvalidOperationException(WithdrawalError.describe rejection))
                    | Ok updated ->
                        do! ledger.update (fun _ -> updated)
                        return state, ()
                })

            handle (_.balance) (fun context state () ->
                task {
                    let! value =
                        (context.transactionalState AccountApi.ledger)
                            .readWith (fun ledger -> ledger.balance)

                    return state, value
                })

            handle (_.peekAndWrite) (fun context state () ->
                task {
                    let ledger = context.transactionalState AccountApi.ledger
                    let! seen = ledger.readWith (fun value -> value.balance)

                    let outcome =
                        try
                            ledger.update (fun value -> { value with balance = value.balance + 1m })
                            |> ignore

                            "the update was ACCEPTED (unexpected)"
                        with error ->
                            $"read {seen}; the update was refused"

                    return state, outcome
                })

            handle (_.unguarded) (fun context state () ->
                task {
                    let outcome =
                        try
                            (context.transactionalState AccountApi.ledger).read ()
                            |> ignore

                            "the read was ACCEPTED (unexpected)"
                        with error ->
                            "the read was refused"

                    return state, outcome
                })
        }

// ── The state-free orchestrator ──────────────────────────────────────────────

type TellerActor = private TellerActor of unit

type TransferRequest =
    { fromAccount: string
      toAccount: string
      amount: decimal }

type AccountPair =
    { leftAccount: string
      rightAccount: string }

type AccountBalances =
    { leftBalance: decimal
      rightBalance: decimal }

[<NoEquality; NoComparison>]
type TellerApi =
    { /// Moves funds between two accounts in ONE transaction it creates itself.
      transfer: TransferRequest -> Task<unit>
      /// Reads both balances in one transaction, so the pair is a consistent snapshot.
      totals: AccountPair -> Task<AccountBalances> }

[<RequireQualifiedAccess>]
module TellerApi =
    [<Literal>]
    let GrainType = "tour.teller"

    /// <summary>
    /// The orchestrator declares <c>transactional</c> but attaches NO transactional state: a
    /// state-free participant is a supported shape, and it is the shape every "unit of work"
    /// grain has.
    /// </summary>
    let contract =
        grainContract<TellerActor, string, TellerApi> {
            grainType GrainType
            version 2
            stringKey
            transactional Orleans.TransactionOption.Create (_.transfer)
            transactional Orleans.TransactionOption.Create (_.totals)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module TellerDefinition =

    let definition =
        grainFor TellerApi.contract {
            defaultState (fun () -> ())

            handle (_.transfer) (fun context state (request: TransferRequest) ->
                task {
                    let source = AccountApi.ref context.grainFactory request.fromAccount
                    let target = AccountApi.ref context.grainFactory request.toAccount

                    do! source.withdraw request.amount
                    do! target.deposit request.amount
                    return state, ()
                })

            handle (_.totals) (fun context state (pair: AccountPair) ->
                task {
                    let first = AccountApi.ref context.grainFactory pair.leftAccount
                    let second = AccountApi.ref context.grainFactory pair.rightAccount

                    let! leftBalance = first.balance ()
                    let! rightBalance = second.balance ()

                    return
                        state,
                        { leftBalance = leftBalance
                          rightBalance = rightBalance }
                })
        }
