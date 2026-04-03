namespace Orleans.FSharp.EventSourcing

/// <summary>
/// Defines the complete specification for an event-sourced F# grain.
/// Captures the initial state, event application function, command handler,
/// and optional log consistency provider configuration.
/// </summary>
/// <typeparam name="'State">The type of the grain's state, rebuilt by folding events.</typeparam>
/// <typeparam name="'Event">The type of events that modify state.</typeparam>
/// <typeparam name="'Command">The type of commands the grain handles.</typeparam>
type EventSourcedGrainDefinition<'State, 'Event, 'Command> =
    {
        /// <summary>The initial state value for the grain when no events have been applied.</summary>
        DefaultState: 'State
        /// <summary>
        /// Pure function that applies a single event to the current state, producing a new state.
        /// Must be deterministic and free of side effects.
        /// </summary>
        Apply: 'State -> 'Event -> 'State
        /// <summary>
        /// Command handler that takes the current state and a command, producing a list of events.
        /// An empty list means the command was rejected or is a query (no state change).
        /// </summary>
        Handle: 'State -> 'Command -> 'Event list
        /// <summary>
        /// Optional name of the Orleans log consistency provider (e.g., "LogStorage", "StateStorage").
        /// When None, the default provider configured on the silo is used.
        /// </summary>
        ConsistencyProvider: string option
    }

/// <summary>
/// Utility functions for working with EventSourcedGrainDefinition values.
/// </summary>
[<RequireQualifiedAccess>]
module EventSourcedGrainDefinition =

    /// <summary>
    /// Applies a sequence of events to a state using the definition's apply function.
    /// This is a pure fold operation useful for replaying event history.
    /// </summary>
    /// <param name="definition">The event-sourced grain definition containing the apply function.</param>
    /// <param name="state">The initial state to fold events into.</param>
    /// <param name="events">The sequence of events to apply.</param>
    /// <returns>The state after all events have been applied.</returns>
    let foldEvents
        (definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>)
        (state: 'State)
        (events: 'Event list)
        : 'State =
        events |> List.fold definition.Apply state

    /// <summary>
    /// Handles a command using the definition, producing events and the resulting state.
    /// First generates events via the Handle function, then folds them onto the current state.
    /// </summary>
    /// <param name="definition">The event-sourced grain definition.</param>
    /// <param name="state">The current state of the grain.</param>
    /// <param name="command">The command to handle.</param>
    /// <returns>A tuple of the new state and the list of events produced.</returns>
    let handleCommand
        (definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>)
        (state: 'State)
        (command: 'Command)
        : 'State * 'Event list =
        let events = definition.Handle state command
        let newState = foldEvents definition state events
        newState, events

/// <summary>
/// Computation expression builder for declaratively defining event-sourced grain behavior.
/// Use the <c>eventSourcedGrain { }</c> syntax with custom operations to build an EventSourcedGrainDefinition.
/// </summary>
/// <example>
/// <code>
/// let myGrain = eventSourcedGrain {
///     defaultState { Balance = 0m }
///     apply (fun state event -> match event with Deposited a -> { state with Balance = state.Balance + a })
///     handle (fun state cmd -> match cmd with Credit a -> [ Deposited a ])
///     logConsistencyProvider "LogStorage"
/// }
/// </code>
/// </example>
type EventSourcedGrainBuilder() =

    /// <summary>Yields the initial empty event-sourced grain definition.</summary>
    member _.Yield(_: unit) : EventSourcedGrainDefinition<'State, 'Event, 'Command> =
        {
            DefaultState = Unchecked.defaultof<'State>
            Apply = fun state _event -> state
            Handle = fun _state _command -> []
            ConsistencyProvider = None
        }

    /// <summary>
    /// Sets the initial state of the event-sourced grain.
    /// This is the state before any events have been applied.
    /// </summary>
    /// <param name="definition">The current definition being built.</param>
    /// <param name="state">The initial state value.</param>
    /// <returns>The updated definition with the default state set.</returns>
    [<CustomOperation("defaultState")>]
    member _.DefaultState(definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>, state: 'State) =
        { definition with DefaultState = state }

    /// <summary>
    /// Sets the event application function for the grain.
    /// This pure function folds a single event into the current state.
    /// Must be deterministic and free of side effects.
    /// </summary>
    /// <param name="definition">The current definition being built.</param>
    /// <param name="f">The event application function: state -> event -> state.</param>
    /// <returns>The updated definition with the apply function set.</returns>
    [<CustomOperation("apply")>]
    member _.Apply
        (
            definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>,
            f: 'State -> 'Event -> 'State
        ) =
        { definition with Apply = f }

    /// <summary>
    /// Sets the command handler function for the grain.
    /// Given the current state and a command, produces a list of events.
    /// An empty list means the command was rejected or is a read-only query.
    /// </summary>
    /// <param name="definition">The current definition being built.</param>
    /// <param name="f">The command handler function: state -> command -> event list.</param>
    /// <returns>The updated definition with the handle function set.</returns>
    [<CustomOperation("handle")>]
    member _.Handle
        (
            definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>,
            f: 'State -> 'Command -> 'Event list
        ) =
        { definition with Handle = f }

    /// <summary>
    /// Sets the name of the Orleans log consistency provider.
    /// Common values are "LogStorage" (log-based) or "StateStorage" (state snapshot-based).
    /// When not specified, the silo's default log consistency provider is used.
    /// </summary>
    /// <param name="definition">The current definition being built.</param>
    /// <param name="name">The provider name.</param>
    /// <returns>The updated definition with the consistency provider set.</returns>
    [<CustomOperation("logConsistencyProvider")>]
    member _.LogConsistencyProvider(definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>, name: string) =
        { definition with
            ConsistencyProvider = Some name
        }

    /// <summary>Returns the completed event-sourced grain definition. Validates that defaultState was set.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown when defaultState was not set for reference types.</exception>
    member _.Run(definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>) =
        if System.Object.ReferenceEquals(definition.DefaultState |> box, null)
           && typeof<'State>.IsClass then
            invalidOp
                $"No default state set for event-sourced grain with state type '{typeof<'State>.Name}'. Use 'defaultState' in the eventSourcedGrain {{ }} CE."

        definition

/// <summary>
/// Module containing the eventSourcedGrain computation expression builder instance.
/// </summary>
[<AutoOpen>]
module EventSourcedGrainBuilderInstance =
    /// <summary>
    /// Computation expression for defining event-sourced grain behavior.
    /// Supports custom operations: defaultState, apply, handle, logConsistencyProvider.
    /// </summary>
    let eventSourcedGrain = EventSourcedGrainBuilder()
