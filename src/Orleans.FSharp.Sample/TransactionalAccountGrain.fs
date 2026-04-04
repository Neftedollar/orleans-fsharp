namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.Transactions
open Orleans.FSharp.Runtime

/// <summary>
/// Mutable state for the transactional account grain.
/// Must be a class (not a record) because <see cref="Orleans.Transactions.Abstractions.ITransactionalState{T}"/>
/// requires in-place mutation via <c>CopyState</c>.
/// </summary>
[<GenerateSerializer>]
[<Sealed>]
type TransactionalAccountState() =
    /// <summary>Current account balance. Defaults to 0.</summary>
    [<Id(0u)>]
    member val Balance: decimal = 0m with get, set

/// <summary>
/// Module containing the F# grain definition for the transactional account grain.
/// All operations (Deposit, Withdraw, GetBalance) are expressed as pure functions over
/// <see cref="TransactionalAccountState"/>; the base class wires them to Orleans transactions.
/// </summary>
module TransactionalAccountGrainDef =

    /// <summary>
    /// Definition for the transactional account grain.
    /// <list type="bullet">
    ///   <item><description><c>Deposit</c>: adds the amount to the balance and returns updated state.</description></item>
    ///   <item><description><c>Withdraw</c>: subtracts the amount; throws <c>InvalidOperationException</c> on overdraft.</description></item>
    ///   <item><description><c>GetBalance</c>: reads the current balance.</description></item>
    ///   <item><description><c>CopyState</c>: copies field values from source to target (required for transactional state).</description></item>
    /// </list>
    /// </summary>
    let transactionalAccountDef: TransactionalGrainDefinition<TransactionalAccountState> =
        {
            Deposit = fun (state: TransactionalAccountState) (amount: decimal) ->
                let newState = TransactionalAccountState()
                newState.Balance <- state.Balance + amount
                newState

            Withdraw = fun (state: TransactionalAccountState) (amount: decimal) ->
                if state.Balance < amount then
                    failwith "Overdraft"
                let newState = TransactionalAccountState()
                newState.Balance <- state.Balance - amount
                newState

            GetBalance = fun (state: TransactionalAccountState) ->
                state.Balance

            CopyState = fun (source: TransactionalAccountState) (target: TransactionalAccountState) ->
                target.Balance <- source.Balance
        }

/// <summary>
/// Grain interface for the transactional account grain.
/// Methods use <c>TransactionOption.CreateOrJoin</c>: they create a new transaction when called
/// standalone (e.g. from tests), or join an existing one (e.g. when called from the ATM grain).
/// </summary>
type ITransactionalAccountGrain =
    inherit IGrainWithStringKey

    /// <summary>Deposits <paramref name="amount"/> into the account within the ambient transaction.</summary>
    [<Transaction(TransactionOption.CreateOrJoin)>]
    abstract Deposit: amount: decimal -> Task

    /// <summary>Withdraws <paramref name="amount"/> from the account within the ambient transaction. Throws on overdraft.</summary>
    [<Transaction(TransactionOption.CreateOrJoin)>]
    abstract Withdraw: amount: decimal -> Task

    /// <summary>Returns the current balance within the ambient transaction.</summary>
    [<Transaction(TransactionOption.CreateOrJoin)>]
    abstract GetBalance: unit -> Task<decimal>

/// <summary>
/// Grain interface for the ATM grain that orchestrates cross-account transfers.
/// The <c>Transfer</c> method creates a new Orleans transaction context.
/// </summary>
type ITransactionalAtmGrain =
    inherit IGrainWithStringKey

    /// <summary>
    /// Atomically transfers <paramref name="amount"/> from the account identified by
    /// <paramref name="fromKey"/> to the account identified by <paramref name="toKey"/>.
    /// Creates a new transaction; both withdraw and deposit are atomic.
    /// </summary>
    [<Transaction(TransactionOption.Create)>]
    abstract Transfer: fromKey: string * toKey: string * amount: decimal -> Task
