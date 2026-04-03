namespace BankAccount.Domain

open System.Threading.Tasks
open Orleans

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
/// State of a bank account, derived by folding events.
/// Uses a sealed class so JournaledGrain can mutate it in place.
/// </summary>
[<Sealed>]
type AccountState() =
    /// <summary>The current account balance.</summary>
    member val Balance: decimal = 0m with get, set

/// <summary>
/// Grain interface for the event-sourced bank account grain.
/// </summary>
type IBankAccountGrain =
    inherit IGrainWithStringKey

    /// <summary>Handles a bank account command and returns a boxed result.</summary>
    abstract HandleCommand: AccountCommand -> Task<obj>
