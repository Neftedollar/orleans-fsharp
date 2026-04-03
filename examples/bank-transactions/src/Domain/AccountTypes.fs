namespace BankTransactions.Domain

open System.Threading.Tasks
open Orleans

/// <summary>
/// Mutable account balance state for transactional grains.
/// Must be a reference type with a parameterless constructor (Orleans constraint for ITransactionalState).
/// </summary>
[<Sealed>]
type AccountBalance() =
    /// <summary>The current account balance.</summary>
    member val Balance: decimal = 0m with get, set

/// <summary>
/// Commands that can be sent to the bank account grain.
/// </summary>
type AccountCommand =
    /// <summary>Deposit funds into the account.</summary>
    | Deposit of amount: decimal
    /// <summary>Withdraw funds from the account (rejected if insufficient balance).</summary>
    | Withdraw of amount: decimal
    /// <summary>Query the current balance without modifying state.</summary>
    | GetBalance

/// <summary>
/// Grain interface for the transactional bank account grain.
/// In C# CodeGen, methods that modify state use [Transaction(TransactionOption.Join)]
/// so they can participate in cross-grain atomic transfers.
/// </summary>
type ITransactionalAccountGrain =
    inherit IGrainWithStringKey

    /// <summary>Deposit funds into the account (transactional).</summary>
    abstract Deposit: amount: decimal -> Task

    /// <summary>Withdraw funds from the account (transactional, throws on overdraft).</summary>
    abstract Withdraw: amount: decimal -> Task

    /// <summary>Get the current balance (transactional read).</summary>
    abstract GetBalance: unit -> Task<decimal>

/// <summary>
/// Grain interface for orchestrating atomic transfers between two accounts.
/// The Transfer method creates a new transaction that calls Withdraw on one grain
/// and Deposit on another -- both succeed or both fail.
/// </summary>
type IAtmGrain =
    inherit IGrainWithStringKey

    /// <summary>
    /// Atomically transfer funds from one account to another.
    /// Creates a transaction, withdraws from the source, and deposits to the destination.
    /// </summary>
    abstract Transfer: fromAccount: string * toAccount: string * amount: decimal -> Task
