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
/// Concrete grain implementation for the declarative timer test grain.
/// Registers timers declared via the onTimer CE keyword during OnActivateAsync.
/// All behavior is delegated to the F# GrainDefinition via TimerTestGrainDef.timerTestGrain.
/// </summary>
public class TimerTestGrainImpl : Grain, ITimerTestGrain
{
    private readonly ILogger<TimerTestGrainImpl> _logger;
    private int _currentState;

    public TimerTestGrainImpl(ILogger<TimerTestGrainImpl> logger)
    {
        _logger = logger;
        _currentState = TimerTestGrainDef.timerTestGrain.DefaultState.Value;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "TimerTestGrainImpl {GrainId} activating with {TimerCount} declarative timers",
            this.GetGrainId(), TimerTestGrainDef.timerTestGrain.TimerHandlers.Count);

        // Register all declarative timers from the grain definition
        foreach (var kvp in TimerTestGrainDef.timerTestGrain.TimerHandlers)
        {
            var timerName = kvp.Key;
            var (dueTime, period, handler) = kvp.Value;

            this.RegisterGrainTimer(
                async (object? _, CancellationToken ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var newState = await handler.Invoke(_currentState);
                    _currentState = newState;
                },
                null,
                new GrainTimerCreationOptions
                {
                    DueTime = dueTime,
                    Period = period
                });
        }

        _logger.LogInformation(
            "TimerTestGrainImpl {GrainId} activated with state {State}",
            this.GetGrainId(), _currentState);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(TimerCommand cmd)
    {
        var definition = TimerTestGrainDef.timerTestGrain;
        var tuple = await GrainDefinition.invokeHandler(definition, _currentState, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _currentState = newState;
        return result;
    }
}
