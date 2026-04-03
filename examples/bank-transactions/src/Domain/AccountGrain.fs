namespace BankTransactions.Domain

open System.Threading.Tasks
open Orleans.FSharp

/// <summary>
/// Module containing the transactional bank account grain definition.
/// The actual transactional state management is handled in the C# CodeGen grain class
/// since it requires [TransactionalState] attribute injection via constructor.
/// This module provides the pure business logic.
/// </summary>
module AccountGrainDef =

    /// <summary>
    /// Validates and applies a deposit to the account balance.
    /// </summary>
    /// <param name="balance">The current account balance state.</param>
    /// <param name="amount">The amount to deposit.</param>
    /// <returns>The updated account balance state.</returns>
    let deposit (balance: AccountBalance) (amount: decimal) : AccountBalance =
        let newBalance = AccountBalance()
        newBalance.Balance <- balance.Balance + amount
        newBalance

    /// <summary>
    /// Validates and applies a withdrawal to the account balance.
    /// Throws InvalidOperationException if the withdrawal would cause an overdraft.
    /// </summary>
    /// <param name="balance">The current account balance state.</param>
    /// <param name="amount">The amount to withdraw.</param>
    /// <returns>The updated account balance state.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the withdrawal amount exceeds the current balance.
    /// </exception>
    let withdraw (balance: AccountBalance) (amount: decimal) : AccountBalance =
        if balance.Balance < amount then
            invalidOp $"Insufficient funds: balance={balance.Balance}, requested={amount}"

        let newBalance = AccountBalance()
        newBalance.Balance <- balance.Balance - amount
        newBalance
