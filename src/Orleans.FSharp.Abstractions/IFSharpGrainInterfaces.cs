using Orleans;
using Orleans.Runtime;

namespace Orleans.FSharp;

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

    /// <inheritdoc/>
    public async Task<object> HandleMessage(object message)
    {
        _currentState ??= _handler.GetDefaultState(message.GetType());
        var dispatch = await _handler.Handle(_currentState, message, ServiceProvider, GrainFactory, this);
        _currentState = dispatch.NewState;
        return dispatch.Result ?? (object)Unit.Default;
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

    /// <inheritdoc/>
    public async Task<object> HandleMessage(object message)
    {
        _currentState ??= _handler.GetDefaultState(message.GetType());
        var dispatch = await _handler.Handle(_currentState, message, ServiceProvider, GrainFactory, this);
        _currentState = dispatch.NewState;
        return dispatch.Result ?? (object)Unit.Default;
    }

    private readonly struct Unit { public static readonly Unit Default = default; }
}

/// <summary>
/// Universal concrete grain class for all F# grains using the <c>FSharpGrain.refInt</c> pattern
/// (integer key variant).
/// </summary>
/// <remarks>
/// State is kept purely in memory. For durable persistence use the typed
/// <c>FSharpGrain&lt;State, Message&gt;</c> pattern in Orleans.FSharp.Runtime.
/// </remarks>
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

    /// <inheritdoc/>
    public async Task<object> HandleMessage(object message)
    {
        _currentState ??= _handler.GetDefaultState(message.GetType());
        var dispatch = await _handler.Handle(_currentState, message, ServiceProvider, GrainFactory, this);
        _currentState = dispatch.NewState;
        return dispatch.Result ?? (object)Unit.Default;
    }

    private readonly struct Unit { public static readonly Unit Default = default; }
}
