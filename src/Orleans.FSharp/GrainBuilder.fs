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
    /// <summary>Places the grain on the silo with the fewest activations. In C# CodeGen, maps to [ActivationCountBasedPlacement].</summary>
    | ActivationCountBased
    /// <summary>Places the grain based on resource usage metrics (CPU, memory, etc.). In C# CodeGen, maps to [ResourceOptimizedPlacement].</summary>
    | ResourceOptimized
    /// <summary>Places the grain on silos with the specified role. In C# CodeGen, maps to [SiloRoleBasedPlacement].</summary>
    | SiloRoleBased of role: string
    /// <summary>Uses a custom placement strategy type. The type must implement IPlacementStrategy. In C# CodeGen, the strategy attribute is applied to the grain class.</summary>
    | Custom of strategyType: Type

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
        /// <summary>Requests the runtime to deactivate this grain when idle.
        /// Set by the runtime grain host; None when running outside the runtime (e.g., unit tests).</summary>
        DeactivateOnIdle: (unit -> unit) option
        /// <summary>Instructs the runtime to delay deactivation of this grain for at least the specified duration.
        /// Set by the runtime grain host; None when running outside the runtime (e.g., unit tests).</summary>
        DelayDeactivation: (TimeSpan -> unit) option
        /// <summary>The unique GrainId of this grain activation, or None when running outside the runtime.</summary>
        GrainId: GrainId option
        /// <summary>The primary key of the grain (string, Guid, or int64), boxed, or None when running outside the runtime.</summary>
        PrimaryKey: obj option
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
    /// Requests the runtime to deactivate this grain activation when it becomes idle.
    /// Must be called from within a grain handler running in the Orleans runtime.
    /// </summary>
    /// <param name="ctx">The grain context providing access to grain lifecycle control.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when called outside the Orleans runtime.</exception>
    let deactivateOnIdle (ctx: GrainContext) : unit =
        match ctx.DeactivateOnIdle with
        | Some f -> f ()
        | None ->
            invalidOp "DeactivateOnIdle is not available outside the Orleans runtime. Ensure the grain is running inside a silo."

    /// <summary>
    /// Instructs the runtime to delay deactivation of this grain activation for at least the specified duration.
    /// Useful for keeping a grain alive after handling a message to avoid repeated activation overhead.
    /// Must be called from within a grain handler running in the Orleans runtime.
    /// </summary>
    /// <param name="ctx">The grain context providing access to grain lifecycle control.</param>
    /// <param name="delay">The minimum duration to delay deactivation.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when called outside the Orleans runtime.</exception>
    let delayDeactivation (ctx: GrainContext) (delay: TimeSpan) : unit =
        match ctx.DelayDeactivation with
        | Some f -> f delay
        | None ->
            invalidOp "DelayDeactivation is not available outside the Orleans runtime. Ensure the grain is running inside a silo."

    /// <summary>
    /// Gets the GrainId of this grain activation.
    /// Throws InvalidOperationException when called outside the Orleans runtime.
    /// </summary>
    /// <param name="ctx">The grain context providing access to grain identity.</param>
    /// <returns>The GrainId of this grain.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when called outside the Orleans runtime.</exception>
    let grainId (ctx: GrainContext) : GrainId =
        match ctx.GrainId with
        | Some id -> id
        | None ->
            invalidOp "GrainId is not available outside the Orleans runtime. Ensure the grain is running inside a silo."

    /// <summary>
    /// Gets the primary key of this grain as a string.
    /// Throws InvalidOperationException when the primary key is not available or is not a string.
    /// </summary>
    /// <param name="ctx">The grain context providing access to grain identity.</param>
    /// <returns>The string primary key.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when called outside the Orleans runtime or key is not a string.</exception>
    let primaryKeyString (ctx: GrainContext) : string =
        match ctx.PrimaryKey with
        | Some (:? string as s) -> s
        | Some other ->
            invalidOp $"Primary key is not a string. Actual type: '{other.GetType().Name}'."
        | None ->
            invalidOp "PrimaryKey is not available outside the Orleans runtime. Ensure the grain is running inside a silo."

    /// <summary>
    /// Gets the primary key of this grain as a Guid.
    /// Throws InvalidOperationException when the primary key is not available or is not a Guid.
    /// </summary>
    /// <param name="ctx">The grain context providing access to grain identity.</param>
    /// <returns>The Guid primary key.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when called outside the Orleans runtime or key is not a Guid.</exception>
    let primaryKeyGuid (ctx: GrainContext) : Guid =
        match ctx.PrimaryKey with
        | Some (:? Guid as g) -> g
        | Some other ->
            invalidOp $"Primary key is not a Guid. Actual type: '{other.GetType().Name}'."
        | None ->
            invalidOp "PrimaryKey is not available outside the Orleans runtime. Ensure the grain is running inside a silo."

    /// <summary>
    /// Gets the primary key of this grain as an int64.
    /// Throws InvalidOperationException when the primary key is not available or is not an int64.
    /// </summary>
    /// <param name="ctx">The grain context providing access to grain identity.</param>
    /// <returns>The int64 primary key.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when called outside the Orleans runtime or key is not an int64.</exception>
    let primaryKeyInt64 (ctx: GrainContext) : int64 =
        match ctx.PrimaryKey with
        | Some (:? int64 as i) -> i
        | Some other ->
            invalidOp $"Primary key is not an int64. Actual type: '{other.GetType().Name}'."
        | None ->
            invalidOp "PrimaryKey is not available outside the Orleans runtime. Ensure the grain is running inside a silo."

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
    /// Gets a type-safe reference to a grain by compound GUID+string key from within a grain handler.
    /// </summary>
    /// <param name="ctx">The grain context providing access to the grain factory.</param>
    /// <param name="guid">The GUID part of the compound key.</param>
    /// <param name="ext">The string extension part of the compound key.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithGuidCompoundKey.</typeparam>
    /// <returns>A type-safe grain reference with a CompoundGuidKey.</returns>
    let getGrainByGuidCompound<'TInterface when 'TInterface :> IGrainWithGuidCompoundKey>
        (ctx: GrainContext)
        (guid: Guid)
        (ext: string)
        : GrainRef<'TInterface, CompoundGuidKey> =
        GrainRef.ofGuidCompound<'TInterface> ctx.GrainFactory guid ext

    /// <summary>
    /// Gets a type-safe reference to a grain by compound int64+string key from within a grain handler.
    /// </summary>
    /// <param name="ctx">The grain context providing access to the grain factory.</param>
    /// <param name="key">The int64 part of the compound key.</param>
    /// <param name="ext">The string extension part of the compound key.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithIntegerCompoundKey.</typeparam>
    /// <returns>A type-safe grain reference with a CompoundIntKey.</returns>
    let getGrainByIntCompound<'TInterface when 'TInterface :> IGrainWithIntegerCompoundKey>
        (ctx: GrainContext)
        (key: int64)
        (ext: string)
        : GrainRef<'TInterface, CompoundIntKey> =
        GrainRef.ofIntCompound<'TInterface> ctx.GrainFactory key ext

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
        DefaultState: 'State option
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
        /// <summary>Set of method names that should be marked with the [OneWay] attribute in C# CodeGen.
        /// One-way methods are fire-and-forget: the caller does not wait for the grain to finish processing.</summary>
        OneWayMethods: Set<string>
        /// <summary>Set of method names that should be marked with [ReadOnly] in C# CodeGen.
        /// Read-only methods allow interleaving for read-only calls, improving throughput for non-mutating operations.</summary>
        ReadOnlyMethods: Set<string>
        /// <summary>Optional name of a static predicate method for custom reentrancy decisions.
        /// In C# CodeGen, maps to [MayInterleave("PredicateMethodName")] on the grain class.
        /// The predicate receives the incoming InvokeMethodRequest and returns bool.</summary>
        MayInterleavePredicate: string option
        /// <summary>Lifecycle hooks keyed by GrainLifecycleStage (int).
        /// Each hook is invoked during the corresponding lifecycle stage with a CancellationToken.
        /// Standard stages: First=2000, SetupState=4000, Activate=6000, Last=int.MaxValue.</summary>
        LifecycleHooks: Map<int, (CancellationToken -> Task<unit>) list>
        /// <summary>Implicit stream subscriptions mapping stream namespace to event handler functions.
        /// In C# CodeGen, maps to [ImplicitStreamSubscription("namespace")] on the grain class.
        /// Each handler receives the current state and a stream event (boxed), and returns the new state.</summary>
        ImplicitSubscriptions: Map<string, 'State -> obj -> Task<'State>>
        /// <summary>Per-grain deactivation timeout override. When set, configures the idle timeout
        /// before this grain type is deactivated. In C# CodeGen, maps to [CollectionAgeLimit] attribute.</summary>
        DeactivationTimeout: TimeSpan option
        /// <summary>Custom grain type name. In C# CodeGen, maps to [GrainType("name")] attribute on the grain class.</summary>
        GrainTypeName: string option
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
            DefaultState = None
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
            OneWayMethods = Set.empty
            ReadOnlyMethods = Set.empty
            MayInterleavePredicate = None
            LifecycleHooks = Map.empty
            ImplicitSubscriptions = Map.empty
            DeactivationTimeout = None
            GrainTypeName = None
        }

    /// <summary>
    /// Sets the initial state of the grain.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="value">The initial state value.</param>
    /// <returns>The updated grain definition with the default state set.</returns>
    [<CustomOperation("defaultState")>]
    member _.DefaultState(definition: GrainDefinition<'State, 'Message>, value: 'State) =
        { definition with DefaultState = Some value }

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

    /// <summary>
    /// Marks a specific method as one-way (fire-and-forget).
    /// In C# CodeGen, this corresponds to the [OneWay] attribute on the interface method.
    /// One-way methods return immediately to the caller without waiting for the grain to finish processing.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="methodName">The name of the method to mark as one-way.</param>
    /// <returns>The updated grain definition with the method added to the one-way set.</returns>
    [<CustomOperation("oneWay")>]
    member _.OneWay(definition: GrainDefinition<'State, 'Message>, methodName: string) =
        { definition with
            OneWayMethods = definition.OneWayMethods |> Set.add methodName
        }

    /// <summary>
    /// Marks a specific interface method as read-only, allowing it to be interleaved (processed concurrently)
    /// even on non-reentrant grains. Read-only methods must not modify grain state.
    /// In C# CodeGen, this corresponds to the [ReadOnly] attribute on the interface method.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="methodName">The name of the method to mark as read-only.</param>
    /// <returns>The updated grain definition with the method added to the read-only set.</returns>
    [<CustomOperation("readOnly")>]
    member _.ReadOnly(definition: GrainDefinition<'State, 'Message>, methodName: string) =
        { definition with
            ReadOnlyMethods = definition.ReadOnlyMethods |> Set.add methodName
        }

    /// <summary>
    /// Sets a custom reentrancy predicate method name for the grain.
    /// The named static method receives an InvokeMethodRequest and returns bool to decide
    /// whether the incoming call may interleave with the current execution.
    /// In C# CodeGen, this corresponds to [MayInterleave("PredicateMethodName")] on the grain class.
    /// Note: call chain reentrancy (grain A -> grain B -> grain A) is automatic when the grain is [Reentrant].
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="predicateMethodName">The name of the static predicate method.</param>
    /// <returns>The updated grain definition with the reentrancy predicate set.</returns>
    [<CustomOperation("mayInterleave")>]
    member _.MayInterleave(definition: GrainDefinition<'State, 'Message>, predicateMethodName: string) =
        { definition with
            MayInterleavePredicate = Some predicateMethodName
        }

    /// <summary>
    /// Sets the grain placement strategy to ActivationCountBased.
    /// Places the grain on the silo with the fewest active grain activations.
    /// In C# CodeGen, this corresponds to the [ActivationCountBasedPlacement] attribute on the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <returns>The updated grain definition with ActivationCountBased placement.</returns>
    [<CustomOperation("activationCountPlacement")>]
    member _.ActivationCountPlacement(definition: GrainDefinition<'State, 'Message>) =
        { definition with
            PlacementStrategy = PlacementStrategy.ActivationCountBased
        }

    /// <summary>
    /// Sets the grain placement strategy to ResourceOptimized.
    /// Places the grain based on silo resource usage metrics (CPU, memory, etc.).
    /// In C# CodeGen, this corresponds to the [ResourceOptimizedPlacement] attribute on the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <returns>The updated grain definition with ResourceOptimized placement.</returns>
    [<CustomOperation("resourceOptimizedPlacement")>]
    member _.ResourceOptimizedPlacement(definition: GrainDefinition<'State, 'Message>) =
        { definition with
            PlacementStrategy = PlacementStrategy.ResourceOptimized
        }

    /// <summary>
    /// Sets the grain placement strategy to SiloRoleBased with the specified role.
    /// Places the grain only on silos configured with the given role.
    /// In C# CodeGen, this corresponds to the [SiloRoleBasedPlacement] attribute with the role parameter.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="role">The silo role name to target.</param>
    /// <returns>The updated grain definition with SiloRoleBased placement.</returns>
    [<CustomOperation("siloRolePlacement")>]
    member _.SiloRolePlacement(definition: GrainDefinition<'State, 'Message>, role: string) =
        { definition with
            PlacementStrategy = PlacementStrategy.SiloRoleBased role
        }

    /// <summary>
    /// Sets the grain placement strategy to a custom type.
    /// The type must implement IPlacementStrategy.
    /// In C# CodeGen, the custom strategy attribute is applied to the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="strategyType">The System.Type of the custom placement strategy.</param>
    /// <returns>The updated grain definition with Custom placement.</returns>
    [<CustomOperation("customPlacement")>]
    member _.CustomPlacement(definition: GrainDefinition<'State, 'Message>, strategyType: Type) =
        { definition with
            PlacementStrategy = PlacementStrategy.Custom strategyType
        }

    /// <summary>
    /// Registers a lifecycle hook at the specified grain lifecycle stage.
    /// The hook is invoked with a CancellationToken during grain activation.
    /// Standard stages: GrainLifecycleStage.First (2000), SetupState (4000), Activate (6000), Last (int.MaxValue).
    /// Multiple hooks at the same stage are executed in registration order.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="stage">The lifecycle stage (int) at which the hook runs.</param>
    /// <param name="hook">The async hook function receiving a CancellationToken.</param>
    /// <returns>The updated grain definition with the lifecycle hook registered.</returns>
    [<CustomOperation("onLifecycleStage")>]
    member _.OnLifecycleStage
        (
            definition: GrainDefinition<'State, 'Message>,
            stage: int,
            hook: CancellationToken -> Task<unit>
        ) =
        let existing =
            definition.LifecycleHooks
            |> Map.tryFind stage
            |> Option.defaultValue []

        { definition with
            LifecycleHooks = definition.LifecycleHooks |> Map.add stage (existing @ [ hook ])
        }

    /// <summary>
    /// Declares an implicit stream subscription for the given namespace.
    /// In C# CodeGen, maps to [ImplicitStreamSubscription("namespace")] on the grain class.
    /// The handler receives the current state and a stream event (boxed), and returns the new state.
    /// Multiple implicit subscriptions can be registered for different namespaces.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="ns">The stream namespace to subscribe to.</param>
    /// <param name="handler">The event handler function that transforms state based on the stream event.</param>
    /// <returns>The updated grain definition with the implicit stream subscription registered.</returns>
    [<CustomOperation("implicitStreamSubscription")>]
    member _.ImplicitStreamSubscription
        (
            definition: GrainDefinition<'State, 'Message>,
            ns: string,
            handler: 'State -> obj -> Task<'State>
        ) =
        { definition with
            ImplicitSubscriptions = definition.ImplicitSubscriptions |> Map.add ns handler
        }

    /// <summary>
    /// Sets the per-grain deactivation timeout (idle timeout before this grain type is deactivated).
    /// In C# CodeGen, maps to the [CollectionAgeLimit] attribute on the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="timeout">The deactivation timeout as a TimeSpan.</param>
    /// <returns>The updated grain definition with the deactivation timeout set.</returns>
    [<CustomOperation("deactivationTimeout")>]
    member _.DeactivationTimeout(definition: GrainDefinition<'State, 'Message>, timeout: TimeSpan) =
        { definition with
            DeactivationTimeout = Some timeout
        }

    /// <summary>
    /// Sets a custom grain type name for this grain.
    /// In C# CodeGen, maps to [GrainType("name")] attribute on the grain class.
    /// </summary>
    /// <param name="definition">The current grain definition being built.</param>
    /// <param name="name">The custom grain type name.</param>
    /// <returns>The updated grain definition with the grain type name set.</returns>
    [<CustomOperation("grainType")>]
    member _.GrainType(definition: GrainDefinition<'State, 'Message>, name: string) =
        { definition with
            GrainTypeName = Some name
        }

    /// <summary>Returns the completed grain definition. Validates constraints:
    /// defaultState must be explicitly set, at least one handler must be registered,
    /// and stateless workers cannot use persistent state.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown when validation fails.</exception>
    member _.Run(definition: GrainDefinition<'State, 'Message>) =
        if definition.DefaultState.IsNone then
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
    /// preferLocalPlacement, randomPlacement, hashBasedPlacement, activationCountPlacement,
    /// resourceOptimizedPlacement, siloRolePlacement, customPlacement,
    /// reentrant, interleave, readOnly, mayInterleave, oneWay, onLifecycleStage,
    /// implicitStreamSubscription.
    /// </summary>
    let grain = GrainBuilder()
