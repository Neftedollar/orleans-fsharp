using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.FSharp.Sample;
using Orleans.FSharp.EventSourcing;

namespace Orleans.FSharp.CodeGen;

/// <summary>
/// Concrete grain implementation for the event-sourced bank account grain.
/// This C# class inherits from JournaledGrain so that Orleans source generators
/// can produce the necessary grain metadata. The Apply/TransitionState logic
/// delegates to the F# EventSourcedGrainDefinition via BankAccountGrainDef.bankAccount.
/// </summary>
[LogConsistencyProvider(ProviderName = "LogStorage")]
public class BankAccountGrainImpl :
    JournaledGrain<BankAccountState, BankAccountEvent>,
    IBankAccountGrain
{
    private readonly ILogger<BankAccountGrainImpl> _logger;

    /// <summary>
    /// Creates a new instance of BankAccountGrainImpl.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public BankAccountGrainImpl(ILogger<BankAccountGrainImpl> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Overrides the JournaledGrain transition method to delegate event application
    /// to the F# pure apply function defined in BankAccountGrainDef.
    /// This is called by the Orleans runtime whenever an event needs to be applied to state.
    /// </summary>
    /// <param name="state">The current grain state.</param>
    /// <param name="event">The event to apply.</param>
    protected override void TransitionState(BankAccountState state, BankAccountEvent @event)
    {
        var definition = BankAccountGrainDef.bankAccount;
        var newState = EventStore.applyEvent(definition, state, @event);
        // Copy the new state values back into the existing state object
        // because JournaledGrain manages the state instance.
        state.Balance = newState.Balance;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "BankAccountGrainImpl {GrainId} activated with balance {Balance}",
            this.GetGrainId(),
            State.Balance);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles a bank account command by generating events via the F# command handler,
    /// raising them on the JournaledGrain, and confirming persistence.
    /// </summary>
    /// <param name="cmd">The bank account command to handle.</param>
    /// <returns>A boxed result (typically the new balance).</returns>
    public async Task<object> HandleCommand(BankAccountCommand cmd)
    {
        var definition = BankAccountGrainDef.bankAccount;
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
            "BankAccountGrainImpl {GrainId} handled {Command}, balance is {Balance}",
            this.GetGrainId(),
            cmd,
            State.Balance);

        return State.Balance;
    }
}
