using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using OrderProcessing.Domain;
using Orleans.FSharp;

namespace OrderProcessing.CodeGen;

/// <summary>
/// Concrete grain implementation for the order processing grain.
/// Implements IRemindable for reminder-based timeout checks.
/// Delegates all behavior to the F# GrainDefinition via OrderGrainDef.order.
/// </summary>
public class OrderGrainImpl : Grain, IOrderGrain, IRemindable
{
    private readonly ILogger<OrderGrainImpl> _logger;
    private OrderState _currentState;

    /// <summary>Creates a new OrderGrainImpl instance.</summary>
    public OrderGrainImpl(ILogger<OrderGrainImpl> logger)
    {
        _logger = logger;
        _currentState = OrderGrainDef.order.DefaultState.Value;
    }

    /// <inheritdoc/>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OrderGrainImpl {GrainId} activated", this.GetGrainId());

        // Register the OrderTimeout reminder (fires every 60s after a 10s delay)
        await this.RegisterOrUpdateReminder(
            "OrderTimeout",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(60));
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(OrderCommand cmd)
    {
        var definition = OrderGrainDef.order;
        var tuple = await GrainDefinition.invokeHandler(definition, _currentState, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _currentState = newState;
        return result;
    }

    /// <inheritdoc/>
    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        _logger.LogInformation(
            "OrderGrainImpl {GrainId} received reminder {ReminderName}",
            this.GetGrainId(), reminderName);

        var handlers = OrderGrainDef.order.ReminderHandlers;
        var handlerOpt = Microsoft.FSharp.Collections.MapModule.TryFind(reminderName, handlers);

        if (Microsoft.FSharp.Core.OptionModule.IsSome(handlerOpt))
        {
            var handler = handlerOpt.Value;
            var newState = await handler.Invoke(_currentState).Invoke(reminderName).Invoke(status);
            _currentState = newState;
        }
        else
        {
            _logger.LogWarning(
                "No handler registered for reminder {ReminderName} on grain {GrainId}",
                reminderName, this.GetGrainId());
        }
    }
}
