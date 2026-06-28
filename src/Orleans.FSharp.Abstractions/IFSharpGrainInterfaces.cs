using Orleans;
using Orleans.Concurrency;
using Orleans.EventSourcing;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.FSharp;

/// <summary>
/// Process-wide registry of message types that may interleave with an in-progress request
/// on a universal F# grain activation.
/// </summary>
/// <remarks>
/// <para>
/// The universal F# grain pattern routes every grain through one of three shared concrete
/// classes (<see cref="FSharpGrainImpl"/> / <see cref="FSharpGrainGuidImpl"/> /
/// <see cref="FSharpGrainIntImpl"/>) via a single <c>HandleMessage(object)</c> method.
/// Orleans' only class-level reentrancy lever that fits this shape is
/// <c>[MayInterleave(predicate)]</c>: a single <b>static</b> predicate consulted with the
/// incoming request. Because the predicate is static it cannot reach a DI-scoped registry,
/// so the set of interleavable message types is held here, in process-static state.
/// </para>
/// <para>
/// This is correct for the universal pattern: the set of interleavable message types is a
/// property of the grain <i>definitions</i> registered into the process, which is identical
/// across every silo in a deployment (including a multi-silo <c>TestCluster</c>). It is
/// populated at silo-configuration time from each grain definition's
/// <c>InterleaveMessageTypes</c> (set by the <c>interleaveMessage</c> grain CE operation).
/// </para>
/// <para>
/// Membership is matched by <b>assignability</b>, not exact type identity: registering a
/// discriminated-union type makes every one of its cases interleavable. This is required
/// because F# DU cases that carry fields compile to nested subtypes that are not nameable
/// in source — callers can only write <c>typeof&lt;TheUnion&gt;</c>, while a boxed DU value
/// reports the nested case type at runtime.
/// </para>
/// </remarks>
public static class FSharpInterleaveRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, byte> _types = new();

    /// <summary>
    /// Records <paramref name="messageType"/> as interleavable. Idempotent and thread-safe.
    /// </summary>
    /// <param name="messageType">The message (or discriminated-union) type to mark interleavable.</param>
    public static void Register(Type messageType)
    {
        if (messageType is not null)
        {
            _types.TryAdd(messageType, 0);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="messageType"/> is itself registered, or is
    /// assignable to any registered type (so cases of a registered DU interleave).
    /// </summary>
    /// <param name="messageType">The runtime type of the incoming message.</param>
    public static bool MayInterleave(Type messageType)
    {
        if (messageType is null)
        {
            return false;
        }

        if (_types.ContainsKey(messageType))
        {
            return true;
        }

        foreach (var registered in _types.Keys)
        {
            if (registered.IsAssignableFrom(messageType))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Universal grain interface for all F# grains with a string key.
/// Every F# grain (regardless of state/message types) implements this single interface.
/// Orleans generates one proxy class for this interface that works for all F# grains —
/// no per-grain C# stubs needed.
/// </summary>
/// <remarks>
/// This interface is defined in C# (Orleans.FSharp.Abstractions) so Orleans' Roslyn source
/// generators can see it and generate the required proxy. F# assemblies are not supported
/// by Orleans source generators.
/// <para>
/// Reminder support (IRemindable) is implemented by the concrete grain class in
/// Orleans.FSharp.Runtime, not declared here, because including Microsoft.Orleans.Reminders
/// would pull in source generators that conflict with proxy generation in CodeGen.
/// </para>
/// </remarks>
public interface IFSharpGrain : IGrainWithStringKey
{
    /// <summary>
    /// Dispatches a message to the grain. The message is deserialized and routed
    /// to the appropriate F# handler based on its runtime type.
    /// </summary>
    /// <param name="message">The command or query object. Must be serializable.</param>
    /// <returns>The result produced by the grain handler, boxed as <c>object</c>.</returns>
    Task<object> HandleMessage(object message);

    /// <summary>
    /// Dispatches a message to the grain as a true one-way (fire-and-forget) call.
    /// The caller returns once the message is sent; no response is marshalled back and
    /// grain-side exceptions are not propagated to the caller. The grain still processes
    /// the message and mutates its state. Backs <c>FSharpGrain.post</c>.
    /// </summary>
    /// <param name="message">The command object. Must be serializable.</param>
    [OneWay]
    Task HandleMessageOneWay(object message);
}

/// <summary>
/// Universal grain interface for all F# grains with a <see cref="Guid"/> key.
/// </summary>
public interface IFSharpGrainWithGuidKey : IGrainWithGuidKey
{
    /// <summary>
    /// Dispatches a message to the grain.
    /// </summary>
    /// <param name="message">The command or query object. Must be serializable.</param>
    /// <returns>The result produced by the grain handler, boxed as <c>object</c>.</returns>
    Task<object> HandleMessage(object message);

    /// <summary>
    /// Dispatches a message to the grain as a true one-way (fire-and-forget) call.
    /// The caller returns once the message is sent; no response is marshalled back and
    /// grain-side exceptions are not propagated to the caller. The grain still processes
    /// the message and mutates its state. Backs <c>FSharpGrain.postGuid</c>.
    /// </summary>
    /// <param name="message">The command object. Must be serializable.</param>
    [OneWay]
    Task HandleMessageOneWay(object message);
}

/// <summary>
/// Universal grain interface for all F# grains with an integer key.
/// </summary>
public interface IFSharpGrainWithIntKey : IGrainWithIntegerKey
{
    /// <summary>
    /// Dispatches a message to the grain.
    /// </summary>
    /// <param name="message">The command or query object. Must be serializable.</param>
    /// <returns>The result produced by the grain handler, boxed as <c>object</c>.</returns>
    Task<object> HandleMessage(object message);

    /// <summary>
    /// Dispatches a message to the grain as a true one-way (fire-and-forget) call.
    /// The caller returns once the message is sent; no response is marshalled back and
    /// grain-side exceptions are not propagated to the caller. The grain still processes
    /// the message and mutates its state. Backs <c>FSharpGrain.postInt</c>.
    /// </summary>
    /// <param name="message">The command object. Must be serializable.</param>
    [OneWay]
    Task HandleMessageOneWay(object message);
}

/// <summary>
/// Universal grain interface for F# event-sourced grains with a string key.
/// Backed by <c>Orleans.FSharp.EventSourcing.FSharpEventSourcedGrain&lt;TState, TEvent, TCommand&gt;</c>.
/// No per-grain C# stub needed — register via <c>AddFSharpEventSourcedGrain</c>.
/// </summary>
public interface IFSharpEventSourcedGrain : IGrainWithStringKey
{
    /// <summary>
    /// Dispatches a command to the event-sourced grain. The command is handled
    /// by the F# EventSourcedGrainDefinition's Handle function, producing events
    /// that are raised on the JournaledGrain and confirmed.
    /// </summary>
    /// <param name="command">The command object. Must be serializable.</param>
    /// <returns>The grain state after all events are applied, boxed as <c>object</c>.</returns>
    Task<object> HandleCommand(object command);
}

/// <summary>
/// Encapsulates the result of dispatching a message through the universal grain handler.
/// Contains both the updated grain state and the value to return to the caller.
/// </summary>
public sealed class GrainDispatchResult
{
    /// <summary>Initialises a new dispatch result with the given state and return value.</summary>
    /// <param name="newState">The updated grain state after handling the message.</param>
    /// <param name="result">The value to return to the grain caller.</param>
    public GrainDispatchResult(object? newState, object? result)
    {
        NewState = newState;
        Result = result;
    }

    /// <summary>Gets the updated grain state after handling the message.</summary>
    public object? NewState { get; }

    /// <summary>Gets the value returned to the grain caller (may be the state or any other value).</summary>
    public object? Result { get; }
}

/// <summary>
/// Dispatcher that routes messages to registered F# grain handlers.
/// Implemented by <c>Orleans.FSharp.Runtime.UniversalGrainHandlerRegistry</c>;
/// registered as a singleton in the silo DI container via <c>AddFSharpGrain</c>.
/// </summary>
/// <remarks>
/// Keyed on the message's runtime type. Each <c>AddFSharpGrain&lt;State, Message&gt;</c> call
/// registers a handler entry for <c>typeof(Message).FullName</c>.
/// </remarks>
public interface IUniversalGrainHandler
{
    /// <summary>
    /// Returns the boxed default state for grains that handle the given message type,
    /// or <c>null</c> if no handler is registered for that type.
    /// </summary>
    /// <param name="messageType">The runtime type of the incoming message.</param>
    object? GetDefaultState(Type messageType);

    /// <summary>
    /// Dispatches a message to the registered F# handler and returns the updated state
    /// together with the value to return to the caller.
    /// </summary>
    /// <param name="currentState">
    /// The current in-memory state, or <c>null</c> on the first call (before
    /// <see cref="GetDefaultState"/> has been applied).
    /// </param>
    /// <param name="message">The boxed command or query to handle.</param>
    /// <param name="serviceProvider">
    /// The silo-scoped DI service provider for the calling grain instance.
    /// Passed to <c>handleWithContext</c> / <c>handleStateWithContext</c> / <c>handleTypedWithContext</c>
    /// handlers so they can resolve DI services inside grain logic.
    /// </param>
    /// <param name="grainFactory">
    /// The grain factory for the calling grain instance. Passed to context-aware handlers
    /// so they can make grain-to-grain calls.
    /// </param>
    /// <param name="grainBase">
    /// The grain instance that is handling the message. Used to wire
    /// <c>DeactivateOnIdle</c> and <c>DelayDeactivation</c> into the
    /// <c>GrainContext</c> so that context-aware handlers can request deactivation.
    /// Pass <c>null</c> in unit-test contexts where no live grain instance exists.
    /// </param>
    Task<GrainDispatchResult> Handle(
        object? currentState,
        object message,
        IServiceProvider serviceProvider,
        IGrainFactory grainFactory,
        IGrainBase? grainBase);
}

/// <summary>
/// Universal concrete grain class for all F# grains using the <c>FSharpGrain.ref</c> pattern
/// (string key variant).
/// Orleans source generators produce the <c>Proxy_IFSharpGrain</c> proxy from this assembly
/// because this concrete class implements <see cref="IFSharpGrain"/>.
/// </summary>
/// <remarks>
/// State is kept purely in memory (no <c>IPersistentState</c> injection).
/// For grains that require durable persistence across silo restarts, use the per-grain
/// CodeGen pattern with the typed <c>FSharpGrain&lt;State, Message&gt;</c> class in
/// <c>Orleans.FSharp.Runtime</c> instead.
/// </remarks>
[MayInterleave(nameof(MayInterleavePredicate))]
public sealed class FSharpGrainImpl : Grain, IFSharpGrain
{
    private readonly IUniversalGrainHandler _handler;
    private object? _currentState;

    /// <summary>
    /// Initialises the grain with the universal message dispatcher resolved from silo DI.
    /// </summary>
    /// <param name="handler">The universal dispatcher registered via <c>AddFSharpGrain</c>.</param>
    public FSharpGrainImpl(IUniversalGrainHandler handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// Class-level reentrancy predicate consulted by the Orleans runtime when a request
    /// arrives while this activation is already processing another request. Returns
    /// <c>true</c> when the incoming message's runtime type was registered as interleavable
    /// via the <c>interleaveMessage</c> grain CE operation (see <see cref="FSharpInterleaveRegistry"/>).
    /// </summary>
    /// <param name="req">The incoming invocation; argument 0 is the boxed F# message.</param>
    public static bool MayInterleavePredicate(IInvokable req) =>
        req.GetArgumentCount() > 0
        && req.GetArgument(0) is { } message
        && FSharpInterleaveRegistry.MayInterleave(message.GetType());

    /// <inheritdoc/>
    public async Task<object> HandleMessage(object message)
    {
        _currentState ??= _handler.GetDefaultState(message.GetType());
        var dispatch = await _handler.Handle(_currentState, message, ServiceProvider, GrainFactory, this);
        _currentState = dispatch.NewState;
        return dispatch.Result ?? (object)Unit.Default;
    }

    /// <inheritdoc/>
    public async Task HandleMessageOneWay(object message)
    {
        _currentState ??= _handler.GetDefaultState(message.GetType());
        var dispatch = await _handler.Handle(_currentState, message, ServiceProvider, GrainFactory, this);
        _currentState = dispatch.NewState;
    }

    private readonly struct Unit { public static readonly Unit Default = default; }
}

/// <summary>
/// Universal concrete grain class for all F# grains using the <c>FSharpGrain.refGuid</c> pattern
/// (GUID key variant).
/// </summary>
/// <remarks>
/// State is kept purely in memory. For durable persistence use the typed
/// <c>FSharpGrain&lt;State, Message&gt;</c> pattern in Orleans.FSharp.Runtime.
/// </remarks>
[MayInterleave(nameof(MayInterleavePredicate))]
public sealed class FSharpGrainGuidImpl : Grain, IFSharpGrainWithGuidKey
{
    private readonly IUniversalGrainHandler _handler;
    private object? _currentState;

    /// <summary>
    /// Initialises the grain with the universal message dispatcher resolved from silo DI.
    /// </summary>
    /// <param name="handler">The universal dispatcher registered via <c>AddFSharpGrain</c>.</param>
    public FSharpGrainGuidImpl(IUniversalGrainHandler handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// Class-level reentrancy predicate consulted by the Orleans runtime when a request
    /// arrives while this activation is already processing another request. Returns
    /// <c>true</c> when the incoming message's runtime type was registered as interleavable
    /// via the <c>interleaveMessage</c> grain CE operation (see <see cref="FSharpInterleaveRegistry"/>).
    /// </summary>
    /// <param name="req">The incoming invocation; argument 0 is the boxed F# message.</param>
    public static bool MayInterleavePredicate(IInvokable req) =>
        req.GetArgumentCount() > 0
        && req.GetArgument(0) is { } message
        && FSharpInterleaveRegistry.MayInterleave(message.GetType());

    /// <inheritdoc/>
    public async Task<object> HandleMessage(object message)
    {
        _currentState ??= _handler.GetDefaultState(message.GetType());
        var dispatch = await _handler.Handle(_currentState, message, ServiceProvider, GrainFactory, this);
        _currentState = dispatch.NewState;
        return dispatch.Result ?? (object)Unit.Default;
    }

    /// <inheritdoc/>
    public async Task HandleMessageOneWay(object message)
    {
        _currentState ??= _handler.GetDefaultState(message.GetType());
        var dispatch = await _handler.Handle(_currentState, message, ServiceProvider, GrainFactory, this);
        _currentState = dispatch.NewState;
    }

    private readonly struct Unit { public static readonly Unit Default = default; }
}

/// <summary>
/// Dispatcher that routes commands to registered F# event-sourced grain definitions.
/// Implemented by <c>EventSourcedHandlerRegistry</c> in <c>Orleans.FSharp.EventSourcing</c>;
/// registered as a singleton via <c>AddFSharpEventSourcedGrain</c>.
/// </summary>
public interface IEventSourcedHandlerRegistry
{
    /// <summary>Returns the boxed default state for the grain that handles the given command type.</summary>
    object GetDefaultStateByCommandType(Type commandType);

    /// <summary>Returns the boxed default state for the grain whose events are of the given type.</summary>
    object GetDefaultStateByEventType(Type eventType);

    /// <summary>Dispatches a boxed command to the registered F# handler and returns the list of boxed events produced.</summary>
    IReadOnlyList<object> HandleCommand(object state, object command);

    /// <summary>Applies a boxed event to a boxed state and returns the resulting boxed state.</summary>
    object ApplyEvent(object state, object @event);
}

/// <summary>
/// Wrapper type that carries a boxed grain state value inside a <c>JournaledGrain</c>.
/// Allows <c>FSharpEventSourcedGrainImpl</c> to work with arbitrary F# state types without
/// per-grain generic parameters.
/// </summary>
[GenerateSerializer]
public sealed class WrappedEventSourcedState
{
    /// <summary>The current grain state, boxed. Null only before the first command or event replay.</summary>
    [Id(0)] public object? Value { get; set; }
}

/// <summary>
/// Wrapper type that carries a boxed grain event value inside a <c>JournaledGrain</c>.
/// Allows <c>FSharpEventSourcedGrainImpl</c> to work with arbitrary F# event types without
/// per-grain generic parameters.
/// </summary>
[GenerateSerializer]
public sealed class WrappedEventSourcedEvent
{
    /// <summary>The event, boxed.</summary>
    [Id(0)] public object? Value { get; set; }
}

/// <summary>
/// Universal non-generic C# grain class backing <see cref="IFSharpEventSourcedGrain"/>.
/// Orleans source generators produce <c>Proxy_IFSharpEventSourcedGrain</c> from this assembly.
/// All event-sourcing logic is delegated to the F# <c>EventSourcedHandlerRegistry</c> registered
/// via <c>AddFSharpEventSourcedGrain</c> — no per-grain C# stub needed.
/// </summary>
/// <remarks>
/// To use this pattern, call <c>services.AddFSharpEventSourcedGrain(myDef)</c> in the silo
/// configurator and obtain a grain reference via
/// <c>grainFactory.GetGrain&lt;IFSharpEventSourcedGrain&gt;("key")</c>.
/// <para>
/// Subclasses generated by <c>Orleans.FSharp.Generator</c> may override
/// <see cref="ReadStateFromStorageCore"/> and <see cref="ApplyUpdatesToStorageCore"/>
/// to implement custom storage via <c>CustomStorageBasedLogConsistencyProvider</c>.
/// </para>
/// </remarks>
[GrainType("fsharp-eventsourced")]
public class FSharpEventSourcedGrainImpl
    : JournaledGrain<WrappedEventSourcedState, WrappedEventSourcedEvent>
    , IFSharpEventSourcedGrain
    , ICustomStorageInterface<WrappedEventSourcedState, WrappedEventSourcedEvent>
{
    private readonly IEventSourcedHandlerRegistry _registry;

    /// <summary>Initialises the grain with the event-sourced handler registry.</summary>
    public FSharpEventSourcedGrainImpl(IEventSourcedHandlerRegistry registry)
    {
        _registry = registry;
    }

    /// <inheritdoc/>
    protected override void TransitionState(WrappedEventSourcedState state, WrappedEventSourcedEvent @event)
    {
        var eventValue = @event.Value!;
        // On first event replay after activation, state.Value may still be null.
        var currentValue = state.Value ?? _registry.GetDefaultStateByEventType(eventValue.GetType());
        state.Value = _registry.ApplyEvent(currentValue, eventValue);
    }

    /// <inheritdoc/>
    public async Task<object> HandleCommand(object command)
    {
        State.Value ??= _registry.GetDefaultStateByCommandType(command.GetType());
        var events = _registry.HandleCommand(State.Value!, command);
        foreach (var evt in events)
            RaiseEvent(new WrappedEventSourcedEvent { Value = evt });
        if (events.Count > 0)
            await ConfirmEvents();
        return State.Value!;
    }

    /// <summary>
    /// Override in a generated subclass to read grain state from a custom storage back-end.
    /// Called by <c>CustomStorageBasedLogConsistencyProvider</c> during grain activation.
    /// The default implementation throws <see cref="NotSupportedException"/> — override when
    /// the grain definition declares <c>customStorage</c>.
    /// </summary>
    protected virtual Task<KeyValuePair<int, WrappedEventSourcedState>> ReadStateFromStorageCore() =>
        throw new NotSupportedException(
            "This grain has no customStorage configured. " +
            "Add customStorage callbacks to eventSourcedGrain { } and set logConsistencyProvider \"CustomStorage\".");

    /// <summary>
    /// Override in a generated subclass to write events to a custom storage back-end.
    /// Called by <c>CustomStorageBasedLogConsistencyProvider</c> when confirming events.
    /// The default implementation throws <see cref="NotSupportedException"/>.
    /// </summary>
    protected virtual Task<bool> ApplyUpdatesToStorageCore(
        IReadOnlyList<WrappedEventSourcedEvent> updates, int expectedVersion) =>
        throw new NotSupportedException("This grain has no customStorage configured.");

    Task<KeyValuePair<int, WrappedEventSourcedState>>
    ICustomStorageInterface<WrappedEventSourcedState, WrappedEventSourcedEvent>.ReadStateFromStorage() =>
        ReadStateFromStorageCore();

    Task<bool>
    ICustomStorageInterface<WrappedEventSourcedState, WrappedEventSourcedEvent>.ApplyUpdatesToStorage(
        IReadOnlyList<WrappedEventSourcedEvent> updates, int expectedVersion) =>
        ApplyUpdatesToStorageCore(updates, expectedVersion);
}

/// <summary>
/// Universal concrete grain class for all F# grains using the <c>FSharpGrain.refInt</c> pattern
/// (integer key variant).
/// </summary>
/// <remarks>
/// State is kept purely in memory. For durable persistence use the typed
/// <c>FSharpGrain&lt;State, Message&gt;</c> pattern in Orleans.FSharp.Runtime.
/// </remarks>
[MayInterleave(nameof(MayInterleavePredicate))]
public sealed class FSharpGrainIntImpl : Grain, IFSharpGrainWithIntKey
{
    private readonly IUniversalGrainHandler _handler;
    private object? _currentState;

    /// <summary>
    /// Initialises the grain with the universal message dispatcher resolved from silo DI.
    /// </summary>
    /// <param name="handler">The universal dispatcher registered via <c>AddFSharpGrain</c>.</param>
    public FSharpGrainIntImpl(IUniversalGrainHandler handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// Class-level reentrancy predicate consulted by the Orleans runtime when a request
    /// arrives while this activation is already processing another request. Returns
    /// <c>true</c> when the incoming message's runtime type was registered as interleavable
    /// via the <c>interleaveMessage</c> grain CE operation (see <see cref="FSharpInterleaveRegistry"/>).
    /// </summary>
    /// <param name="req">The incoming invocation; argument 0 is the boxed F# message.</param>
    public static bool MayInterleavePredicate(IInvokable req) =>
        req.GetArgumentCount() > 0
        && req.GetArgument(0) is { } message
        && FSharpInterleaveRegistry.MayInterleave(message.GetType());

    /// <inheritdoc/>
    public async Task<object> HandleMessage(object message)
    {
        _currentState ??= _handler.GetDefaultState(message.GetType());
        var dispatch = await _handler.Handle(_currentState, message, ServiceProvider, GrainFactory, this);
        _currentState = dispatch.NewState;
        return dispatch.Result ?? (object)Unit.Default;
    }

    /// <inheritdoc/>
    public async Task HandleMessageOneWay(object message)
    {
        _currentState ??= _handler.GetDefaultState(message.GetType());
        var dispatch = await _handler.Handle(_currentState, message, ServiceProvider, GrainFactory, this);
        _currentState = dispatch.NewState;
    }

    private readonly struct Unit { public static readonly Unit Default = default; }
}
