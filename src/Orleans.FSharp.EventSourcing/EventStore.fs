namespace Orleans.FSharp.EventSourcing

open System.Threading.Tasks

/// <summary>
/// Abstraction for event store operations that the C# JournaledGrain implementation provides.
/// The C# grain class implements this interface by delegating to the protected
/// JournaledGrain methods (RaiseEvent, ConfirmEvents, Version).
/// F# command handlers can use this interface when they need to interact with the event store.
/// </summary>
type IEventStoreContext<'Event> =
    /// <summary>
    /// Raises a single event. The event is applied to in-memory state immediately
    /// but not persisted until ConfirmEvents is called.
    /// </summary>
    /// <param name="event">The event to raise.</param>
    abstract RaiseEvent: event: 'Event -> unit

    /// <summary>
    /// Confirms (persists) all previously raised events.
    /// </summary>
    /// <returns>A Task that completes when events are durably stored.</returns>
    abstract ConfirmEvents: unit -> Task

    /// <summary>
    /// Gets the current confirmed event version number.
    /// </summary>
    abstract Version: int

/// <summary>
/// F# helper functions for working with event-sourced grains.
/// These functions operate on the EventSourcedGrainDefinition to process commands
/// and produce events, without directly accessing JournaledGrain protected members.
/// The C# CodeGen grain class calls these functions and bridges to JournaledGrain.
/// </summary>
[<RequireQualifiedAccess>]
module EventStore =

    /// <summary>
    /// Processes a command using the grain definition, returning the list of events produced.
    /// The caller (C# grain) is responsible for raising these events on the JournaledGrain
    /// and calling ConfirmEvents.
    /// </summary>
    /// <param name="definition">The event-sourced grain definition.</param>
    /// <param name="state">The current grain state.</param>
    /// <param name="command">The command to process.</param>
    /// <returns>The list of events produced by the command handler.</returns>
    let processCommand
        (definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>)
        (state: 'State)
        (command: 'Command)
        : 'Event list =
        definition.Handle state command

    /// <summary>
    /// Applies a single event to a state using the definition's apply function.
    /// Called by the C# grain's TransitionState override to delegate to F#.
    /// </summary>
    /// <param name="definition">The event-sourced grain definition.</param>
    /// <param name="state">The current state.</param>
    /// <param name="event">The event to apply.</param>
    /// <returns>The new state after applying the event.</returns>
    let applyEvent
        (definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>)
        (state: 'State)
        (event: 'Event)
        : 'State =
        definition.Apply state event

    /// <summary>
    /// Replays a list of events onto a state using the definition's apply function.
    /// Useful for rebuilding state from event history.
    /// </summary>
    /// <param name="definition">The event-sourced grain definition.</param>
    /// <param name="state">The initial state to fold events into.</param>
    /// <param name="events">The events to replay.</param>
    /// <returns>The state after all events have been applied.</returns>
    let replayEvents
        (definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>)
        (state: 'State)
        (events: 'Event list)
        : 'State =
        events |> List.fold definition.Apply state

    /// <summary>
    /// Evaluates the definition's snapshot strategy to determine whether a snapshot
    /// checkpoint should be written at the given version and state.
    /// </summary>
    /// <remarks>
    /// This helper is intended for custom log-consistency providers (or grain
    /// implementations) that support persisting a state snapshot alongside the event
    /// log. With the built-in <c>LogStorageBasedLogConsistencyProvider</c> there is no
    /// snapshot mechanism, so the result of this function has no runtime effect unless
    /// wired to a provider that can store snapshots.
    /// </remarks>
    /// <param name="definition">The event-sourced grain definition.</param>
    /// <param name="version">The current confirmed event version.</param>
    /// <param name="state">The current confirmed grain state.</param>
    /// <returns><c>true</c> if a snapshot should be written at this point.</returns>
    let shouldSnapshot
        (definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>)
        (version: int)
        (state: 'State)
        : bool =
        match definition.SnapshotStrategy with
        | Never -> false
        | Every n -> n > 0 && version > 0 && version % n = 0
        | Condition predicate -> predicate version state
