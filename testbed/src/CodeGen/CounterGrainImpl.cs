using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Testbed.Shared;
using Orleans.FSharp;

namespace Testbed.CodeGen;

/// <summary>
/// Concrete grain implementation for the counter grain.
/// Delegates all behavior to the F# GrainDefinition registered in DI.
/// </summary>
public class CounterGrainImpl : Grain, ICounterGrain
{
    private readonly GrainDefinition<CounterState, CounterCommand> _definition;
    private readonly IPersistentState<CounterState> _persistentState;
    private readonly ILogger<CounterGrainImpl> _logger;
    private CounterState _currentState;

    public CounterGrainImpl(
        GrainDefinition<CounterState, CounterCommand> definition,
        [PersistentState("state", "Default")] IPersistentState<CounterState> persistentState,
        ILogger<CounterGrainImpl> logger)
    {
        _definition = definition;
        _persistentState = persistentState;
        _logger = logger;
        _currentState = definition.DefaultState.Value;
    }

    public override Task OnActivateAsync(CancellationToken ct)
    {
        if (_persistentState.RecordExists)
            _currentState = _persistentState.State;

        _logger.LogInformation("CounterGrainImpl {GrainId} activated", this.GetGrainId());
        return Task.CompletedTask;
    }

    public async Task<object> HandleMessage(CounterCommand cmd)
    {
        var tuple = await GrainDefinition.invokeHandler(_definition, _currentState, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _currentState = newState;
        _persistentState.State = _currentState;
        await _persistentState.WriteStateAsync();
        return result;
    }
}
