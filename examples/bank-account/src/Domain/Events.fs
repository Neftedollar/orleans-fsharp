namespace BankAccount.Domain

/// <summary>
/// Events that can occur on a bank account.
/// These are the immutable facts stored in the event log and replayed to rebuild state.
/// </summary>
type AccountEvent =
    /// <summary>A deposit was made to the account.</summary>
    | Deposited of amount: decimal
    /// <summary>A withdrawal was made from the account.</summary>
    | Withdrawn of amount: decimal
