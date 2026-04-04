namespace Orleans.FSharp

open System
open System.Threading.Tasks
open Orleans

/// <summary>
/// A typed handle to an F# grain using a string key.
/// Wraps the universal IFSharpGrain interface, hiding the object boxing/unboxing
/// so callers work with strongly-typed commands and state.
/// </summary>
/// <typeparam name="'State">The grain's state type, returned from send operations.</typeparam>
/// <typeparam name="'Command">The grain's command/message type.</typeparam>
[<Struct>]
type FSharpGrainHandle<'State, 'Command> =
    internal
        {
            /// <summary>The underlying IFSharpGrain proxy.</summary>
            Grain: IFSharpGrain
        }

/// <summary>
/// A typed handle to an F# grain using a GUID key.
/// </summary>
/// <typeparam name="'State">The grain's state type.</typeparam>
/// <typeparam name="'Command">The grain's command/message type.</typeparam>
[<Struct>]
type FSharpGrainGuidHandle<'State, 'Command> =
    internal
        {
            /// <summary>The underlying IFSharpGrainWithGuidKey proxy.</summary>
            Grain: IFSharpGrainWithGuidKey
        }

/// <summary>
/// A typed handle to an F# grain using an integer key.
/// </summary>
/// <typeparam name="'State">The grain's state type.</typeparam>
/// <typeparam name="'Command">The grain's command/message type.</typeparam>
[<Struct>]
type FSharpGrainIntHandle<'State, 'Command> =
    internal
        {
            /// <summary>The underlying IFSharpGrainWithIntKey proxy.</summary>
            Grain: IFSharpGrainWithIntKey
        }

/// <summary>
/// Functions for creating typed grain handles and sending commands.
/// Eliminates the need for per-grain C# interfaces by using the universal
/// IFSharpGrain proxy with type-safe wrappers.
/// </summary>
/// <remarks>
/// <para>Choosing the right function:</para>
/// <list type="bullet">
///   <item><description><c>send</c> — handler returns <c>(newState, box newState)</c>;
///     use when the caller needs the new state value.</description></item>
///   <item><description><c>post</c> — fire-and-forget; use for commands that don't
///     need a return value (e.g., state-changing side-effects).</description></item>
///   <item><description><c>ask&lt;'S,'C,'R&gt;</c> — handler returns a <c>'R</c> value
///     different from the state (e.g., <c>int</c>, <c>string</c>, a tuple); use with
///     <c>handleTyped</c> or when the boxed result is not the new state.</description></item>
/// </list>
/// </remarks>
[<RequireQualifiedAccess>]
module FSharpGrain =

    /// <summary>
    /// Creates a typed grain handle for a string-keyed grain.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="key">The string primary key of the grain.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>A typed grain handle.</returns>
    let ref<'State, 'Command> (factory: IGrainFactory) (key: string) : FSharpGrainHandle<'State, 'Command> =
        { Grain = factory.GetGrain<IFSharpGrain>(key) }

    /// <summary>
    /// Creates a typed grain handle for a GUID-keyed grain.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="key">The GUID primary key of the grain.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>A typed grain handle.</returns>
    let refGuid<'State, 'Command> (factory: IGrainFactory) (key: Guid) : FSharpGrainGuidHandle<'State, 'Command> =
        { Grain = factory.GetGrain<IFSharpGrainWithGuidKey>(key) }

    /// <summary>
    /// Creates a typed grain handle for an integer-keyed grain.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="key">The integer primary key of the grain.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>A typed grain handle.</returns>
    let refInt<'State, 'Command> (factory: IGrainFactory) (key: int64) : FSharpGrainIntHandle<'State, 'Command> =
        { Grain = factory.GetGrain<IFSharpGrainWithIntKey>(key) }

    /// <summary>
    /// Sends a command to a string-keyed grain and returns the typed state.
    /// The command is boxed, dispatched via IFSharpGrain.HandleMessage,
    /// and the result is unboxed to the expected state type.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>A Task containing the typed result.</returns>
    let send<'State, 'Command> (cmd: 'Command) (handle: FSharpGrainHandle<'State, 'Command>) : Task<'State> =
        task {
            let! result = handle.Grain.HandleMessage(box cmd)
            return result :?> 'State
        }

    /// <summary>
    /// Sends a command to a string-keyed grain, discarding the return value.
    /// <para>
    /// <b>Note:</b> this function awaits the full round-trip RPC call to the grain and
    /// discards the result. It is <em>not</em> a true fire-and-forget (one-way) call.
    /// Use this when you want to send a state-changing command without needing the new state.
    /// For a genuine one-way message, annotate the handler method with <c>[&lt;OneWay&gt;]</c>
    /// and use <c>FSharpGrain.post</c> — both approaches will complete the Task on return.
    /// </para>
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>A Task that completes when the grain finishes processing.</returns>
    let post<'State, 'Command> (cmd: 'Command) (handle: FSharpGrainHandle<'State, 'Command>) : Task =
        task {
            let! _ = handle.Grain.HandleMessage(box cmd)
            ()
        }

    /// <summary>
    /// Sends a command to a GUID-keyed grain and returns the typed state.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>A Task containing the typed result.</returns>
    let sendGuid<'State, 'Command> (cmd: 'Command) (handle: FSharpGrainGuidHandle<'State, 'Command>) : Task<'State> =
        task {
            let! result = handle.Grain.HandleMessage(box cmd)
            return result :?> 'State
        }

    /// <summary>
    /// Sends a command to a GUID-keyed grain, ignoring the result.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>A Task that completes when the grain finishes processing.</returns>
    let postGuid<'State, 'Command> (cmd: 'Command) (handle: FSharpGrainGuidHandle<'State, 'Command>) : Task =
        task {
            let! _ = handle.Grain.HandleMessage(box cmd)
            ()
        }

    /// <summary>
    /// Sends a command to an integer-keyed grain and returns the typed state.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>A Task containing the typed result.</returns>
    let sendInt<'State, 'Command> (cmd: 'Command) (handle: FSharpGrainIntHandle<'State, 'Command>) : Task<'State> =
        task {
            let! result = handle.Grain.HandleMessage(box cmd)
            return result :?> 'State
        }

    /// <summary>
    /// Sends a command to an integer-keyed grain, ignoring the result.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>A Task that completes when the grain finishes processing.</returns>
    let postInt<'State, 'Command> (cmd: 'Command) (handle: FSharpGrainIntHandle<'State, 'Command>) : Task =
        task {
            let! _ = handle.Grain.HandleMessage(box cmd)
            ()
        }

    /// <summary>
    /// Sends a command to a string-keyed grain and returns the typed result value.
    /// Unlike <c>send</c> which expects the result to be the new state, <c>ask</c> lets
    /// you specify a separate <typeparamref name="'Result"/> type for the handler's
    /// second return value (the boxed result). Use this when the handler returns a value
    /// that is not the state — e.g., a count, a string message, or a boolean flag.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <typeparam name="'Result">The expected result type returned by the handler.</typeparam>
    /// <returns>A Task containing the typed result.</returns>
    /// <exception cref="System.InvalidCastException">
    /// Thrown if the handler's result value cannot be cast to <typeparamref name="'Result"/>.
    /// </exception>
    let ask<'State, 'Command, 'Result> (cmd: 'Command) (handle: FSharpGrainHandle<'State, 'Command>) : Task<'Result> =
        task {
            let! result = handle.Grain.HandleMessage(box cmd)
            return result :?> 'Result
        }

    /// <summary>
    /// Sends a command to a GUID-keyed grain and returns the typed result value.
    /// Use this when the handler's result differs from the grain state type.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <typeparam name="'Result">The expected result type returned by the handler.</typeparam>
    /// <returns>A Task containing the typed result.</returns>
    let askGuid<'State, 'Command, 'Result> (cmd: 'Command) (handle: FSharpGrainGuidHandle<'State, 'Command>) : Task<'Result> =
        task {
            let! result = handle.Grain.HandleMessage(box cmd)
            return result :?> 'Result
        }

    /// <summary>
    /// Sends a command to an integer-keyed grain and returns the typed result value.
    /// Use this when the handler's result differs from the grain state type.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <typeparam name="'Result">The expected result type returned by the handler.</typeparam>
    /// <returns>A Task containing the typed result.</returns>
    let askInt<'State, 'Command, 'Result> (cmd: 'Command) (handle: FSharpGrainIntHandle<'State, 'Command>) : Task<'Result> =
        task {
            let! result = handle.Grain.HandleMessage(box cmd)
            return result :?> 'Result
        }

// ---------------------------------------------------------------------------
// Event-sourced grain handles
// ---------------------------------------------------------------------------

/// <summary>
/// A typed handle to an F# event-sourced grain with a string key.
/// Wraps <see cref="IFSharpEventSourcedGrain"/>, hiding the object boxing/unboxing
/// so callers work with strongly-typed commands and state.
/// </summary>
/// <typeparam name="'State">The grain's state type, returned from send operations.</typeparam>
/// <typeparam name="'Command">The grain's command type.</typeparam>
[<Struct>]
type FSharpEventSourcedGrainHandle<'State, 'Command> =
    internal
        {
            /// <summary>The underlying IFSharpEventSourcedGrain proxy.</summary>
            Grain: IFSharpEventSourcedGrain
        }

/// <summary>
/// Functions for creating typed handles and sending commands to F# event-sourced grains.
/// Uses the universal <see cref="IFSharpEventSourcedGrain"/> proxy — no per-grain C# stub needed.
/// Register the grain definition via <c>AddFSharpEventSourcedGrain</c> in the silo configurator.
/// </summary>
/// <example>
/// <code lang="fsharp">
/// // Get a typed handle — no IBankAccountGrain interface or C# stub needed
/// let grain = FSharpEventSourcedGrain.ref&lt;BankAccountState, BankAccountCommand&gt; grainFactory "acc-1"
/// // Send a command, get back strongly-typed state
/// let! state = FSharpEventSourcedGrain.send (Deposit 100m) grain
/// printfn "Balance: %M" state.Balance
/// </code>
/// </example>
[<RequireQualifiedAccess>]
module FSharpEventSourcedGrain =

    /// <summary>
    /// Creates a typed handle for a string-keyed event-sourced grain.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="key">The string primary key of the grain.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command type.</typeparam>
    /// <returns>A typed event-sourced grain handle.</returns>
    let ref<'State, 'Command> (factory: IGrainFactory) (key: string) : FSharpEventSourcedGrainHandle<'State, 'Command> =
        // grainClassNamePrefix uses StartsWith against the grain's full C# type name.
        // Use typeof<FSharpEventSourcedGrainImpl>.FullName so Orleans picks the universal
        // base class unambiguously when thin stubs (e.g. BankAccountGrainImpl) also
        // implement IFSharpEventSourcedGrain via a derived interface.
        { Grain = factory.GetGrain<IFSharpEventSourcedGrain>(key, typeof<FSharpEventSourcedGrainImpl>.FullName) }

    /// <summary>
    /// Sends a command to an event-sourced grain and returns the new state.
    /// The command is boxed, dispatched via <c>IFSharpEventSourcedGrain.HandleCommand</c>,
    /// and the result is unboxed to <typeparamref name="'State"/>.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command type.</typeparam>
    /// <returns>A Task containing the new typed state after the command is applied.</returns>
    let send<'State, 'Command> (cmd: 'Command) (handle: FSharpEventSourcedGrainHandle<'State, 'Command>) : Task<'State> =
        task {
            let! result = handle.Grain.HandleCommand(box cmd)
            return result :?> 'State
        }

    /// <summary>
    /// Sends a command to an event-sourced grain, discarding the returned state.
    /// Use when you need the side-effect but not the resulting state value.
    /// </summary>
    /// <param name="cmd">The command to send.</param>
    /// <param name="handle">The typed grain handle.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command type.</typeparam>
    /// <returns>A Task that completes when the grain finishes processing.</returns>
    let post<'State, 'Command> (cmd: 'Command) (handle: FSharpEventSourcedGrainHandle<'State, 'Command>) : Task =
        task {
            let! _ = handle.Grain.HandleCommand(box cmd)
            ()
        }

    /// <summary>
    /// Creates a typed handle for a grain that inherits <see cref="IFSharpEventSourcedGrain"/>.
    /// Use this when your grain interface is defined as <c>type IMyGrain = inherit IFSharpEventSourcedGrain</c>
    /// and is backed by a generated thin stub (<c>MyGrainImpl : FSharpEventSourcedGrainImpl, IMyGrain</c>).
    /// Unlike <c>ref</c>, this routes through the named interface rather than the universal one,
    /// giving a distinct grain type in the Orleans system (visible in dashboards and logs).
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="key">The string primary key of the grain.</param>
    /// <typeparam name="'Iface">The specific grain interface (must inherit IFSharpEventSourcedGrain).</typeparam>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command type.</typeparam>
    /// <returns>A typed event-sourced grain handle.</returns>
    let refTyped<'Iface, 'State, 'Command when 'Iface :> IFSharpEventSourcedGrain>
        (factory: IGrainFactory)
        (key: string)
        : FSharpEventSourcedGrainHandle<'State, 'Command> =
        { Grain = factory.GetGrain<'Iface>(key) :> IFSharpEventSourcedGrain }

    /// <summary>
    /// Wraps an already-obtained grain reference (that inherits <see cref="IFSharpEventSourcedGrain"/>)
    /// into a typed handle. Useful when you already have a grain reference from elsewhere.
    /// </summary>
    /// <param name="grain">The grain reference to wrap.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command type.</typeparam>
    /// <returns>A typed event-sourced grain handle.</returns>
    let from<'State, 'Command> (grain: IFSharpEventSourcedGrain) : FSharpEventSourcedGrainHandle<'State, 'Command> =
        { Grain = grain }
