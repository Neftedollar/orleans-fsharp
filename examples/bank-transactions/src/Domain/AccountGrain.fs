namespace BankTransactions.Domain

open System.Threading.Tasks
open Orleans.FSharp
open Orleans.FSharp.Runtime

/// <summary>
/// Module containing the transactional bank account grain definition.
/// Provides the pure business logic for deposits, withdrawals, and balance queries.
/// The actual transactional state management is handled by FSharpTransactionalGrain.
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

    /// <summary>
    /// The transactional account grain definition for use with FSharpTransactionalGrain.
    /// </summary>
    let transactionalAccount : TransactionalGrainDefinition<AccountBalance> =
        {
            Deposit = deposit
            Withdraw = withdraw
            GetBalance = fun state -> state.Balance
            CopyState = fun source target -> target.Balance <- source.Balance
        }

    /// <summary>
    /// The ATM grain definition for orchestrating cross-grain transfers.
    /// </summary>
    let atm : AtmGrainDefinition<ITransactionalAccountGrain> =
        {
            Transfer = fun from to' amount ->
                task {
                    do! from.Withdraw(amount)
                    do! to'.Deposit(amount)
                }
        }
