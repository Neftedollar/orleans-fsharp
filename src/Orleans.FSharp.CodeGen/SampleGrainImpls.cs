// Backward-compatible concrete grain implementations for the Orleans.FSharp.Sample project.
// These classes exist so Orleans source generators can produce grain metadata for the
// per-grain F# interfaces used in the legacy pattern.
//
// NOTE: In the new universal interface pattern, users reference Orleans.FSharp.Abstractions
// and call grains via IFSharpGrain + FSharpGrainHandle — no per-grain C# stubs needed.
// These stubs are only here to keep the sample project and integration tests working.
//
// Each impl delegates all behavior to its corresponding F# GrainDefinition.

#pragma warning disable CS1591

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.FSharp;
using Orleans.FSharp.Sample;
using Orleans.FSharp.EventSourcing;

namespace Orleans.FSharp.CodeGen;

// ─── Counter ───────────────────────────────────────────────────────────────

public class CounterGrainImpl : Grain, ICounterGrain
{
    private readonly IPersistentState<CounterStateHolder> _state;
    private readonly ILogger<CounterGrainImpl> _logger;
    private CounterState _current;

    public CounterGrainImpl(
        [PersistentState("state", "Default")] IPersistentState<CounterStateHolder> state,
        ILogger<CounterGrainImpl> logger)
    { _state = state; _logger = logger; _current = CounterGrainDef.counter.DefaultState.Value; }

    public override Task OnActivateAsync(CancellationToken ct)
    {
        if (_state.RecordExists) _current = _state.State.State;
        return Task.CompletedTask;
    }

    public async Task<object> HandleMessage(CounterCommand cmd)
    {
        var (next, result) = await GrainDefinition.invokeHandler(CounterGrainDef.counter, _current, cmd);
        _current = next; _state.State.State = next; await _state.WriteStateAsync();
        return result;
    }
}

// ─── Echo ───────────────────────────────────────────────────────────────────

public class EchoGrainImpl : Grain, IEchoGrain
{
    private readonly ILogger<EchoGrainImpl> _logger;
    private string _current = "";

    public EchoGrainImpl(ILogger<EchoGrainImpl> logger) => _logger = logger;

    public override Task OnActivateAsync(CancellationToken ct)
    { _current = this.GetPrimaryKeyString(); return Task.CompletedTask; }

    public async Task<object> HandleMessage(EchoCommand cmd)
    {
        var (next, result) = await GrainDefinition.invokeHandler(EchoGrainDef.echo, _current, cmd);
        _current = next; return result;
    }
}

// ─── Order ──────────────────────────────────────────────────────────────────

public class OrderGrainImpl : Grain, IOrderGrain
{
    private readonly IPersistentState<OrderStatusHolder> _state;
    private readonly ILogger<OrderGrainImpl> _logger;
    private OrderStatus _current;

    public OrderGrainImpl(
        [PersistentState("state", "Default")] IPersistentState<OrderStatusHolder> state,
        ILogger<OrderGrainImpl> logger)
    { _state = state; _logger = logger; _current = OrderGrainDef.order.DefaultState.Value; }

    public override Task OnActivateAsync(CancellationToken ct)
    { if (_state.RecordExists) _current = _state.State.State; return Task.CompletedTask; }

    public async Task<object> HandleMessage(OrderCommand cmd)
    {
        var (next, result) = await GrainDefinition.invokeHandler(OrderGrainDef.order, _current, cmd);
        _current = next; _state.State.State = next; await _state.WriteStateAsync();
        return result;
    }
}

// ─── BankAccount (event-sourced) ─────────────────────────────────────────────

[LogConsistencyProvider(ProviderName = "LogStorage")]
public class BankAccountGrainImpl : JournaledGrain<BankAccountState, BankAccountEvent>, IBankAccountGrain
{
    private readonly ILogger<BankAccountGrainImpl> _logger;

    public BankAccountGrainImpl(ILogger<BankAccountGrainImpl> logger) => _logger = logger;

    protected override void TransitionState(BankAccountState state, BankAccountEvent @event)
    {
        var next = EventStore.applyEvent(BankAccountGrainDef.bankAccount, state, @event);
        state.Balance = next.Balance;
    }

    public async Task<object> HandleCommand(BankAccountCommand cmd)
    {
        var events = EventStore.processCommand(BankAccountGrainDef.bankAccount, State, cmd);
        foreach (var e in events) RaiseEvent(e);
        await ConfirmEvents();
        return (object)State.Balance;
    }
}

// ─── Processor (stateless worker) ───────────────────────────────────────────

[StatelessWorker(4)]
public class ProcessorGrainImpl : Grain, IProcessorGrain
{
    private readonly ILogger<ProcessorGrainImpl> _logger;
    private string _current = Guid.NewGuid().ToString();

    public ProcessorGrainImpl(ILogger<ProcessorGrainImpl> logger) => _logger = logger;

    public async Task<object> HandleMessage(ProcessorCommand cmd)
    {
        var (next, result) = await GrainDefinition.invokeHandler(ProcessorGrainDef.processor, _current, cmd);
        _current = next; return result;
    }
}

// ─── ReminderTest ───────────────────────────────────────────────────────────

public class ReminderTestGrainImpl : Grain, IReminderTestGrain, IRemindable
{
    private readonly IPersistentState<ReminderStateHolder> _state;
    private readonly ILogger<ReminderTestGrainImpl> _logger;
    private int _current;

    public ReminderTestGrainImpl(
        [PersistentState("state", "Default")] IPersistentState<ReminderStateHolder> state,
        ILogger<ReminderTestGrainImpl> logger)
    { _state = state; _logger = logger; _current = ReminderTestGrainDef.reminderTestGrain.DefaultState.Value; }

    public override Task OnActivateAsync(CancellationToken ct)
    { if (_state.RecordExists) _current = _state.State.ReminderFireCount; return Task.CompletedTask; }

    public async Task<object> HandleMessage(ReminderCommand cmd)
    {
        if (cmd.IsGetFireCount)
            return (object)_current;
        if (cmd is ReminderCommand.RegisterReminder reg)
        {
            await this.RegisterOrUpdateReminder(reg.name, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            return (object)true;
        }
        if (cmd is ReminderCommand.UnregisterReminder unreg)
        {
            try { var r = await this.GetReminder(unreg.name); if (r != null) await this.UnregisterReminder(r); }
            catch { /* reminder may not exist */ }
            return (object)true;
        }
        return (object)_current;
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        _current = await GrainDefinition.invokeReminderHandler(ReminderTestGrainDef.reminderTestGrain, _current, reminderName, status);
        _state.State.ReminderFireCount = _current;
        await _state.WriteStateAsync();
    }
}

// ─── TimerTest ──────────────────────────────────────────────────────────────

public class TimerTestGrainImpl : Grain, ITimerTestGrain
{
    private readonly ILogger<TimerTestGrainImpl> _logger;
    private int _current;

    public TimerTestGrainImpl(ILogger<TimerTestGrainImpl> logger)
    { _logger = logger; _current = TimerTestGrainDef.timerTestGrain.DefaultState.Value; }

    public override Task OnActivateAsync(CancellationToken ct)
    {
        foreach (var kvp in TimerTestGrainDef.timerTestGrain.TimerHandlers)
        {
            var (dueTime, period, handler) = kvp.Value;
            this.RegisterGrainTimer(
                async (object? _, CancellationToken _) => { _current = await handler.Invoke(_current); },
                null,
                new GrainTimerCreationOptions { DueTime = dueTime, Period = period });
        }
        return Task.CompletedTask;
    }

    public async Task<object> HandleMessage(TimerCommand cmd)
    {
        var (next, result) = await GrainDefinition.invokeHandler(TimerTestGrainDef.timerTestGrain, _current, cmd);
        _current = next; return result;
    }
}

// ─── Sequential ─────────────────────────────────────────────────────────────

public class SequentialGrainImpl : Grain, ISequentialGrain
{
    private readonly ILogger<SequentialGrainImpl> _logger;
    private int _current;

    public SequentialGrainImpl(ILogger<SequentialGrainImpl> logger)
    { _logger = logger; _current = SequentialGrainDef.sequential.DefaultState.Value; }

    public async Task<object> HandleMessage(SequentialCommand cmd)
    {
        var (next, result) = await GrainDefinition.invokeHandler(SequentialGrainDef.sequential, _current, cmd);
        _current = next; return result;
    }
}

// ─── Aggregator ─────────────────────────────────────────────────────────────

[Reentrant]
public class AggregatorGrainImpl : Grain, IAggregatorGrain
{
    private readonly ILogger<AggregatorGrainImpl> _logger;
    private int _current;

    public AggregatorGrainImpl(ILogger<AggregatorGrainImpl> logger)
    { _logger = logger; _current = AggregatorGrainDef.aggregator.DefaultState.Value; }

    public async Task<object> HandleMessage(AggregatorCommand cmd)
    {
        var (next, result) = await GrainDefinition.invokeHandler(AggregatorGrainDef.aggregator, _current, cmd);
        _current = next; return result;
    }
}

#pragma warning restore CS1591
