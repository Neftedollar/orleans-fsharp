using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using BankTransactions.Domain;

namespace BankTransactions.CodeGen;

/// <summary>
/// ATM grain that orchestrates atomic cross-grain transfers.
/// The Transfer method creates a new transaction context, then calls
/// Withdraw and Deposit on two different account grains. Both operations
/// execute within the same transaction -- if either fails, both are rolled back.
/// </summary>
public class AtmGrainImpl : Grain, IAtmGrain
{
    private readonly ILogger<AtmGrainImpl> _logger;

    /// <summary>Creates a new AtmGrainImpl instance.</summary>
    public AtmGrainImpl(ILogger<AtmGrainImpl> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Atomically transfers funds from one account to another.
    /// Both the withdrawal and deposit happen within a single Orleans transaction.
    /// If the withdrawal fails (e.g., insufficient funds), the deposit is also rolled back.
    /// </summary>
    [Transaction(TransactionOption.Create)]
    public async Task Transfer(string fromAccount, string toAccount, decimal amount)
    {
        _logger.LogInformation(
            "ATM: Transferring {Amount} from {From} to {To}",
            amount, fromAccount, toAccount);

        var from = GrainFactory.GetGrain<ITransactionalAccountGrain>(fromAccount);
        var to = GrainFactory.GetGrain<ITransactionalAccountGrain>(toAccount);

        await from.Withdraw(amount);
        await to.Deposit(amount);

        _logger.LogInformation(
            "ATM: Transfer of {Amount} from {From} to {To} completed",
            amount, fromAccount, toAccount);
    }
}
