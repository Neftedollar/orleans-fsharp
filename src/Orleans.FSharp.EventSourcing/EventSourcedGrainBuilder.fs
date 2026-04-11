namespace Orleans.FSharp.EventSourcing

open System
open System.Threading.Tasks

/// <summary>
/// Marks a module-level F# event-sourced grain definition for automatic C# stub generation.
/// Apply this attribute to a <c>let</c> binding of type
/// <c>EventSourcedGrainDefinition&lt;TState, TEvent, TCommand&gt;</c>.
/// The <c>Orleans.FSharp.Generator</c> tool reads this attribute at build time and emits a
/// minimal C# class that extends
/// <c>FSharpEventSourcedGrain&lt;TState, TEvent, TCommand&gt;</c>
/// and implements the specified grain interface — no hand-written C# stub needed.
/// </summary>
/// <example>
/// <code lang="fsharp">
/// [&lt;FSharpEventSourcedGrain(typeof&lt;IBankAccountGrain&gt;)&gt;]
/// let bankAccount = eventSourcedGrain {
///     defaultState (BankAccountState())
///     apply applyEvent
///     handle handleCommand
/// }
/// </code>
/// </example>
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type FSharpEventSourcedGrainAttribute(grainInterface: Type) =
    inherit Attribute()
    /// <summary>The Orleans grain interface that the generated C# stub will implement.</summary>
    member _.GrainInterface = grainInterface

/// <summary>
/// Boxed read/write callbacks for custom log-consistency storage, used to bridge
/// typed F# functions to C#-callable delegates without per-grain generic parameters.
/// The F# user provides typed callbacks via the <c>customStorage</c> CE operation;
/// this adapter is stored in the definition and consumed by the generated C# stub.
/// </summary>
[<NoEquality; NoComparison>]
type CustomStorageAdapter =
    { /// <summary>
      /// Reads the current version and grain state from the custom storage back-end.
      /// Returns a struct tuple (version, boxed state) so the C# caller can destructure it.
      /// </summary>
      ReadBoxed: Func<Task<struct (int * obj)>>
      /// <summary>
      /// Applies a list of boxed events to the custom storage back-end using optimistic concurrency.
      /// Returns <c>true</c> when the write succeeds, <c>false</c> on concurrency conflict.
      /// </summary>
      WriteBoxed: Func<obj list, int, Task<bool>> }

/// <summary>
/// Controls when state snapshots are written alongside the event log.
/// Snapshots allow a future hybrid log-consistency provider to load the latest
/// checkpoint and only replay events after it, avoiding full event-history replay
/// on every grain activation.
/// </summary>
/// <remarks>
/// With the built-in <c>LogStorageBasedLogConsistencyProvider</c>, all events are
/// always replayed on activation — this type declares <em>intent</em> for providers
/// that support hybrid snapshot+tail-event storage (e.g. a future Marten provider).
/// Use <c>Never</c> to disable snapshots entirely (default).
/// </remarks>
/// <typeparam name="'State">The grain state type that will be snapshotted.</typeparam>
[<NoEquality; NoComparison>]
type SnapshotStrategy<'State> =
    /// <summary>Never write snapshots. All events are replayed on every activation.</summary>
    | Never
    /// <summary>
    /// Write a snapshot every <paramref name="everyN"/> confirmed events (based on grain Version).
    /// Version is checked after each successful command; the snapshot fires when
    /// <c>Version % everyN = 0</c>.
    /// </summary>
    | Every of everyN: int
    /// <summary>
    /// Write a snapshot whenever the predicate returns <c>true</c>.
    /// The predicate receives the current confirmed version and the current state.
    /// </summary>
    | Condition of predicate: (int -> 'State -> bool)

/// <summary>
/// Defines the complete specification for an event-sourced F# grain.
/// Captures the initial state, event application function, command handler,
/// log consistency provider configuration, and optional snapshot strategy.
/// </summary>
/// <typeparam name="'State">The type of the grain's state, rebuilt by folding events.</typeparam>
/// <typeparam name="'Event">The type of events that modify state.</typeparam>
/// <typeparam name="'Command">The type of commands the grain handles.</typeparam>
type EventSourcedGrainDefinition<'State, 'Event, 'Command> =
    {
        /// <summary>The initial state value for the grain when no events have been applied.</summary>
        DefaultState: 'State option
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
        /// <summary>
        /// Controls when state snapshots are written.
        /// Defaults to <c>Never</c>. Requires a snapshot-capable log-consistency provider
        /// (e.g. a future Marten hybrid provider) to have an effect at runtime.
        /// </summary>
        SnapshotStrategy: SnapshotStrategy<'State>
        /// <summary>
        /// Optional custom storage adapter for use with
        /// <c>CustomStorageBasedLogConsistencyProvider</c> (provider name <c>"CustomStorage"</c>).
        /// When set, the generated C# stub implements
        /// <c>ICustomStorageInterface&lt;WrappedEventSourcedState, WrappedEventSourcedEvent&gt;</c>
        /// and delegates read/write calls to these typed F# functions.
        /// Combine with <c>logConsistencyProvider "CustomStorage"</c>.
        /// </summary>
        CustomStorage: CustomStorageAdapter option
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
///     snapshot (Every 100)
/// }
/// </code>
/// </example>
type EventSourcedGrainBuilder() =

    /// <summary>Yields the initial empty event-sourced grain definition.</summary>
    member _.Yield(_: unit) : EventSourcedGrainDefinition<'State, 'Event, 'Command> =
        {
            DefaultState = None
            Apply = fun state _event -> state
            Handle = fun _state _command -> []
            ConsistencyProvider = None
            SnapshotStrategy = Never
            CustomStorage = None
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
        { definition with DefaultState = Some state }

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

    /// <summary>
    /// Sets the snapshot strategy for the grain.
    /// Controls when state is persisted as a checkpoint alongside the event log.
    /// Requires a snapshot-capable log-consistency provider to have a runtime effect.
    /// </summary>
    /// <param name="definition">The current definition being built.</param>
    /// <param name="strategy">The snapshot strategy to use.</param>
    /// <returns>The updated definition with the snapshot strategy set.</returns>
    [<CustomOperation("snapshot")>]
    member _.Snapshot
        (
            definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>,
            strategy: SnapshotStrategy<'State>
        ) =
        { definition with SnapshotStrategy = strategy }

    /// <summary>
    /// Configures custom storage callbacks for use with <c>CustomStorageBasedLogConsistencyProvider</c>.
    /// The read function loads the current (version, state) from your storage back-end;
    /// the write function persists a batch of new events with optimistic concurrency.
    /// Pair this with <c>logConsistencyProvider "CustomStorage"</c>.
    /// </summary>
    /// <param name="definition">The current definition being built.</param>
    /// <param name="read">
    /// Function that reads the current version and state from custom storage.
    /// Returns a tuple of (version, state).
    /// </param>
    /// <param name="write">
    /// Function that applies a list of new events to custom storage.
    /// Receives the events and the expected version for optimistic concurrency.
    /// Returns <c>true</c> on success, <c>false</c> on concurrency conflict.
    /// </param>
    /// <returns>The updated definition with custom storage configured.</returns>
    [<CustomOperation("customStorage")>]
    member _.CustomStorage
        (
            definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>,
            read: unit -> Task<int * 'State>,
            write: 'Event list -> int -> Task<bool>
        ) : EventSourcedGrainDefinition<'State, 'Event, 'Command> =
        let adapter =
            { ReadBoxed =
                Func<_>(fun () -> task {
                    let! v, s = read ()
                    return struct (v, box s)
                })
              WriteBoxed =
                Func<_, _, _>(fun (events: obj list) version -> task {
                    let typed = events |> List.map unbox<'Event>
                    return! write typed version
                }) }

        { definition with CustomStorage = Some adapter }

    /// <summary>Returns the completed event-sourced grain definition. Validates that defaultState was set.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown when defaultState was not set for reference types.</exception>
    member _.Run(definition: EventSourcedGrainDefinition<'State, 'Event, 'Command>) =
        if definition.DefaultState.IsNone then
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
    /// Supports custom operations: defaultState, apply, handle, logConsistencyProvider, snapshot.
    /// </summary>
    let eventSourcedGrain = EventSourcedGrainBuilder()
