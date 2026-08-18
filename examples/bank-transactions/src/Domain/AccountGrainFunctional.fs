/// <summary>
/// Functional-runtime equivalent of <c>AccountGrainDef.transactionalAccount</c> and
/// <c>AccountGrainDef.atm</c> in <c>AccountGrain.fs</c> (the <c>FSharpTransactionalGrain</c> /
/// <c>FSharpAtmGrain</c> originals). Full-depth twin over the SAME domain: the same
/// <c>AccountBalance</c> transactional state, under the same state name and the same
/// <c>"TransactionStore"</c> storage, driven by literally the same
/// <c>AccountGrainDef.deposit</c> / <c>AccountGrainDef.withdraw</c> functions -- passed as update
/// functions rather than restated. Nothing about the business rules lives in this file.
///
/// Classic member -> functional operation, where the shapes differ:
///
///   | classic | functional | note |
///   |---|---|---|
///   | <c>ITransactionalAccountGrain.Deposit</c> | <c>AccountApi.deposit</c> | <c>transactional CreateOrJoin</c> |
///   | <c>ITransactionalAccountGrain.Withdraw</c> | <c>AccountApi.withdraw</c> | throws, and so aborts, exactly as before |
///   | <c>ITransactionalAccountGrain.GetBalance</c> | <c>AccountApi.balance</c> | <c>transactional CreateOrJoin</c> |
///   | <c>IAtmGrain.Transfer</c> | <c>AtmApi.transfer</c> | <c>transactional Create</c> |
///   | — | <c>AtmApi.totals</c> | both balances read in ONE transaction |
///   | <c>[&lt;TransactionalState("state", "TransactionStore")&gt;]</c> ctor injection | <c>transactionalStateFrom</c> + <c>context.transactionalState</c> | same names, same storage |
///   | <c>[&lt;Transaction(TransactionOption.X)&gt;]</c> on an interface method | <c>transactional TransactionOption.X (_.op)</c> on the contract | same Orleans policy, declared instead of attributed |
///   | <c>[&lt;Reentrant&gt;]</c> on the grain class | — | the functional runtime applies what a transactional definition needs |
///   | <c>CopyState: 'State -&gt; 'State -&gt; unit</c> | — | see below |
///
/// <b>The disappearing <c>CopyState</c>.</b> Orleans applies a transactional update by mutating
/// the instance it stores, so the classic definition had to carry a fourth function whose only job
/// was to copy the fields of a freshly computed state back into Orleans' instance -- a field-by-
/// field copy every state type has to hand-write and keep in step with its own shape. The
/// functional runtime keeps the application's value inside its own box and hands application code
/// a plain <c>'State -&gt; 'State</c>, performing the single reference assignment itself, so there
/// is nothing left to hand-write. That is also why an ordinary immutable F# record works as
/// transactional state here even though Orleans' own constraint is <c>TState : class, new()</c>;
/// this twin keeps the classic <c>AccountBalance</c> instead, because reusing the very same state
/// type is what makes the parity checkable.
///
/// <b>The abort path is unchanged.</b> <c>AccountGrainDef.withdraw</c> throws
/// <c>InvalidOperationException</c> on an overdraft. Handed to <c>update</c>, it throws inside
/// Orleans' write lock, the handler faults, and Orleans aborts the whole transaction -- so a failed
/// transfer leaves BOTH balances exactly as they were. Nothing in this file catches or retries;
/// the rollback is Orleans'.
/// </summary>
namespace BankTransactions.Domain

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>The actor brand of the transactional account twin.</summary>
type AccountActor = private AccountActor of unit

/// <summary>The actor brand of the ATM orchestrator twin.</summary>
type AtmActor = private AtmActor of unit

[<NoEquality; NoComparison>]
type AccountApi =
    { /// <summary>Adds to the balance inside the caller's transaction.</summary>
      deposit: decimal -> Task<unit>
      /// <summary>Subtracts from the balance, throwing -- and so aborting the whole transaction --
      /// on an overdraft.</summary>
      withdraw: decimal -> Task<unit>
      /// <summary>Reads the balance inside the caller's transaction.</summary>
      balance: unit -> Task<decimal> }

[<NoEquality; NoComparison>]
type AtmApi =
    { /// <summary>Moves funds between two accounts in ONE transaction it creates itself.</summary>
      transfer: string * string * decimal -> Task<unit>
      /// <summary>Reads both balances in one transaction, so the pair is a consistent snapshot
      /// rather than two reads that could straddle a commit. No classic counterpart.</summary>
      totals: string * string -> Task<decimal * decimal>
      /// <summary>Performs the whole transfer and THEN throws. No classic counterpart, and the
      /// only way to see a real rollback: the overdraft case aborts before the second account is
      /// touched, so it proves short-circuiting rather than atomicity. Here both accounts really
      /// are written, and both writes have to be undone.</summary>
      transferThenFail: string * string * decimal -> Task<unit> }

[<RequireQualifiedAccess>]
module AccountApi =

    /// <summary>The transactional state name. Part of the <c>ParticipantId</c> Orleans uses during
    /// the commit protocol and of the storage key, so it is durable identity -- the same
    /// <c>"state"</c> the classic <c>[&lt;TransactionalState&gt;]</c> attribute names.</summary>
    [<Literal>]
    let StateName = "state"

    /// <summary>The transactional storage name, registered on the silo with
    /// <c>addMemoryStorage "TransactionStore"</c>. The same name the classic attribute names.</summary>
    [<Literal>]
    let Storage = "TransactionStore"

    /// <summary>
    /// The transactional facet descriptor: the functional counterpart of the classic grain's
    /// <c>[&lt;TransactionalState("state", "TransactionStore")&gt;]</c> constructor parameter.
    /// </summary>
    let ledger = TransactionalState.create<AccountBalance> StateName Storage

    let contract =
        grainContract<AccountActor, string, AccountApi> () {
            grainType "bank-transactions.account.functional"
            version 1
            stringKey

            // CreateOrJoin, not Join: the classic demo calls Deposit/GetBalance straight from the
            // client with no ambient transaction AND from inside the ATM's transaction, and
            // CreateOrJoin is exactly that -- join the caller's transaction when there is one,
            // start one otherwise. `Join` would refuse the direct calls; `Create` would give the
            // transfer three transactions instead of one, and lose atomicity.
            transactional TransactionOption.CreateOrJoin (_.deposit)
            transactional TransactionOption.CreateOrJoin (_.withdraw)
            transactional TransactionOption.CreateOrJoin (_.balance)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module AtmApi =

    let contract =
        grainContract<AtmActor, string, AtmApi> () {
            grainType "bank-transactions.atm.functional"
            version 1
            stringKey

            // The orchestrator declares `transactional` and attaches NO transactional state of its
            // own -- the shape every "unit of work" grain has, and the same shape the classic
            // FSharpAtmGrain had. `Create`: the transfer is the transaction boundary.
            transactional TransactionOption.Create (_.transfer)
            transactional TransactionOption.Create (_.totals)
            transactional TransactionOption.Create (_.transferThenFail)
        }

    let ref = FunctionalGrain.ref contract

module AccountFunctionalDef =

    /// <summary>
    /// The transactional account. Its primary state is <c>unit</c>: everything durable lives in
    /// the transactional facet, so there is nothing for a handler to publish.
    /// </summary>
    let account =
        grainFor AccountApi.contract {
            defaultState (fun () -> ())

            // The initializer supplies the value a facet that was never written reads as. It is
            // substituted on read and stores nothing, so a pure read never becomes a write.
            transactionalStateFrom AccountApi.ledger (fun _key -> AccountBalance())

            // `AccountGrainDef.deposit` is `AccountBalance -> decimal -> AccountBalance`; an
            // update function is `'State -> 'State`. Partially applying the amount is the whole
            // adaptation -- no CopyState, and no restated arithmetic.
            handle (_.deposit) (fun context state (amount: decimal) ->
                task {
                    do! (context.transactionalState AccountApi.ledger).update (fun balance ->
                        AccountGrainDef.deposit balance amount)

                    return state, ()
                })

            // The same, with the classic overdraft guard intact: `AccountGrainDef.withdraw` raises
            // InvalidOperationException, which faults this handler and aborts the transaction.
            handle (_.withdraw) (fun context state (amount: decimal) ->
                task {
                    do! (context.transactionalState AccountApi.ledger).update (fun balance ->
                        AccountGrainDef.withdraw balance amount)

                    return state, ()
                })

            // `readWith` projects inside Orleans' read lock and returns the application's own
            // value uncopied -- the right member for reading one field out of the state.
            handle (_.balance) (fun context state () ->
                task {
                    let! value =
                        (context.transactionalState AccountApi.ledger)
                            .readWith AccountGrainDef.transactionalAccount.GetBalance

                    return state, value
                })
        }

module AtmFunctionalDef =

    /// <summary>
    /// The orchestrator. The body is the classic <c>AccountGrainDef.atm</c>'s <c>Transfer</c>
    /// verbatim -- withdraw from the source, deposit into the target -- over functional references
    /// bound from the invocation context instead of <c>GrainFactory.GetGrain</c>.
    /// </summary>
    let atm =
        grainFor AtmApi.contract {
            defaultState (fun () -> ())

            handle (_.transfer) (fun context state ((from: string), (into: string), (amount: decimal)) ->
                task {
                    let source = AccountApi.ref context.grainFactory from
                    let target = AccountApi.ref context.grainFactory into

                    do! source.withdraw amount
                    do! target.deposit amount
                    return state, ()
                })

            handle (_.totals) (fun context state ((left: string), (right: string)) ->
                task {
                    let first = AccountApi.ref context.grainFactory left
                    let second = AccountApi.ref context.grainFactory right

                    let! leftBalance = first.balance ()
                    let! rightBalance = second.balance ()
                    return state, (leftBalance, rightBalance)
                })

            // The atomicity control. Both participants complete their writes, and only then does
            // the orchestrator fail -- so the rollback has two committed-in-progress writes to
            // undo, on two different grains, rather than one that never started.
            handle (_.transferThenFail) (fun context state ((from: string), (into: string), (amount: decimal)) ->
                task {
                    let source = AccountApi.ref context.grainFactory from
                    let target = AccountApi.ref context.grainFactory into

                    do! source.withdraw amount
                    do! target.deposit amount
                    return failwith "the orchestrator failed after both accounts had been written"
                })
        }
