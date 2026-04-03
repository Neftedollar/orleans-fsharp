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
    Task<GrainDispatchResult> Handle(object? currentState, object message);
}

/// <summary>
/// Universal concrete grain class for all F# grains using the <c>FSharpGrain.ref</c> pattern.
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
    /// <param name="handler">
    /// The universal dispatcher registered via <c>AddFSharpGrain</c>.
    /// </param>
    public FSharpGrainImpl(IUniversalGrainHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(object message)
    {
        // Lazy state initialisation: use default state on the first message.
        _currentState ??= _handler.GetDefaultState(message.GetType());

        var dispatch = await _handler.Handle(_currentState, message);
        _currentState = dispatch.NewState;

        // Orleans requires a non-null return value from Task<object>.
        return dispatch.Result ?? (object)Unit.Default;
    }

    // Marker struct returned when the handler produces a null result,
    // to keep Orleans happy with the non-null Task<object> contract.
    private readonly struct Unit
    {
        public static readonly Unit Default = default;
    }
}
