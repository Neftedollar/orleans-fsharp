using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.FSharp.Sample;
using Orleans.FSharp;

namespace Orleans.FSharp.CodeGen;

/// <summary>
/// Concrete grain implementation for the counter grain.
/// This C# class exists in the CodeGen project so Orleans source generators
/// can produce the necessary grain metadata (grain type mapping, activation, etc.)
/// that is not possible in F# projects.
/// All behavior is delegated to the F# GrainDefinition via CounterGrainDef.counter.
/// </summary>
public class CounterGrainImpl : Grain, ICounterGrain
{
    private readonly IPersistentState<CounterStateHolder> _persistentState;
    private readonly ILogger<CounterGrainImpl> _logger;
    private CounterState _currentState;

    public CounterGrainImpl(
        [PersistentState("state", "Default")] IPersistentState<CounterStateHolder> persistentState,
        ILogger<CounterGrainImpl> logger)
    {
        _persistentState = persistentState;
        _logger = logger;
        _currentState = CounterGrainDef.counter.DefaultState;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (_persistentState.RecordExists)
        {
            _currentState = _persistentState.State.State;
        }

        _logger.LogInformation(
            "CounterGrainImpl {GrainId} activated",
            this.GetGrainId());

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(CounterCommand cmd)
    {
        var definition = CounterGrainDef.counter;
        var tuple = await GrainDefinition.invokeHandler(definition, _currentState, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _currentState = newState;
        _persistentState.State.State = _currentState;
        await _persistentState.WriteStateAsync();
        return result;
    }
}
