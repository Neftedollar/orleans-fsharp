namespace Orleans.FSharp.EventSourcing

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.EventSourcing
open Orleans.FSharp

/// <summary>
/// A generic grain implementation that bridges F# EventSourcedGrainDefinition handlers
/// to Orleans JournaledGrain. Orleans instantiates this grain class, which delegates
/// all event application and command handling to the registered F# definition.
/// </summary>
/// <typeparam name="'State">The type of the grain's state, rebuilt by folding events.</typeparam>
/// <typeparam name="'Event">The type of events that modify state.</typeparam>
/// <typeparam name="'Command">The type of commands the grain handles.</typeparam>
type FSharpEventSourcedGrain< 'State, 'Event, 'Command
    when 'State: not struct and 'State: (new: unit -> 'State)
    and 'Event: not struct>
    (
        definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>,
        logger: ILogger<FSharpEventSourcedGrain<'State, 'Event, 'Command>>
    ) =
    inherit JournaledGrain<'State, 'Event>()

    /// <summary>
    /// Overrides JournaledGrain transition to delegate event application
    /// to the F# pure apply function defined in the EventSourcedGrainDefinition.
    /// Because JournaledGrain manages the state instance, field values are copied
    /// from the new state back into the existing state object via reflection.
    /// </summary>
    override _.TransitionState(state: 'State, event: 'Event) =
        let newState = definition.Apply state event

        // Copy all mutable fields from newState to state.
        // JournaledGrain manages the state object instance, so we must mutate it in place.
        let stateType = typeof<'State>

        for prop in stateType.GetProperties() do
            if prop.CanWrite then
                let value = prop.GetValue(newState)
                prop.SetValue(state, value)

    /// <summary>Internal bridge for protected State property, callable from closures.</summary>
    member internal this.InternalState = this.State

    /// <summary>Internal bridge for protected RaiseEvent method, callable from closures.</summary>
    member internal this.InternalRaiseEvent(event: 'Event) = this.RaiseEvent(event)

    /// <summary>Internal bridge for protected ConfirmEvents method, callable from closures.</summary>
    member internal this.InternalConfirmEvents() = this.ConfirmEvents()

    /// <summary>
    /// Called when the grain is activated. Logs activation with current state.
    /// </summary>
    override this.OnActivateAsync(_ct: CancellationToken) =
        Log.logInfo
            logger
            "FSharpEventSourcedGrain {GrainType} activated {GrainId}"
            [| box (this.GetGrainId().Type.ToString()); box (this.GetGrainId().ToString()) |]

        Task.CompletedTask

    /// <summary>
    /// Handles a command by generating events via the F# command handler,
    /// raising them on the JournaledGrain, and confirming persistence.
    /// Returns the current state's boxed representation after all events are applied.
    /// </summary>
    /// <param name="command">The command to handle.</param>
    /// <returns>A boxed result (typically the current state or a derived value).</returns>
    member this.HandleCommand(command: 'Command) : Task<obj> =
        // Capture protected members before entering task CE (closure can't access protected members)
        let currentState = this.InternalState
        let raiseEvent = this.InternalRaiseEvent
        let confirmEvents = this.InternalConfirmEvents
        let grainId = this.GetGrainId()

        task {
            let events = definition.Handle currentState command

            for event in events do
                raiseEvent event

            if not events.IsEmpty then
                do! confirmEvents ()

            Log.logDebug
                logger
                "FSharpEventSourcedGrain {GrainType} handled command {GrainId}"
                [| box (grainId.Type.ToString()); box (grainId.ToString()) |]

            return box this.InternalState
        }

/// <summary>
/// Extension methods for IServiceCollection to register F# event-sourced grain definitions.
/// </summary>
[<AutoOpen>]
module EventSourcedGrainSiloBuilderExtensions =

    type IServiceCollection with

        /// <summary>
        /// Registers an F# EventSourcedGrainDefinition as a singleton service that the
        /// FSharpEventSourcedGrain dispatcher can resolve. The grain will be discovered
        /// by Orleans through the generic FSharpEventSourcedGrain type.
        /// </summary>
        /// <param name="definition">The event-sourced grain definition to register.</param>
        /// <returns>The service collection for chaining.</returns>
        member services.AddFSharpEventSourcedGrain< 'State, 'Event, 'Command
            when 'State: not struct and 'State: (new: unit -> 'State)
            and 'Event: not struct>
            (definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>)
            : IServiceCollection =
            services.AddSingleton<EventSourcedGrainDefinition<'State, 'Event, 'Command>>(definition)
