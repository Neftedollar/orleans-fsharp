using Orleans;

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
