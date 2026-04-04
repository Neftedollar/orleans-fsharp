namespace Orleans.FSharp.EventSourcing

open System
open System.Collections.Concurrent
open System.Collections.Generic
open Orleans.FSharp

// ---------------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------------

/// Boxed dispatch functions for a single event-sourced grain definition.
[<NoEquality; NoComparison>]
type private RegistryEntry =
    { GetDefaultState: unit -> obj
      HandleCommand: obj -> obj -> obj list
      ApplyEvent: obj -> obj -> obj }

// ---------------------------------------------------------------------------
// EventSourcedHandlerRegistry
// ---------------------------------------------------------------------------

/// <summary>
/// F# implementation of <see cref="IEventSourcedHandlerRegistry"/>.
/// Stores boxed dispatch functions for all event-sourced grain definitions registered via
/// <c>AddFSharpEventSourcedGrain</c>.
/// Used by <c>FSharpEventSourcedGrainImpl</c> to handle commands at runtime without
/// per-grain C# stubs.
/// </summary>
/// <remarks>
/// Keyed by the <c>FullName</c> of the command type (for command dispatch) and
/// the event type (for <c>TransitionState</c> replay during activation).
/// Not thread-safe for writes — populate only during silo startup (single-threaded config phase).
/// </remarks>
type EventSourcedHandlerRegistry() =
    let commandHandlers = ConcurrentDictionary<string, RegistryEntry>()
    let eventHandlers   = ConcurrentDictionary<string, RegistryEntry>()

    /// <summary>
    /// Registers a typed grain definition, storing boxed dispatch functions keyed by
    /// command-type and event-type full names.
    /// </summary>
    member _.Register< 'State, 'Event, 'Command
        when 'State : not struct
        and  'State : (new : unit -> 'State)
        and  'Event : not struct >
        (definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>) =

        let entry =
            { GetDefaultState = fun () ->
                definition.DefaultState
                |> Option.map box
                |> Option.defaultWith (fun () ->
                    Activator.CreateInstance(typeof<'State>) |> box)

              HandleCommand = fun state cmd ->
                definition.Handle (unbox<'State> state) (unbox<'Command> cmd)
                |> List.map box

              ApplyEvent = fun state evt ->
                box (definition.Apply (unbox<'State> state) (unbox<'Event> evt)) }

        // Register the top-level DU type and all nested case types (F# DU cases compile
        // as nested classes, so value.GetType() returns the case type, not the DU type).
        let nestedFlags =
            System.Reflection.BindingFlags.Public |||
            System.Reflection.BindingFlags.NonPublic

        let registerType (dict: ConcurrentDictionary<string, RegistryEntry>) (t: Type) =
            if not (isNull t.FullName) then
                dict[t.FullName] <- entry
            for nested in t.GetNestedTypes(nestedFlags) do
                if t.IsAssignableFrom(nested) && not (isNull nested.FullName) then
                    dict[nested.FullName] <- entry

        registerType commandHandlers typeof<'Command>
        registerType eventHandlers   typeof<'Event>

    interface IEventSourcedHandlerRegistry with

        member _.GetDefaultStateByCommandType(commandType: Type) =
            match commandHandlers.TryGetValue(commandType.FullName) with
            | true, e -> e.GetDefaultState()
            | _ ->
                failwith
                    $"No event-sourced grain registered for command type '{commandType.FullName}'. \
                      Ensure AddFSharpEventSourcedGrain was called in the silo configurator."

        member _.GetDefaultStateByEventType(eventType: Type) =
            match eventHandlers.TryGetValue(eventType.FullName) with
            | true, e -> e.GetDefaultState()
            | _ ->
                failwith
                    $"No event-sourced grain registered for event type '{eventType.FullName}'. \
                      Ensure AddFSharpEventSourcedGrain was called in the silo configurator."

        member _.HandleCommand(state: obj, command: obj) =
            let key = command.GetType().FullName
            match commandHandlers.TryGetValue(key) with
            | true, e ->
                e.HandleCommand state command
                |> List.toArray
                :> IReadOnlyList<obj>
            | _ ->
                failwith
                    $"No handler for command type '{key}'. \
                      Ensure AddFSharpEventSourcedGrain was called in the silo configurator."

        member _.ApplyEvent(state: obj, event: obj) =
            let key = event.GetType().FullName
            match eventHandlers.TryGetValue(key) with
            | true, e -> e.ApplyEvent state event
            | _ ->
                failwith
                    $"No apply function for event type '{key}'. \
                      Ensure AddFSharpEventSourcedGrain was called in the silo configurator."
