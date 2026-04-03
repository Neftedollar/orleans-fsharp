namespace Orleans.FSharp.Runtime

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open Orleans.Timers
open Orleans.FSharp

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

    let mutable currentState = definition.DefaultState

    /// <summary>
    /// Called when the grain is activated. Restores persisted state and runs the onActivate hook.
    /// Emits a structured log entry with grain context.
    /// </summary>
    override this.OnActivateAsync(ct: CancellationToken) =
        task {
            ct.ThrowIfCancellationRequested()

            if persistentState.RecordExists then
                currentState <- persistentState.State

            match definition.OnActivate with
            | Some f ->
                ct.ThrowIfCancellationRequested()
                let! newState = f currentState
                currentState <- newState
            | None -> ()

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
    /// Handles an incoming message by delegating to the GrainDefinition handler.
    /// Updates state and persists it if a storage provider is configured.
    /// Passes a GrainContext to context-aware handlers for grain-to-grain communication.
    /// Propagates the current correlation ID across the grain call.
    /// </summary>
    /// <param name="message">The message to handle.</param>
    /// <returns>A boxed result value from the handler.</returns>
    member this.HandleMessage(message: 'Message) : Task<obj> =
        let ctx: GrainContext = { GrainFactory = this.GrainFactory }
        let handler = GrainDefinition.getContextHandler definition

        task {
            Log.logDebug
                logger
                "Grain {GrainType} handling message {MessageType} {GrainId}"
                [| box (this.GetGrainId().Type.ToString()); box (typeof<'Message>.Name); box (this.GetGrainId().ToString()) |]

            let! (newState, result) = handler ctx currentState message
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

    type IServiceCollection with

        /// <summary>
        /// Registers an F# GrainDefinition as a singleton service that the FSharpGrain dispatcher can resolve.
        /// </summary>
        /// <param name="definition">The grain definition to register.</param>
        /// <returns>The service collection for chaining.</returns>
        member services.AddFSharpGrain<'State, 'Message>(definition: GrainDefinition<'State, 'Message>) : IServiceCollection =
            let key = $"{typeof<'State>.FullName}+{typeof<'Message>.FullName}"

            // Get or create the registry
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
            services.AddSingleton<GrainDefinition<'State, 'Message>>(definition)
