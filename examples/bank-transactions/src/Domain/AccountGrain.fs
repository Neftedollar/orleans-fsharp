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
    /// Validates and applies a withdrawal to the account balance as a pure domain decision.
    /// </summary>
    /// <param name="balance">The current account balance state.</param>
    /// <param name="amount">The amount to withdraw.</param>
    /// <returns>The updated account balance, or a typed overdraft rejection.</returns>
    let withdraw (balance: AccountBalance) (amount: decimal) : Result<AccountBalance, AccountError> =
        if balance.Balance < amount then
            Error(InsufficientFunds(balance.Balance, amount))
        else
            let newBalance = AccountBalance()
            newBalance.Balance <- balance.Balance - amount
            Ok newBalance

    /// <summary>
    /// Orleans aborts a transaction when its participant faults. This is the narrow OO boundary
    /// that translates the pure domain <c>Result</c> into that framework signal.
    /// </summary>
    let withdrawAtBoundary (balance: AccountBalance) (amount: decimal) : AccountBalance =
        match withdraw balance amount with
        | Ok updated -> updated
        | Error rejection ->
            raise (System.InvalidOperationException(AccountError.describe rejection))

    /// <summary>
    /// The transactional account grain definition for use with FSharpTransactionalGrain.
    /// </summary>
    let transactionalAccount : TransactionalGrainDefinition<AccountBalance> =
        {
            Deposit = deposit
            Withdraw = withdrawAtBoundary
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
