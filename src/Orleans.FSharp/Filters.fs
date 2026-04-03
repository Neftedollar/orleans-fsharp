namespace Orleans.FSharp

open System
open System.Threading.Tasks
open Orleans

/// <summary>
/// F# wrapper for creating incoming grain call filters from functions.
/// Wraps a <c>(IIncomingGrainCallContext -> Task&lt;unit&gt;)</c> function as an <see cref="IIncomingGrainCallFilter"/>.
/// </summary>
/// <param name="handler">The async function to invoke for each incoming grain call.</param>
type FSharpIncomingFilter(handler: IIncomingGrainCallContext -> Task<unit>) =
    interface IIncomingGrainCallFilter with
        member _.Invoke(context) = handler context :> Task

/// <summary>
/// F# wrapper for creating outgoing grain call filters from functions.
/// Wraps a <c>(IOutgoingGrainCallContext -> Task&lt;unit&gt;)</c> function as an <see cref="IOutgoingGrainCallFilter"/>.
/// </summary>
/// <param name="handler">The async function to invoke for each outgoing grain call.</param>
type FSharpOutgoingFilter(handler: IOutgoingGrainCallContext -> Task<unit>) =
    interface IOutgoingGrainCallFilter with
        member _.Invoke(context) = handler context :> Task

/// <summary>
/// Module for creating F#-idiomatic grain call filters.
/// Provides factory functions to construct <see cref="IIncomingGrainCallFilter"/> and
/// <see cref="IOutgoingGrainCallFilter"/> instances from F# functions.
/// </summary>
module Filter =

    /// <summary>
    /// Create an incoming filter from an F# async function.
    /// The function receives the <see cref="IIncomingGrainCallContext"/> and must call
    /// <c>context.Invoke()</c> to continue the filter pipeline.
    /// </summary>
    /// <param name="handler">The async function to invoke for each incoming grain call.</param>
    /// <returns>An <see cref="IIncomingGrainCallFilter"/> wrapping the given function.</returns>
    let incoming (handler: IIncomingGrainCallContext -> Task<unit>) : IIncomingGrainCallFilter =
        FSharpIncomingFilter(handler)

    /// <summary>
    /// Create an outgoing filter from an F# async function.
    /// The function receives the <see cref="IOutgoingGrainCallContext"/> and must call
    /// <c>context.Invoke()</c> to continue the filter pipeline.
    /// </summary>
    /// <param name="handler">The async function to invoke for each outgoing grain call.</param>
    /// <returns>An <see cref="IOutgoingGrainCallFilter"/> wrapping the given function.</returns>
    let outgoing (handler: IOutgoingGrainCallContext -> Task<unit>) : IOutgoingGrainCallFilter =
        FSharpOutgoingFilter(handler)

    /// <summary>
    /// Create an incoming filter that runs <paramref name="before"/> before and <paramref name="after"/>
    /// after the grain call. The <c>context.Invoke()</c> call is inserted automatically between
    /// the two functions.
    /// </summary>
    /// <param name="before">The async function to run before the grain method executes.</param>
    /// <param name="after">The async function to run after the grain method executes.</param>
    /// <returns>An <see cref="IIncomingGrainCallFilter"/> that runs the before/after functions around the call.</returns>
    let incomingWithAround
        (before: IIncomingGrainCallContext -> Task<unit>)
        (after: IIncomingGrainCallContext -> Task<unit>)
        : IIncomingGrainCallFilter =
        FSharpIncomingFilter(fun context ->
            task {
                do! before context
                do! context.Invoke()
                do! after context
            })

    /// <summary>
    /// Create an outgoing filter that runs <paramref name="before"/> before and <paramref name="after"/>
    /// after the grain call. The <c>context.Invoke()</c> call is inserted automatically between
    /// the two functions.
    /// </summary>
    /// <param name="before">The async function to run before the outgoing grain call.</param>
    /// <param name="after">The async function to run after the outgoing grain call.</param>
    /// <returns>An <see cref="IOutgoingGrainCallFilter"/> that runs the before/after functions around the call.</returns>
    let outgoingWithAround
        (before: IOutgoingGrainCallContext -> Task<unit>)
        (after: IOutgoingGrainCallContext -> Task<unit>)
        : IOutgoingGrainCallFilter =
        FSharpOutgoingFilter(fun context ->
            task {
                do! before context
                do! context.Invoke()
                do! after context
            })

/// <summary>
/// Helper functions for extracting call context details from grain call filter contexts.
/// Useful for logging, metrics, and authorization filters.
/// </summary>
module FilterContext =

    /// <summary>
    /// Gets the method name being called on the grain.
    /// </summary>
    /// <param name="ctx">The incoming grain call context.</param>
    /// <returns>The name of the method being invoked.</returns>
    let methodName (ctx: IIncomingGrainCallContext) : string =
        ctx.ImplementationMethod.Name

    /// <summary>
    /// Gets the grain interface type being called.
    /// </summary>
    /// <param name="ctx">The incoming grain call context.</param>
    /// <returns>The System.Type of the grain interface.</returns>
    let interfaceType (ctx: IIncomingGrainCallContext) : Type =
        ctx.InterfaceMethod.DeclaringType

    /// <summary>
    /// Gets the grain instance (if available) from the call context.
    /// Returns None if the grain instance is not accessible.
    /// </summary>
    /// <param name="ctx">The incoming grain call context.</param>
    /// <returns>Some with the grain instance object, or None if not available.</returns>
    let grainInstance (ctx: IIncomingGrainCallContext) : obj option =
        try
            let target = ctx.TargetContext
            if isNull (box target) then None
            else Some (box target)
        with _ -> None
