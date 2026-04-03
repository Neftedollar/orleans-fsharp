using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.FSharp.Sample;
using Orleans.FSharp;

namespace Orleans.FSharp.CodeGen;

/// <summary>
/// Concrete grain implementation for the order grain.
/// This C# class exists in the CodeGen project so Orleans source generators
/// can produce the necessary grain metadata (grain type mapping, activation, etc.)
/// that is not possible in F# projects.
/// All behavior is delegated to the F# GrainDefinition via OrderGrainDef.order.
/// </summary>
public class OrderGrainImpl : Grain, IOrderGrain
{
    private readonly IPersistentState<OrderStatusHolder> _persistentState;
    private readonly ILogger<OrderGrainImpl> _logger;
    private OrderStatus _currentState;

    public OrderGrainImpl(
        [PersistentState("state", "Default")] IPersistentState<OrderStatusHolder> persistentState,
        ILogger<OrderGrainImpl> logger)
    {
        _persistentState = persistentState;
        _logger = logger;
        _currentState = OrderGrainDef.order.DefaultState.Value;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (_persistentState.RecordExists)
        {
            _currentState = _persistentState.State.State;
        }

        _logger.LogInformation(
            "OrderGrainImpl {GrainId} activated with state {State}",
            this.GetGrainId(),
            _currentState);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(OrderCommand cmd)
    {
        var definition = OrderGrainDef.order;
        var tuple = await GrainDefinition.invokeHandler(definition, _currentState, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _currentState = newState;
        _persistentState.State.State = _currentState;
        await _persistentState.WriteStateAsync();
        return result;
    }
}
