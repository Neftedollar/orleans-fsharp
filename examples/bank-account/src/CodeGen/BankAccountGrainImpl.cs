using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.EventSourcing;
using Orleans.Providers;
using BankAccount.Domain;
using Orleans.FSharp.EventSourcing;

namespace BankAccount.CodeGen;

/// <summary>
/// Concrete grain implementation for the event-sourced bank account grain.
/// Inherits from JournaledGrain so Orleans source generators can produce the
/// necessary grain metadata. Delegates apply/handle logic to the F# definition.
/// </summary>
[LogConsistencyProvider(ProviderName = "LogStorage")]
public class BankAccountGrainImpl :
    JournaledGrain<AccountState, AccountEvent>,
    IBankAccountGrain
{
    private readonly ILogger<BankAccountGrainImpl> _logger;

    /// <summary>Creates a new BankAccountGrainImpl instance.</summary>
    public BankAccountGrainImpl(ILogger<BankAccountGrainImpl> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Overrides JournaledGrain transition to delegate event application
    /// to the F# pure apply function.
    /// </summary>
    protected override void TransitionState(AccountState state, AccountEvent @event)
    {
        var definition = AccountGrainDef.account;
        var newState = EventStore.applyEvent(definition, state, @event);
        state.Balance = newState.Balance;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "BankAccountGrainImpl {GrainId} activated with balance {Balance}",
            this.GetGrainId(), State.Balance);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles a bank account command by generating events via the F# handler,
    /// raising them on the JournaledGrain, and confirming persistence.
    /// </summary>
    public async Task<object> HandleCommand(AccountCommand cmd)
    {
        var definition = AccountGrainDef.account;
        var events = EventStore.processCommand(definition, State, cmd);

        foreach (var evt in events)
        {
            RaiseEvent(evt);
        }

        if (!events.IsEmpty)
        {
            await ConfirmEvents();
        }

        _logger.LogInformation(
            "BankAccountGrainImpl {GrainId} handled {Command}, balance = {Balance}",
            this.GetGrainId(), cmd, State.Balance);

        return State.Balance;
    }
}
