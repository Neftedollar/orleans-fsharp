using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.FSharp.Sample;
using Orleans.FSharp;

namespace Orleans.FSharp.CodeGen;

/// <summary>
/// Concrete grain implementation for the reminder test grain.
/// Implements IRemindable so Orleans can deliver reminder ticks.
/// Delegates reminder handling to the F# GrainDefinition's ReminderHandlers.
/// </summary>
public class ReminderTestGrainImpl : Grain, IReminderTestGrain, IRemindable
{
    private readonly IPersistentState<ReminderStateHolder> _persistentState;
    private readonly ILogger<ReminderTestGrainImpl> _logger;
    private int _currentState;

    public ReminderTestGrainImpl(
        [PersistentState("state", "Default")] IPersistentState<ReminderStateHolder> persistentState,
        ILogger<ReminderTestGrainImpl> logger)
    {
        _persistentState = persistentState;
        _logger = logger;
        _currentState = ReminderTestGrainDef.reminderTestGrain.DefaultState.Value;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (_persistentState.RecordExists)
        {
            _currentState = _persistentState.State.ReminderFireCount;
        }

        _logger.LogInformation(
            "ReminderTestGrainImpl {GrainId} activated with state {State}",
            this.GetGrainId(), _currentState);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(ReminderCommand cmd)
    {
        if (cmd.IsGetFireCount)
        {
            return _currentState;
        }
        else if (cmd.IsRegisterReminder)
        {
            var registerCmd = (ReminderCommand.RegisterReminder)cmd;
            var name = registerCmd.name;
            await this.RegisterOrUpdateReminder(
                name,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2));
            return true;
        }
        else if (cmd.IsUnregisterReminder)
        {
            var unregisterCmd = (ReminderCommand.UnregisterReminder)cmd;
            var reminderName = unregisterCmd.name;
            try
            {
                var reminder = await this.GetReminder(reminderName);
                if (reminder != null)
                {
                    await this.UnregisterReminder(reminder);
                }
            }
            catch (Exception)
            {
                // Reminder might not exist
            }
            return true;
        }
        else
        {
            throw new InvalidOperationException($"Unknown command: {cmd}");
        }
    }

    /// <inheritdoc/>
    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        _logger.LogInformation(
            "ReminderTestGrainImpl {GrainId} received reminder {ReminderName}",
            this.GetGrainId(), reminderName);

        var handlers = ReminderTestGrainDef.reminderTestGrain.ReminderHandlers;
        var handlerOpt = Microsoft.FSharp.Collections.MapModule.TryFind(reminderName, handlers);

        if (Microsoft.FSharp.Core.OptionModule.IsSome(handlerOpt))
        {
            var handler = handlerOpt.Value;
            var newState = await handler.Invoke(_currentState).Invoke(reminderName).Invoke(status);
            _currentState = newState;
            _persistentState.State.ReminderFireCount = _currentState;
            await _persistentState.WriteStateAsync();
        }
        else
        {
            _logger.LogWarning(
                "No handler registered for reminder {ReminderName} on grain {GrainId}",
                reminderName, this.GetGrainId());
        }
    }
}
