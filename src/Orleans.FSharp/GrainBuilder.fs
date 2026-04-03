namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Runtime

/// <summary>
/// Specifies the grain placement strategy that determines which silo activates a grain.
/// In C# CodeGen, these map to Orleans placement attributes on the grain class.
/// </summary>
type PlacementStrategy =
    /// <summary>Uses the Orleans default placement strategy (typically random).</summary>
    | Default
    /// <summary>Prefers to place the grain on the silo where the request originated.</summary>
    | PreferLocal
    /// <summary>Places the grain on a randomly selected silo.</summary>
    | Random
    /// <summary>Places the grain based on a hash of the grain ID for consistent placement.</summary>
    | HashBased

/// <summary>
/// Provides access to grain infrastructure from within a grain handler.
/// Enables grain-to-grain communication via type-safe GrainRef creation
/// and service resolution via the IServiceProvider.
/// </summary>
type GrainContext =
    {
        /// <summary>The Orleans grain factory for creating grain references.</summary>
        GrainFactory: IGrainFactory
        /// <summary>The dependency injection service provider for resolving registered services.</summary>
        ServiceProvider: IServiceProvider
        /// <summary>Named persistent states for grains with multiple state stores.
        /// Each entry maps a state name to a boxed IPersistentState instance.</summary>
        States: Map<string, obj>
    }

/// <summary>
/// Functions for creating type-safe grain references and resolving services from within a grain context.
/// </summary>
[<RequireQualifiedAccess>]
module GrainContext =

    /// <summary>
    /// Resolves a service of type 'T from the grain's dependency injection container.
    /// Throws InvalidOperationException if the service is not registered.
    /// </summary>
    /// <param name="ctx">The grain context providing access to the service provider.</param>
    /// <typeparam name="'T">The service type to resolve.</typeparam>
    /// <returns>The resolved service instance.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when the service is not registered.</exception>
    let getService<'T when 'T: not struct> (ctx: GrainContext) : 'T =
        ctx.ServiceProvider.GetRequiredService<'T>()

    /// <summary>
    /// Gets a named persistent state from the grain context.
    /// The state must have been declared using 'additionalState' in the grain CE
    /// and is available in handlers registered with 'handleWithContext' or 'handleWithServices'.
    /// </summary>
    /// <param name="ctx">The grain context containing the named states.</param>
    /// <param name="name">The name of the persistent state as declared in the grain definition.</param>
    /// <typeparam name="'T">The type of the persistent state value.</typeparam>
    /// <returns>The IPersistentState wrapper for the named state.</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when the named state is not found.</exception>
    /// <exception cref="System.InvalidCastException">Thrown when the state type does not match.</exception>
    let getState<'T> (ctx: GrainContext) (name: string) : IPersistentState<'T> =
        match ctx.States |> Map.tryFind name with
        | Some boxedState -> boxedState :?> IPersistentState<'T>
        | None ->
            raise (System.Collections.Generic.KeyNotFoundException($"Named state '{name}' not found in grain context. Ensure it was declared using 'additionalState' in the grain CE."))

    /// <summary>
    /// Gets a type-safe reference to a grain by string key from within a grain handler.
    /// </summary>
    /// <param name="ctx">The grain context providing access to the grain factory.</param>
    /// <param name="key">The string primary key of the grain.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithStringKey.</typeparam>
    /// <returns>A type-safe grain reference.</returns>
    let getGrainByString<'TInterface when 'TInterface :> IGrainWithStringKey>
        (ctx: GrainContext)
        (key: string)
        : GrainRef<'TInterface, string> =
        GrainRef.ofString<'TInterface> ctx.GrainFactory key

    /// <summary>
    /// Gets a type-safe reference to a grain by GUID key from within a grain handler.
    /// </summary>
    /// <param name="ctx">The grain context providing access to the grain factory.</param>
    /// <param name="key">The GUID primary key of the grain.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithGuidKey.</typeparam>
    /// <returns>A type-safe grain reference.</returns>
    let getGrainByGuid<'TInterface when 'TInterface :> IGrainWithGuidKey>
        (ctx: GrainContext)
        (key: Guid)
        : GrainRef<'TInterface, Guid> =
        GrainRef.ofGuid<'TInterface> ctx.GrainFactory key

    /// <summary>
    /// Gets a type-safe reference to a grain by int64 key from within a grain handler.
    /// </summary>
    /// <param name="ctx">The grain context providing access to the grain factory.</param>
    /// <param name="key">The int64 primary key of the grain.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithIntegerKey.</typeparam>
    /// <returns>A type-safe grain reference.</returns>
    let getGrainByInt64<'TInterface when 'TInterface :> IGrainWithIntegerKey>
        (ctx: GrainContext)
        (key: int64)
        : GrainRef<'TInterface, int64> =
        GrainRef.ofInt64<'TInterface> ctx.GrainFactory key

/// <summary>
/// Defines the complete specification for an F# grain, including its initial state,
/// message handler, persistence configuration, and lifecycle hooks.
/// </summary>
/// <typeparam name="'State">The type of the grain's state.</typeparam>
/// <typeparam name="'Message">The type of messages the grain handles.</typeparam>
/// <summary>
/// Declares a named additional persistent state for use in grain handlers.
/// Each entry specifies the state name, the storage provider name, and the default value.
/// </summary>
/// <typeparam name="'T">The type of the state value.</typeparam>
type AdditionalStateSpec =
    {
        /// <summary>The name for this persistent state (used as the state key in storage).</summary>
        Name: string
        /// <summary>The Orleans storage provider name to use for this state.</summary>
        StorageName: string
        /// <summary>The default value for this state, boxed for storage in the definition.</summary>
        DefaultValue: obj
        /// <summary>The CLR type of the state value.</summary>
        StateType: Type
    }

type GrainDefinition<'State, 'Message> =
    {
        /// <summary>The initial state value for the grain when first activated.</summary>
        DefaultState: 'State
        /// <summary>The message handler function. Takes current state and a message, returns new state and a boxed result.</summary>
        Handler: ('State -> 'Message -> Task<'State * obj>) option
        /// <summary>The context-aware message handler function. Takes a GrainContext, current state and a message, returns new state and a boxed result.</summary>
        ContextHandler: (GrainContext -> 'State -> 'Message -> Task<'State * obj>) option
        /// <summary>The cancellable message handler function. Takes current state, a message, and a CancellationToken, returns new state and a boxed result.</summary>
        CancellableHandler: ('State -> 'Message -> CancellationToken -> Task<'State * obj>) option
        /// <summary>The cancellable context-aware message handler function. Takes a GrainContext, current state, a message, and a CancellationToken, returns new state and a boxed result.</summary>
        CancellableContextHandler: (GrainContext -> 'State -> 'Message -> CancellationToken -> Task<'State * obj>) option
        /// <summary>The name of the Orleans storage provider, or None for in-memory only.</summary>
        PersistenceName: string option
        /// <summary>Optional activation hook. Called when the grain is activated with the current state.</summary>
        OnActivate: ('State -> Task<'State>) option
        /// <summary>Optional deactivation hook. Called when the grain is being deactivated.</summary>
        OnDeactivate: ('State -> Task<unit>) option
        /// <summary>Named reminder handlers. Each handler takes state, reminder name, and TickStatus, and returns new state.</summary>
        ReminderHandlers: Map<string, 'State -> string -> TickStatus -> Task<'State>>
        /// <summary>Named declarative timer handlers. Each entry maps a timer name to its dueTime, period, and state-transforming callback.
        /// Timers are automatically registered on grain activation and disposed on deactivation by Orleans.</summary>
        TimerHandlers: Map<string, TimeSpan * TimeSpan * ('State -> Task<'State>)>
        /// <summary>Whether this grain allows reentrant (concurrent) message processing.</summary>
        IsReentrant: bool
        /// <summary>Set of method names that are always interleaved (processed concurrently) even on non-reentrant grains.</summary>
        InterleavedMethods: Set<string>
        /// <summary>Whether this grain is a stateless worker that allows multiple activations per silo.</summary>
        IsStatelessWorker: bool
        /// <summary>Maximum number of local worker activations per silo, or None for CPU count.</summary>
        MaxLocalWorkers: int option
        /// <summary>The placement strategy for the grain. In C# CodeGen, maps to Orleans placement attributes.</summary>
        PlacementStrategy: PlacementStrategy
        /// <summary>Named additional persistent states. Each entry maps a state name to its storage provider name, default value, and type.</summary>
        AdditionalStates: Map<string, AdditionalStateSpec>
    }

/// <summary>
/// Utility functions for working with GrainDefinition values.
/// </summary>
[<RequireQualifiedAccess>]
module GrainDefinition =

    /// <summary>
    /// Returns true if any handler (plain, context, cancellable, or cancellable-context) is registered.
    /// </summary>
    let hasAnyHandler (definition: GrainDefinition<'State, 'Message>) =
        definition.Handler.IsSome
        || definition.ContextHandler.IsSome
        || definition.CancellableHandler.IsSome
        || definition.CancellableContextHandler.IsSome

    /// <summary>
    /// Gets the handler from a GrainDefinition, raising InvalidOperationException if no handler is registered.
    /// If only a context-aware handler is registered, wraps it with a default empty context.
    /// Prefer <see cref="getContextHandler"/> when a GrainContext is available.
    /// </summary>
    /// <param name="definition">The grain definition to extract the handler from.</param>
    /// <returns>The message handler function.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when no handler has been registered.</exception>
    let getHandler (definition: GrainDefinition<'State, 'Message>) : 'State -> 'Message -> Task<'State * obj> =
        match definition.Handler with
        | Some h -> h
        | None ->
            match definition.CancellableHandler with
            | Some ch -> fun state msg -> ch state msg CancellationToken.None
            | None ->
                if hasAnyHandler definition then
                    fun _state _msg ->
                        invalidOp
                            $"This grain definition uses a context-aware or cancellable handler which requires a GrainContext or CancellationToken. Use GrainDefinition.getContextHandler or getCancellableContextHandler instead, or invoke via the FSharpGrain runtime. State type: '{typeof<'State>.Name}', message type: '{typeof<'Message>.Name}'."
                        |> Task.FromException<'State * obj>
                else
                    invalidOp
                        $"No handler registered for grain definition with state type '{typeof<'State>.Name}' and message type '{typeof<'Message>.Name}'. Use the 'handle' or 'handleWithContext' custom operation in the grain {{ }} CE to register a handler."

    /// <summary>
    /// Gets the context-aware handler from a GrainDefinition.
    /// If only a plain handler is registered, wraps it to accept (and ignore) the context.
    /// Raises InvalidOperationException if no handler of either kind is registered.
    /// </summary>
    /// <param name="definition">The grain definition to extract the handler from.</param>
    /// <returns>The context-aware message handler function.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when no handler has been registered.</exception>
    let getContextHandler
        (definition: GrainDefinition<'State, 'Message>)
        : GrainContext -> 'State -> 'Message -> Task<'State * obj> =
        match definition.ContextHandler with
        | Some ch -> ch
        | None ->
            match definition.Handler with
            | Some h -> fun _ctx state msg -> h state msg
            | None ->
                match definition.CancellableContextHandler with
                | Some cch -> fun ctx state msg -> cch ctx state msg CancellationToken.None
                | None ->
                    match definition.CancellableHandler with
                    | Some ch -> fun _ctx state msg -> ch state msg CancellationToken.None
                    | None ->
                        invalidOp
                            $"No handler registered for grain definition with state type '{typeof<'State>.Name}' and message type '{typeof<'Message>.Name}'. Use 'handle' or 'handleWithContext' in the grain {{ }} CE."

    /// <summary>
    /// Gets the cancellable context-aware handler from a GrainDefinition.
    /// Falls back through all handler variants, adapting them as needed:
    /// CancellableContextHandler > CancellableHandler > ContextHandler > Handler.
    /// </summary>
    /// <param name="definition">The grain definition to extract the handler from.</param>
    /// <returns>The cancellable context-aware message handler function.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when no handler has been registered.</exception>
    let getCancellableContextHandler
        (definition: GrainDefinition<'State, 'Message>)
        : GrainContext -> 'State -> 'Message -> CancellationToken -> Task<'State * obj> =
        match definition.CancellableContextHandler with
        | Some cch -> cch
        | None ->
            match definition.CancellableHandler with
            | Some ch -> fun _ctx state msg ct -> ch state msg ct
            | None ->
                match definition.ContextHandler with
                | Some h -> fun ctx state msg _ct -> h ctx state msg
                | None ->
                    match definition.Handler with
                    | Some h -> fun _ctx state msg _ct -> h state msg
                    | None ->
                        invalidOp
                            $"No handler registered for grain definition with state type '{typeof<'State>.Name}' and message type '{typeof<'Message>.Name}'. Use 'handle', 'handleCancellable', or 'handleWithContext' in the grain {{ }} CE."

    /// <summary>
    /// Invokes the handler from a GrainDefinition with the given state and message.
    /// This method is designed for C# interop, avoiding F# curried function types.
    /// </summary>
    /// <param name="definition">The grain definition containing the handler.</param>
    /// <param name="state">The current state.</param>
    /// <param name="message">The message to handle.</param>
    /// <returns>A Task containing a tuple of the new state and a boxed result.</returns>
    let invokeHandler (definition: GrainDefinition<'State, 'Message>) (state: 'State) (message: 'Message) : Task<'State * obj> =
        let handler = getHandler definition
        handler state message

    /// <summary>
    /// Invokes the context-aware handler from a GrainDefinition with the given context, state, and message.
    /// This method is designed for C# interop, avoiding F# curried function types.
    /// </summary>
    /// <param name="definition">The grain definition containing the handler.</param>
    /// <param name="ctx">The grain context providing access to grain infrastructure.</param>
    /// <param name="state">The current state.</param>
    /// <param name="message">The message to handle.</param>
    /// <returns>A Task containing a tuple of the new state and a boxed result.</returns>
    let invokeContextHandler
        (definition: GrainDefinition<'State, 'Message>)
        (ctx: GrainContext)
        (state: 'State)
        (message: 'Message)
        : Task<'State * obj> =
        let handler = getContextHandler definition
        handler ctx state message

    /// <summary>
    /// Invokes the cancellable context-aware handler from a GrainDefinition.
    /// This method is designed for C# interop, avoiding F# curried function types.
    /// </summary>
    /// <param name="definition">The grain definition containing the handler.</param>
    /// <param name="ctx">The grain context providing access to grain infrastructure.</param>
    /// <param name="state">The current state.</param>
    /// <param name="message">The message to handle.</param>
    /// <param name="ct">The cancellation token to pass to the handler.</param>
    /// <returns>A Task containing a tuple of the new state and a boxed result.</returns>
    let invokeCancellableContextHandler
        (definition: GrainDefinition<'State, 'Message>)
        (ctx: GrainContext)
        (state: 'State)
        (message: 'Message)
        (ct: CancellationToken)
        : Task<'State * obj> =
        let handler = getCancellableContextHandler definition
        handler ctx state message ct

/// <summary>
/// Computation expression builder for declaratively defining grain behavior.
/// Use the <c>grain {{ }}</c> syntax with custom operations to build a GrainDefinition.
/// </summary>
/// <example>
/// <code>
/// let myGrain = grain {
///     defaultState 0
///     handle (fun state msg -> task { return state + 1, box (state + 1) })
///     persist "Default"
/// }
/// </code>
/// </example>
type GrainBuilder() =

    /// <summary>Yields the initial empty grain definition.</summary>
    member _.Yield(_: unit) : GrainDefinition<'State, 'Message> =
        {
            DefaultState = Unchecked.defaultof<'State>
            Handler = None
            ContextHandler = None
            CancellableHandler = None
            CancellableContextHandler = None
            PersistenceName = None
            OnActivate = None
            OnDeactivate = None
            ReminderHandlers = Map.empty
            TimerHandlers = Map.empty
            IsReentrant = false
            InterleavedMethods = Set.empty
            IsStatelessWorker = false
            MaxLocalWorkers = None
            PlacementStrategy = PlacementStrategy.Default
            AdditionalStates = Map.empty
        }

    /// <summary>
    /// Sets the initial state of the grain.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="value">The initial state value.</param>
    /// <returns>The updated grain definition with the default state set.</returns>
    [<CustomOperation("defaultState")>]
    member _.DefaultState(definition: GrainDefinition<'State, 'Message>, value: 'State) =
        { definition with DefaultState = value }

    /// <summary>
    /// Registers the message handler function for the grain.
    /// The handler takes the current state and a message, and returns a Task of the new state and a boxed result.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="handler">The message handler function.</param>
    /// <returns>The updated grain definition with the handler registered.</returns>
    [<CustomOperation("handle")>]
    member _.Handle
        (
            definition: GrainDefinition<'State, 'Message>,
            handler: 'State -> 'Message -> Task<'State * obj>
        ) =
        { definition with Handler = Some handler }

    /// <summary>
    /// Registers a context-aware message handler function for the grain.
    /// The handler receives a GrainContext (providing access to IGrainFactory for grain-to-grain calls),
    /// the current state, and a message, and returns a Task of the new state and a boxed result.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="handler">The context-aware message handler function.</param>
    /// <returns>The updated grain definition with the context handler registered.</returns>
    [<CustomOperation("handleWithContext")>]
    member _.HandleWithContext
        (
            definition: GrainDefinition<'State, 'Message>,
            handler: GrainContext -> 'State -> 'Message -> Task<'State * obj>
        ) =
        { definition with
            ContextHandler = Some handler
        }

    /// <summary>
    /// Sets the name of the Orleans storage provider for state persistence.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="name">The storage provider name (e.g., "Default", "AzureBlob").</param>
    /// <returns>The updated grain definition with persistence configured.</returns>
    [<CustomOperation("persist")>]
    member _.Persist(definition: GrainDefinition<'State, 'Message>, name: string) =
        { definition with
            PersistenceName = Some name
        }

    /// <summary>
    /// Registers an activation hook that runs when the grain is activated.
    /// The hook receives the current state and returns a potentially modified state.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="handler">The activation handler function.</param>
    /// <returns>The updated grain definition with the activation hook registered.</returns>
    [<CustomOperation("onActivate")>]
    member _.OnActivate(definition: GrainDefinition<'State, 'Message>, handler: 'State -> Task<'State>) =
        { definition with
            OnActivate = Some handler
        }

    /// <summary>
    /// Registers a deactivation hook that runs when the grain is being deactivated.
    /// The hook receives the current state and can perform cleanup.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="handler">The deactivation handler function.</param>
    /// <returns>The updated grain definition with the deactivation hook registered.</returns>
    [<CustomOperation("onDeactivate")>]
    member _.OnDeactivate(definition: GrainDefinition<'State, 'Message>, handler: 'State -> Task<unit>) =
        { definition with
            OnDeactivate = Some handler
        }

    /// <summary>
    /// Registers a named reminder handler for the grain.
    /// When a reminder with the given name fires, the handler is invoked with the current state,
    /// reminder name, and TickStatus, and returns the new state.
    /// Multiple reminders can be registered by calling onReminder multiple times with different names.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="name">The unique name of the reminder.</param>
    /// <param name="handler">The reminder handler function.</param>
    /// <returns>The updated grain definition with the reminder handler registered.</returns>
    [<CustomOperation("onReminder")>]
    member _.OnReminder
        (
            definition: GrainDefinition<'State, 'Message>,
            name: string,
            handler: 'State -> string -> TickStatus -> Task<'State>
        ) =
        { definition with
            ReminderHandlers = definition.ReminderHandlers |> Map.add name handler
        }

    /// <summary>
    /// Registers a declarative timer handler for the grain.
    /// The timer is automatically registered on grain activation and disposed on deactivation by Orleans.
    /// The handler receives the current state and returns the new state.
    /// Multiple timers can be registered by calling onTimer multiple times with different names.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="name">The unique name of the timer.</param>
    /// <param name="dueTime">The time delay before the first firing.</param>
    /// <param name="period">The interval between subsequent firings.</param>
    /// <param name="handler">The timer handler function that transforms state.</param>
    /// <returns>The updated grain definition with the timer handler registered.</returns>
    [<CustomOperation("onTimer")>]
    member _.OnTimer
        (
            definition: GrainDefinition<'State, 'Message>,
            name: string,
            dueTime: TimeSpan,
            period: TimeSpan,
            handler: 'State -> Task<'State>
        ) =
        { definition with
            TimerHandlers = definition.TimerHandlers |> Map.add name (dueTime, period, handler)
        }

    /// <summary>
    /// Marks the grain as reentrant, allowing concurrent message processing.
    /// A reentrant grain can process multiple messages simultaneously without waiting for each to complete.
    /// In C# CodeGen, this corresponds to the [Reentrant] attribute on the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <returns>The updated grain definition with reentrant mode enabled.</returns>
    [<CustomOperation("reentrant")>]
    member _.Reentrant(definition: GrainDefinition<'State, 'Message>) =
        { definition with IsReentrant = true }

    /// <summary>
    /// Marks a specific method as always interleaved (concurrently processed),
    /// even on non-reentrant grains. In C# CodeGen, this corresponds to the
    /// [AlwaysInterleave] attribute on the interface method.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="methodName">The name of the method to always interleave.</param>
    /// <returns>The updated grain definition with the method added to the interleaved set.</returns>
    [<CustomOperation("interleave")>]
    member _.Interleave(definition: GrainDefinition<'State, 'Message>, methodName: string) =
        { definition with
            InterleavedMethods = definition.InterleavedMethods |> Set.add methodName
        }

    /// <summary>
    /// Marks the grain as a stateless worker, allowing multiple activations per silo
    /// for load balancing. Stateless workers cannot use persistent state.
    /// In C# CodeGen, this corresponds to the [StatelessWorker] attribute on the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <returns>The updated grain definition with stateless worker mode enabled.</returns>
    [<CustomOperation("statelessWorker")>]
    member _.StatelessWorker(definition: GrainDefinition<'State, 'Message>) =
        { definition with
            IsStatelessWorker = true
        }

    /// <summary>
    /// Sets the maximum number of local worker activations per silo for a stateless worker grain.
    /// When not specified, Orleans defaults to the number of CPU cores.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="count">The maximum number of local worker activations.</param>
    /// <returns>The updated grain definition with the max activations set.</returns>
    [<CustomOperation("maxActivations")>]
    member _.MaxActivations(definition: GrainDefinition<'State, 'Message>, count: int) =
        { definition with
            MaxLocalWorkers = Some count
        }

    /// <summary>
    /// Registers a service-aware message handler function for the grain.
    /// This is a convenience alias for handleWithContext that emphasizes
    /// access to the IServiceProvider for dependency injection.
    /// The handler receives a GrainContext (providing access to IGrainFactory and IServiceProvider),
    /// the current state, and a message, and returns a Task of the new state and a boxed result.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="handler">The service-aware message handler function.</param>
    /// <returns>The updated grain definition with the context handler registered.</returns>
    [<CustomOperation("handleWithServices")>]
    member _.HandleWithServices
        (
            definition: GrainDefinition<'State, 'Message>,
            handler: GrainContext -> 'State -> 'Message -> Task<'State * obj>
        ) =
        { definition with
            ContextHandler = Some handler
        }

    /// <summary>
    /// Registers a cancellable message handler function for the grain.
    /// The handler takes the current state, a message, and a CancellationToken,
    /// and returns a Task of the new state and a boxed result.
    /// The CancellationToken is propagated from the Orleans runtime and can be used
    /// to abort long-running operations cooperatively.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="handler">The cancellable message handler function.</param>
    /// <returns>The updated grain definition with the cancellable handler registered.</returns>
    [<CustomOperation("handleCancellable")>]
    member _.HandleCancellable
        (
            definition: GrainDefinition<'State, 'Message>,
            handler: 'State -> 'Message -> CancellationToken -> Task<'State * obj>
        ) =
        { definition with
            CancellableHandler = Some handler
        }

    /// <summary>
    /// Registers a cancellable context-aware message handler function for the grain.
    /// The handler receives a GrainContext, the current state, a message, and a CancellationToken,
    /// and returns a Task of the new state and a boxed result.
    /// Combines access to grain infrastructure with cooperative cancellation support.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="handler">The cancellable context-aware message handler function.</param>
    /// <returns>The updated grain definition with the cancellable context handler registered.</returns>
    [<CustomOperation("handleWithContextCancellable")>]
    member _.HandleWithContextCancellable
        (
            definition: GrainDefinition<'State, 'Message>,
            handler: GrainContext -> 'State -> 'Message -> CancellationToken -> Task<'State * obj>
        ) =
        { definition with
            CancellableContextHandler = Some handler
        }

    /// <summary>
    /// Registers a cancellable service-aware message handler function for the grain.
    /// This is a convenience alias for handleWithContextCancellable that emphasizes
    /// access to the IServiceProvider for dependency injection.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="handler">The cancellable service-aware message handler function.</param>
    /// <returns>The updated grain definition with the cancellable context handler registered.</returns>
    [<CustomOperation("handleWithServicesCancellable")>]
    member _.HandleWithServicesCancellable
        (
            definition: GrainDefinition<'State, 'Message>,
            handler: GrainContext -> 'State -> 'Message -> CancellationToken -> Task<'State * obj>
        ) =
        { definition with
            CancellableContextHandler = Some handler
        }

    /// <summary>
    /// Declares a named additional persistent state for the grain.
    /// The state can be accessed in context-aware handlers via GrainContext.getState.
    /// Multiple additional states can be declared by calling additionalState multiple times.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="name">The unique name for this persistent state.</param>
    /// <param name="storageName">The Orleans storage provider name to use.</param>
    /// <param name="defaultValue">The default value for this state.</param>
    /// <returns>The updated grain definition with the additional state declared.</returns>
    [<CustomOperation("additionalState")>]
    member _.AdditionalState(definition: GrainDefinition<'State, 'Message>, name: string, storageName: string, defaultValue: 'T) =
        { definition with
            AdditionalStates =
                definition.AdditionalStates
                |> Map.add
                    name
                    {
                        Name = name
                        StorageName = storageName
                        DefaultValue = box defaultValue
                        StateType = typeof<'T>
                    }
        }

    /// <summary>
    /// Sets the grain placement strategy to PreferLocal.
    /// Prefers to place the grain on the silo where the request originated.
    /// In C# CodeGen, this corresponds to the [PreferLocalPlacement] attribute on the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <returns>The updated grain definition with PreferLocal placement.</returns>
    [<CustomOperation("preferLocalPlacement")>]
    member _.PreferLocalPlacement(definition: GrainDefinition<'State, 'Message>) =
        { definition with
            PlacementStrategy = PlacementStrategy.PreferLocal
        }

    /// <summary>
    /// Sets the grain placement strategy to Random.
    /// Places the grain on a randomly selected compatible silo.
    /// In C# CodeGen, this corresponds to the [RandomPlacement] attribute on the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <returns>The updated grain definition with Random placement.</returns>
    [<CustomOperation("randomPlacement")>]
    member _.RandomPlacement(definition: GrainDefinition<'State, 'Message>) =
        { definition with
            PlacementStrategy = PlacementStrategy.Random
        }

    /// <summary>
    /// Sets the grain placement strategy to HashBased.
    /// Places the grain based on a consistent hash of the grain ID.
    /// In C# CodeGen, this corresponds to the [HashBasedPlacement] attribute on the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <returns>The updated grain definition with HashBased placement.</returns>
    [<CustomOperation("hashBasedPlacement")>]
    member _.HashBasedPlacement(definition: GrainDefinition<'State, 'Message>) =
        { definition with
            PlacementStrategy = PlacementStrategy.HashBased
        }

    /// <summary>Returns the completed grain definition. Validates constraints:
    /// defaultState must be explicitly set, at least one handler must be registered,
    /// and stateless workers cannot use persistent state.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown when validation fails.</exception>
    member _.Run(definition: GrainDefinition<'State, 'Message>) =
        if Object.ReferenceEquals(definition.DefaultState |> box, null)
           && typeof<'State>.IsClass then
            invalidOp
                $"No default state set for grain definition with state type '{typeof<'State>.Name}'. Use 'defaultState' in the grain {{ }} CE. Every grain must have an explicit initial state."

        if not (GrainDefinition.hasAnyHandler definition) then
            invalidOp
                $"No handler registered for grain definition with state type '{typeof<'State>.Name}' and message type '{typeof<'Message>.Name}'. Use 'handle', 'handleCancellable', 'handleWithContext', or 'handleWithContextCancellable' in the grain {{ }} CE."

        if definition.IsStatelessWorker && definition.PersistenceName.IsSome then
            invalidOp
                "Stateless worker grains cannot use persistent state. Remove either 'statelessWorker' or 'persist' from the grain definition."

        definition

/// <summary>
/// The grain computation expression builder instance.
/// Use <c>grain { ... }</c> to define grain behavior declaratively.
/// </summary>
[<AutoOpen>]
module GrainBuilderInstance =
    /// <summary>
    /// Computation expression for defining grain behavior.
    /// Supports custom operations: defaultState, handle, handleCancellable, handleWithContext,
    /// handleWithContextCancellable, handleWithServices, handleWithServicesCancellable, persist,
    /// additionalState, onActivate, onDeactivate, onReminder, onTimer,
    /// preferLocalPlacement, randomPlacement, hashBasedPlacement.
    /// </summary>
    let grain = GrainBuilder()
