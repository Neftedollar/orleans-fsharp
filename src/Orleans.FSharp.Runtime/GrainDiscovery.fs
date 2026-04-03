namespace Orleans.FSharp.Runtime

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open Orleans.Storage
open Orleans.Timers
open Orleans.FSharp

/// <summary>
/// A simple implementation of IGrainState used internally to bridge IGrainStorage
/// calls for named persistent states.
/// </summary>
type internal SimpleGrainState<'T>(initialValue: 'T) =
    let mutable state: 'T = initialValue
    let mutable etag: string = null
    let mutable recordExists = false

    interface Orleans.IGrainState<'T> with
        member _.State
            with get () = state
            and set v = state <- v

        member _.ETag
            with get () = etag
            and set v = etag <- v

        member _.RecordExists
            with get () = recordExists
            and set v = recordExists <- v

/// <summary>
/// A wrapper that provides IPersistentState-like access to a named state stored via IGrainStorage.
/// Used internally by FSharpGrain to support multiple named persistent states declared
/// via the 'additionalState' CE keyword.
/// </summary>
type NamedPersistentState<'T>(storage: IGrainStorage, grainId: GrainId, stateName: string, defaultValue: 'T) =
    let grainState = SimpleGrainState<'T>(defaultValue)
    let iGrainState = grainState :> Orleans.IGrainState<'T>

    /// <summary>Gets or sets the current in-memory state value.</summary>
    member _.State
        with get () = iGrainState.State
        and set v = iGrainState.State <- v

    /// <summary>Returns true if a record was found in storage during the last read.</summary>
    member _.RecordExists = iGrainState.RecordExists

    /// <summary>Gets the ETag for concurrency control.</summary>
    member _.Etag = iGrainState.ETag

    /// <summary>Reads the state from storage.</summary>
    member _.ReadStateAsync() : Task =
        storage.ReadStateAsync(stateName, grainId, iGrainState)

    /// <summary>Writes the current state value to storage.</summary>
    member _.WriteStateAsync() : Task =
        storage.WriteStateAsync(stateName, grainId, iGrainState)

    /// <summary>Clears the state from storage.</summary>
    member _.ClearStateAsync() : Task =
        storage.ClearStateAsync(stateName, grainId, iGrainState)

    interface IPersistentState<'T> with
        member this.State
            with get () = this.State
            and set v = this.State <- v

    interface Orleans.Core.IStorage with
        member this.Etag = this.Etag
        member this.RecordExists = this.RecordExists
        member this.ReadStateAsync() = this.ReadStateAsync()
        member this.WriteStateAsync() = this.WriteStateAsync()
        member this.ClearStateAsync() = this.ClearStateAsync()
        member this.ReadStateAsync(_ct) = this.ReadStateAsync()
        member this.WriteStateAsync(_ct) = this.WriteStateAsync()
        member this.ClearStateAsync(_ct) = this.ClearStateAsync()

/// <summary>
/// A generic grain implementation that bridges F# GrainDefinition handlers to the Orleans runtime.
/// Orleans instantiates this grain class, which delegates all behavior to the registered GrainDefinition.
/// </summary>
/// <typeparam name="'State">The type of the grain's state.</typeparam>
/// <typeparam name="'Message">The type of messages the grain handles.</typeparam>
type FSharpGrain<'State, 'Message>
    (
        definition: GrainDefinition<'State, 'Message>,
        [<PersistentState("state", "Default")>] persistentState: IPersistentState<'State>,
        logger: ILogger<FSharpGrain<'State, 'Message>>
    ) =
    inherit Grain()

    let mutable currentState = definition.DefaultState.Value
    let mutable additionalStates: Map<string, obj> = Map.empty

    /// <summary>Internal bridge for the protected DelayDeactivation method, callable from lambdas.</summary>
    member internal this.InternalDelayDeactivation(delay: System.TimeSpan) =
        this.DelayDeactivation(delay)

    /// <summary>
    /// Called when the grain is activated. Restores persisted state and runs the onActivate hook.
    /// Initializes any additional named persistent states declared in the grain definition.
    /// Emits a structured log entry with grain context.
    /// </summary>
    override this.OnActivateAsync(ct: CancellationToken) =
        // Capture protected members before entering task CE (closure can't access protected members)
        let sp = this.ServiceProvider
        let grainId = this.GetGrainId()

        task {
            ct.ThrowIfCancellationRequested()

            if persistentState.RecordExists then
                currentState <- persistentState.State

            // Initialize additional named persistent states
            if not definition.AdditionalStates.IsEmpty then
                for KeyValue(name, spec) in definition.AdditionalStates do
                    let storage =
                        sp.GetKeyedService<Orleans.Storage.IGrainStorage>(spec.StorageName)

                    if isNull (box storage) then
                        invalidOp $"Storage provider '{spec.StorageName}' not found for additional state '{name}'. Ensure it is registered in the silo configuration."

                    // Create a typed wrapper via reflection to preserve the generic type
                    let wrapperType = typedefof<NamedPersistentState<_>>.MakeGenericType(spec.StateType)
                    let wrapper = Activator.CreateInstance(wrapperType, storage, grainId, name, spec.DefaultValue)

                    // Read existing state
                    let readMethod = wrapperType.GetMethod("ReadStateAsync")
                    do! (readMethod.Invoke(wrapper, [||]) :?> Task)

                    additionalStates <- additionalStates |> Map.add name wrapper

            match definition.OnActivate with
            | Some f ->
                ct.ThrowIfCancellationRequested()
                let! newState = f currentState
                currentState <- newState
            | None -> ()

            // Register declarative timers
            for KeyValue(name, (dueTime, period, handler)) in definition.TimerHandlers do
                this.RegisterGrainTimer(
                    (fun (_state: obj) (_ct: CancellationToken) ->
                        task {
                            let! newState = handler currentState
                            currentState <- newState

                            match definition.PersistenceName with
                            | Some _ ->
                                persistentState.State <- currentState
                                do! persistentState.WriteStateAsync()
                            | None -> ()
                        }
                        :> Task),
                    (null: obj),
                    Orleans.Runtime.GrainTimerCreationOptions(DueTime = dueTime, Period = period)
                )
                |> ignore

            Log.logInfo
                logger
                "Grain {GrainType} activated with state type {StateType} {GrainId}"
                [| box (this.GetGrainId().Type.ToString()); box (typeof<'State>.Name); box (this.GetGrainId().ToString()) |]
        }

    /// <summary>
    /// Called when the grain is being deactivated. Runs the onDeactivate hook.
    /// Emits a structured log entry with grain context.
    /// </summary>
    override this.OnDeactivateAsync(reason: DeactivationReason, ct: CancellationToken) =
        task {
            ct.ThrowIfCancellationRequested()

            match definition.OnDeactivate with
            | Some f -> do! f currentState
            | None -> ()

            Log.logInfo
                logger
                "Grain {GrainType} deactivated. Reason: {DeactivationReason} {GrainId}"
                [| box (this.GetGrainId().Type.ToString()); box reason.Description; box (this.GetGrainId().ToString()) |]
        }

    /// <summary>
    /// Participates in the grain lifecycle by subscribing hooks declared in the GrainDefinition.
    /// Each hook is registered at its specified lifecycle stage and executed during grain activation.
    /// </summary>
    member _.Participate(lifecycle: IGrainLifecycle) =
        for KeyValue(stage, hooks) in definition.LifecycleHooks do
            for hook in hooks do
                lifecycle.Subscribe(
                    typeof<FSharpGrain<'State, 'Message>>.FullName,
                    stage,
                    (fun ct -> hook ct :> Task))
                |> ignore

    interface ILifecycleParticipant<IGrainLifecycle> with
        member this.Participate(lifecycle) = this.Participate(lifecycle)

    /// <summary>
    /// Handles an incoming message by delegating to the GrainDefinition handler.
    /// Updates state and persists it if a storage provider is configured.
    /// Passes a GrainContext to context-aware handlers for grain-to-grain communication.
    /// Propagates the current correlation ID across the grain call.
    /// Accepts an optional CancellationToken that is forwarded to cancellable handlers.
    /// </summary>
    /// <param name="message">The message to handle.</param>
    /// <param name="ct">Optional cancellation token for cooperative cancellation.</param>
    /// <returns>A boxed result value from the handler.</returns>
    /// <summary>Extracts the primary key from a GrainId as a boxed value (string, Guid, or int64).</summary>
    static member private ExtractPrimaryKey(grainId: GrainId) : obj option =
        try
            let key = grainId.Key.ToString()
            // Try parsing as Guid first
            match System.Guid.TryParse(key) with
            | true, guid -> Some(box guid)
            | false, _ ->
                // Try parsing as int64
                match System.Int64.TryParse(key) with
                | true, i -> Some(box i)
                | false, _ ->
                    // Default to string
                    Some(box key)
        with _ -> None

    member this.HandleMessage(message: 'Message, [<System.Runtime.InteropServices.Optional; System.Runtime.InteropServices.DefaultParameterValue(CancellationToken())>] ct: CancellationToken) : Task<obj> =
        // Capture protected members before entering task CE
        let sp = this.ServiceProvider
        let gf = this.GrainFactory
        let gid = this.GetGrainId()
        let ctx: GrainContext =
            {
                GrainFactory = gf
                ServiceProvider = sp
                States = additionalStates
                DeactivateOnIdle = Some(fun () -> (this :> IGrainBase).DeactivateOnIdle())
                DelayDeactivation = Some(fun delay -> this.InternalDelayDeactivation(delay))
                GrainId = Some gid
                PrimaryKey = FSharpGrain<'State, 'Message>.ExtractPrimaryKey(gid)
            }
        let handler = GrainDefinition.getCancellableContextHandler definition

        task {
            Log.logDebug
                logger
                "Grain {GrainType} handling message {MessageType} {GrainId}"
                [| box (this.GetGrainId().Type.ToString()); box (typeof<'Message>.Name); box (this.GetGrainId().ToString()) |]

            let! (newState, result) = handler ctx currentState message ct
            currentState <- newState

            match definition.PersistenceName with
            | Some _ ->
                persistentState.State <- currentState
                do! persistentState.WriteStateAsync()
            | None -> ()

            return result
        }

    /// <summary>
    /// Gets the current state of the grain (for testing/diagnostics).
    /// </summary>
    member _.CurrentState = currentState

    interface IFSharpGrain with
        /// <summary>
        /// Receives a boxed message, downcasts to the grain's typed message, and dispatches.
        /// This is the universal entry point used by IFSharpGrain proxies.
        /// </summary>
        member this.HandleMessage(message: obj) : Task<obj> =
            let typedMsg = message :?> 'Message
            this.HandleMessage(typedMsg)

    interface IRemindable with

        /// <summary>
        /// Receives a reminder tick and delegates to the registered handler by name.
        /// If no handler is registered for the given reminder name, logs a warning.
        /// If the handler throws, the exception is caught, logged, and the reminder continues to fire.
        /// </summary>
        member this.ReceiveReminder(reminderName: string, status: TickStatus) : Task =
            task {
                match definition.ReminderHandlers |> Map.tryFind reminderName with
                | Some handler ->
                    try
                        let! newState = handler currentState reminderName status
                        currentState <- newState

                        match definition.PersistenceName with
                        | Some _ ->
                            persistentState.State <- currentState
                            do! persistentState.WriteStateAsync()
                        | None -> ()
                    with ex ->
                        Log.logError
                            logger
                            ex
                            "Reminder handler {ReminderName} threw exception on grain {GrainType} {GrainId}"
                            [|
                                box reminderName
                                box (this.GetGrainId().Type.ToString())
                                box (this.GetGrainId().ToString())
                            |]
                | None ->
                    Log.logWarning
                        logger
                        "No reminder handler registered for {ReminderName} on grain {GrainType} {GrainId}"
                        [|
                            box reminderName
                            box (this.GetGrainId().Type.ToString())
                            box (this.GetGrainId().ToString())
                        |]
            }

/// <summary>
/// Extension methods for ISiloBuilder to register F# grain definitions with the Orleans runtime.
/// </summary>
[<AutoOpen>]
module SiloBuilderExtensions =

    /// <summary>
    /// Internal registry of grain definitions, keyed by grain interface type.
    /// Used to detect duplicate registrations.
    /// </summary>
    type GrainRegistry() =
        let mutable registrations: Map<string, Type> = Map.empty

        /// <summary>
        /// Registers a grain definition key, throwing if a duplicate is detected.
        /// </summary>
        /// <param name="key">The grain interface type key.</param>
        /// <param name="stateType">The state type being registered.</param>
        member _.Register(key: string, stateType: Type) =
            match registrations |> Map.tryFind key with
            | Some existingType ->
                invalidOp
                    $"Duplicate grain registration for key '{key}'. State type '{existingType.Name}' is already registered. Cannot register state type '{stateType.Name}' for the same key."
            | None -> registrations <- registrations |> Map.add key stateType

        /// <summary>Gets the current set of registrations.</summary>
        member _.Registrations = registrations

    /// <summary>
    /// Runtime dispatcher for the universal <c>FSharpGrain.ref</c> pattern.
    /// Aggregates all <c>AddFSharpGrain&lt;State, Message&gt;</c> registrations and routes
    /// incoming messages to the correct F# handler by inspecting the message's runtime type.
    /// Implements <see cref="IUniversalGrainHandler"/> so that <c>FSharpGrainImpl</c> (in
    /// the C# Abstractions project) can dispatch without referencing F#-specific types.
    /// </summary>
    type UniversalGrainHandlerRegistry() =
        // Keyed by message type full name (Type.FullName of the 'Message type parameter).
        let mutable handlers: Map<string, obj option -> obj -> Task<GrainDispatchResult>> =
            Map.empty

        let mutable defaults: Map<string, obj> = Map.empty

        /// <summary>
        /// Registers a grain definition. Called once per <c>AddFSharpGrain&lt;S,M&gt;</c> invocation.
        /// Not thread-safe — must be called only during silo startup (single-threaded configuration phase).
        /// </summary>
        /// <remarks>
        /// F# discriminated union cases with fields compile to nested types that inherit from the DU
        /// base type (e.g. <c>StringMsg+Append</c> inherits from <c>StringMsg</c>). The dispatch key
        /// is registered for both the base DU type AND all its directly-nested subtypes so that
        /// field-carrying DU cases are routed to the correct handler.
        /// </remarks>
        member _.Register<'State, 'Message>(definition: GrainDefinition<'State, 'Message>) : unit =
            let msgBaseKey = typeof<'Message>.FullName

            // Guard against duplicate message type registrations.
            // Each message type can only map to one (State, handler) pair because the
            // universal dispatcher keys solely on the message's runtime type.
            if handlers |> Map.containsKey msgBaseKey then
                invalidOp
                    $"Duplicate handler registration: message type '{msgBaseKey}' is already registered. \
Each message type can only be registered with one FSharpGrain definition. \
Use distinct command/message types for each grain."

            let simpleHandler = GrainDefinition.getHandler definition

            let h (state: obj option) (msg: obj) : Task<GrainDispatchResult> =
                task {
                    let typedState =
                        match state with
                        | None ->
                            match definition.DefaultState with
                            | Some d -> d
                            | None ->
                                Unchecked.defaultof<'State>
                        | Some s -> s :?> 'State

                    let typedMsg = msg :?> 'Message
                    let! (newState, result) = simpleHandler typedState typedMsg
                    return GrainDispatchResult(box newState, result)
                }

            // Register handler under the DU base type key.
            handlers <- Map.add msgBaseKey h handlers

            // Also register under each nested type that is assignable to 'Message.
            // F# DU cases with fields compile to PUBLIC nested subclasses (e.g. TextCommand+Append).
            // F# nullary DU cases in a mixed DU compile to PRIVATE nested subclasses (e.g. TextCommand+_GetText).
            // We need BindingFlags.NonPublic to capture those private case types.
            let nestedFlags = BindingFlags.Public ||| BindingFlags.NonPublic
            for nestedType in typeof<'Message>.GetNestedTypes(nestedFlags) do
                if typeof<'Message>.IsAssignableFrom(nestedType) then
                    handlers <- Map.add nestedType.FullName h handlers

            match definition.DefaultState with
            | Some d ->
                defaults <- Map.add msgBaseKey (box d) defaults
                // Mirror default under nested case types as well.
                for nestedType in typeof<'Message>.GetNestedTypes(nestedFlags) do
                    if typeof<'Message>.IsAssignableFrom(nestedType) then
                        defaults <- Map.add nestedType.FullName (box d) defaults
            | None -> ()

        interface IUniversalGrainHandler with

            /// <inheritdoc/>
            member _.GetDefaultState(messageType: Type) : obj =
                defaults |> Map.tryFind messageType.FullName |> Option.defaultValue null

            /// <inheritdoc/>
            member _.Handle(currentState: obj, message: obj) : Task<GrainDispatchResult> =
                let key = message.GetType().FullName

                match handlers |> Map.tryFind key with
                | None ->
                    raise (
                        InvalidOperationException(
                            $"No handler registered for message type '{key}'. "
                            + "Ensure AddFSharpGrain<State, Message>() was called in the silo configurator."
                        )
                    )
                | Some h ->
                    let state = if isNull currentState then None else Some currentState
                    h state message

    type IServiceCollection with

        /// <summary>
        /// Registers an F# GrainDefinition as a singleton service that the FSharpGrain dispatcher can resolve.
        /// Also wires the definition into the universal <see cref="IUniversalGrainHandler"/> so that
        /// <c>FSharpGrain.ref&lt;State, Message&gt;</c> works without per-grain C# stubs.
        /// </summary>
        /// <param name="definition">The grain definition to register.</param>
        /// <returns>The service collection for chaining.</returns>
        member services.AddFSharpGrain<'State, 'Message>(definition: GrainDefinition<'State, 'Message>) : IServiceCollection =
            let key = $"{typeof<'State>.FullName}+{typeof<'Message>.FullName}"

            // ── GrainRegistry (duplicate-check tracking) ──────────────────────────
            let registry =
                let existing =
                    services
                    |> Seq.tryFind (fun sd -> sd.ServiceType = typeof<GrainRegistry>)

                match existing with
                | Some sd ->
                    match sd.ImplementationInstance with
                    | :? GrainRegistry as r -> r
                    | _ -> invalidOp "GrainRegistry service was registered but ImplementationInstance is not a GrainRegistry"
                | None ->
                    let r = GrainRegistry()
                    services.AddSingleton<GrainRegistry>(r) |> ignore
                    r

            registry.Register(key, typeof<'State>)

            // ── UniversalGrainHandlerRegistry (IUniversalGrainHandler impl) ───────
            let handlerRegistry =
                let existing =
                    services
                    |> Seq.tryFind (fun sd -> sd.ServiceType = typeof<UniversalGrainHandlerRegistry>)

                match existing with
                | Some sd ->
                    match sd.ImplementationInstance with
                    | :? UniversalGrainHandlerRegistry as r -> r
                    | _ -> invalidOp "UniversalGrainHandlerRegistry service was registered but ImplementationInstance is unexpected type"
                | None ->
                    let r = UniversalGrainHandlerRegistry()
                    // Register as both its own type (for mutation access during startup)
                    // and as IUniversalGrainHandler (for injection into FSharpGrainImpl).
                    services.AddSingleton<UniversalGrainHandlerRegistry>(r) |> ignore
                    services.AddSingleton<IUniversalGrainHandler>(r) |> ignore
                    r

            handlerRegistry.Register<'State, 'Message>(definition)

            // ── FSharpBinaryCodec (idempotent — only once per container) ──────────
            // Required so F# DUs / records / options are serialised correctly when
            // passed as `obj` through the universal grain dispatch layer.  We guard
            // with a presence-check so multiple AddFSharpGrain<_,_> calls don't
            // add redundant registrations.
            let alreadyRegistered =
                services
                |> Seq.exists (fun sd -> sd.ServiceType = typeof<Orleans.FSharp.FSharpBinaryCodec>)

            if not alreadyRegistered then
                Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                    services,
                    System.Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
                        Orleans.FSharp.FSharpBinaryCodecRegistration.addToSerializerBuilder serializerBuilder
                        |> ignore))
                |> ignore

            services.AddSingleton<GrainDefinition<'State, 'Message>>(definition)
