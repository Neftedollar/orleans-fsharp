using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;
using BankTransactions.Domain;

namespace BankTransactions.CodeGen;

/// <summary>
/// Concrete grain implementation for the transactional bank account.
/// Uses ITransactionalState for ACID guarantees -- all reads and writes
/// within a transaction are atomic, consistent, isolated, and durable.
/// Delegates pure business logic to F# AccountGrainDef functions.
/// </summary>
[Reentrant]
public class TransactionalAccountGrainImpl : Grain, ITransactionalAccountGrain
{
    private readonly ITransactionalState<AccountBalance> _balance;
    private readonly ILogger<TransactionalAccountGrainImpl> _logger;

    /// <summary>Creates a new TransactionalAccountGrainImpl instance.</summary>
    public TransactionalAccountGrainImpl(
        [TransactionalState(nameof(_balance), "TransactionStore")]
        ITransactionalState<AccountBalance> balance,
        ILogger<TransactionalAccountGrainImpl> logger)
    {
        _balance = balance;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "TransactionalAccountGrainImpl {GrainId} activated",
            this.GetGrainId());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deposit funds into the account within a transaction.
    /// Uses [Transaction(TransactionOption.Join)] semantics via the interface.
    /// </summary>
    public Task Deposit(decimal amount) =>
        _balance.PerformUpdate(balance =>
        {
            var result = AccountGrainDef.deposit(balance, amount);
            balance.Balance = result.Balance;
        });

    /// <summary>
    /// Withdraw funds from the account within a transaction.
    /// Throws InvalidOperationException on overdraft, which aborts the entire transaction.
    /// </summary>
    public Task Withdraw(decimal amount) =>
        _balance.PerformUpdate(balance =>
        {
            var result = AccountGrainDef.withdraw(balance, amount);
            balance.Balance = result.Balance;
        });

    /// <summary>
    /// Read the current balance within a transaction.
    /// </summary>
    public Task<decimal> GetBalance() =>
        _balance.PerformRead(balance => balance.Balance);
}
