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
    /// Sends a command to a string-keyed grain, ignoring the result.
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
