namespace Orleans.FSharp

open System
open System.Collections.Generic
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>A declared reminder frozen into definition metadata.</summary>
[<ReferenceEquality>]
type internal ReminderDeclaration<'Actor, 'Key, 'State> =
    {
        /// The durable reminder name.
        Name: string
        /// Explicit due time.
        DueTime: TimeSpan
        /// Explicit period.
        Period: TimeSpan
        /// The reminder hook.
        Hook: ReminderHook<'Actor, 'Key, 'State>
    }

/// <summary>A declared timer frozen into definition metadata.</summary>
[<ReferenceEquality>]
type internal TimerDeclaration<'Actor, 'Key, 'State> =
    {
        /// The timer name, unique within the definition.
        Name: string
        /// <c>GrainTimerCreationOptions.DueTime</c>.
        DueTime: TimeSpan
        /// <c>GrainTimerCreationOptions.Period</c>.
        Period: TimeSpan
        /// <c>GrainTimerCreationOptions.Interleave</c>; whole-state timers require <c>false</c>.
        Interleave: bool
        /// <c>GrainTimerCreationOptions.KeepAlive</c>.
        KeepAlive: bool
        /// The timer hook.
        Hook: TimerHook<'Actor, 'Key, 'State>
    }

/// <summary>Accumulated, not yet sealed, definition configuration.</summary>
[<ReferenceEquality>]
type internal DefinitionDraftState<'Actor, 'Key, 'Api, 'State> =
    { Contract: GrainContract<'Actor, 'Key, 'Api>
      InitializerOperation: string
      Initializer: 'Key -> 'State
      Primary: PersistentStateRef<'State> option
      Additional: FunctionalFacetBlueprint list
      CollectionAge: TimeSpan option
      OnActivate: ActivateHook<'Actor, 'Key, 'State> option
      OnDeactivate: DeactivateHook<'Actor, 'Key, 'State> option
      Reminders: ReminderDeclaration<'Actor, 'Key, 'State> list
      Timers: TimerDeclaration<'Actor, 'Key, 'State> list
      Handlers: Map<int, obj> }

/// <summary>
/// A sealed server definition: the contract, state initialization, one handler per API field,
/// persistence attachment, lifecycle hooks, timers, reminders, and collection configuration.
/// </summary>
[<Sealed>]
type FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>
    internal (state: DefinitionDraftState<'Actor, 'Key, 'Api, 'State>) =

    /// <summary>The contract this definition hosts.</summary>
    member internal _.Contract = state.Contract

    /// <summary>The explicit Orleans grain type name.</summary>
    member internal _.GrainTypeName = state.Contract.GrainTypeName

    /// <summary>State initialization normalized to <c>'Key -&gt; 'State</c>.</summary>
    member internal _.Initializer = state.Initializer

    /// <summary>The primary persistent holder, when <c>stateFrom</c> is configured.</summary>
    member internal _.Primary = state.Primary

    /// <summary>Additional attached persistent states in declaration order.</summary>
    member internal _.Additional = state.Additional

    /// <summary>The configured idle collection age, when present.</summary>
    member internal _.CollectionAge = state.CollectionAge

    /// <summary>The activation hook, when configured.</summary>
    member internal _.OnActivate = state.OnActivate

    /// <summary>The deactivation hook, when configured.</summary>
    member internal _.OnDeactivate = state.OnDeactivate

    /// <summary>Declared reminders in declaration order.</summary>
    member internal _.Reminders = state.Reminders

    /// <summary>Declared timers in declaration order.</summary>
    member internal _.Timers = state.Timers

    /// <summary>Boxed handlers keyed by API-record field index.</summary>
    member internal _.Handlers = state.Handlers

    /// <summary>The boxed handler for one operation descriptor.</summary>
    member internal _.HandlerFor(operation: FunctionalOperation) = state.Handlers.[operation.Index]

    override _.ToString() =
        $"FunctionalGrainDefinition(grainType = '{state.Contract.GrainTypeName}', state = '{typeof<'State>.FullName}')"

/// <summary>
/// The seed state of a <c>grainFor</c> computation expression, before <c>defaultState</c> or
/// <c>initialState</c> introduces the state type.
/// </summary>
[<Sealed>]
type FunctionalGrainDefinitionSeed<'Actor, 'Key, 'Api> internal (contract: GrainContract<'Actor, 'Key, 'Api>) =

    /// <summary>The contract this definition will host.</summary>
    member internal _.Contract = contract

/// <summary>The intermediate state of a <c>grainFor</c> computation expression.</summary>
[<Sealed>]
type FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>
    internal (state: DefinitionDraftState<'Actor, 'Key, 'Api, 'State>) =

    /// <summary>The accumulated configuration.</summary>
    member internal _.State = state

/// <summary>Definition-draft helpers shared by the computation-expression builder.</summary>
module internal DefinitionDraft =

    let create
        (contract: GrainContract<'Actor, 'Key, 'Api>)
        (operationName: string)
        (initializer: 'Key -> 'State)
        : FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> =
        if obj.ReferenceEquals(initializer, null) then
            fail DefinitionStage $"'{operationName}' requires a state factory."

        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>(
            { Contract = contract
              InitializerOperation = operationName
              Initializer = initializer
              Primary = None
              Additional = []
              CollectionAge = None
              OnActivate = None
              OnDeactivate = None
              Reminders = []
              Timers = []
              Handlers = Map.empty }
        )

    let withState (state: DefinitionDraftState<'Actor, 'Key, 'Api, 'State>) =
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>(state)

    /// <summary>Reject a repeated singleton operation instead of replacing the earlier value.</summary>
    let single (operationName: string) (grainTypeName: string) (current: 'T option) (value: 'T) =
        match current with
        | Some _ ->
            fail
                DefinitionStage
                $"'{operationName}' is declared more than once for grain type '{grainTypeName}'. A repeated singleton operation is a definition error."
        | None -> Some value

    /// <summary>Seal a draft into an immutable definition.</summary>
    let run
        (draft: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>)
        : FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State> =
        let state = draft.State
        let grainTypeName = state.Contract.GrainTypeName

        // Exactly one handler for every API-record field.
        let missing =
            state.Contract.Operations
            |> Array.filter (fun operation -> not (state.Handlers.ContainsKey operation.Index))
            |> Array.map (fun operation -> operation.FieldName)

        if missing.Length > 0 then
            let missingNames = String.Join(", ", missing)

            fail DefinitionStage $"grain type '{grainTypeName}' has no handler for API field(s) {missingNames}."

        // Unique state names, with one provider and one stored type per name. The name alone is
        // the key: Orleans derives its activation-migration keys from the state name, so the
        // same name under two different providers is still a collision.
        let attached = ResizeArray<PersistentStateDescriptor>()

        match state.Primary with
        | Some primary -> attached.Add primary.Descriptor
        | None -> ()

        for extra in state.Additional do
            attached.Add extra.Descriptor

        let seenStates = Dictionary<string, PersistentStateDescriptor>(StringComparer.Ordinal)

        for descriptor in attached do
            match seenStates.TryGetValue descriptor.StateName with
            | true, existing ->
                // The full logical identity, not the state name alone: the collision at hand
                // already shares its name with the primary descriptor (that is how it reached
                // this branch), but it is the SAME attachment as 'stateFrom' only when its
                // provider and stored type match too. A same-named 'usePersistentState' under a
                // different provider or stored type is a genuine name collision, not a repeat of
                // the primary, and must not get the "already attached as the primary state"
                // sentence appended to its message.
                let repeatsPrimary =
                    match state.Primary with
                    | Some primary -> primary.Descriptor = descriptor
                    | None -> false

                let detail =
                    if repeatsPrimary then
                        " The 'stateFrom' descriptor is already attached as the primary state and must not be repeated with 'usePersistentState'."
                    else
                        ""

                fail
                    DefinitionStage
                    $"stateName '{descriptor.StateName}' is attached more than once to grain type '{grainTypeName}' (providers '{existing.ProviderName}' and '{descriptor.ProviderName}', stored types '{existing.StoredType.FullName}' and '{descriptor.StoredType.FullName}').{detail}"
            | _ -> seenStates.[descriptor.StateName] <- descriptor

        // Stock Orleans cannot even construct an IPersistentState over some closed types, so an
        // attachment of one of them can never activate on any storage provider.
        for descriptor in attached do
            match StoredStateType.unsupportedReason descriptor.StoredType with
            | Some reason ->
                fail
                    DefinitionStage
                    $"the stored type '{descriptor.StoredType.FullName}' of persistent state '{descriptor.StateName}' (provider '{descriptor.ProviderName}') attached to grain type '{grainTypeName}' cannot be held in an Orleans IPersistentState: {reason}."
            | None -> ()

        match state.CollectionAge with
        | Some age when age <= TimeSpan.Zero ->
            fail
                DefinitionStage
                $"'collectionAge' for grain type '{grainTypeName}' must be strictly positive, but {age} was supplied."
        | _ -> ()

        let seenReminders = HashSet<string>(StringComparer.Ordinal)

        for reminder in state.Reminders do
            if isBlank reminder.Name then
                fail DefinitionStage $"a reminder of grain type '{grainTypeName}' has a blank name."

            if not (seenReminders.Add reminder.Name) then
                fail
                    DefinitionStage
                    $"reminder name '{reminder.Name}' is declared more than once for grain type '{grainTypeName}'."

            if reminder.DueTime < TimeSpan.Zero then
                fail
                    DefinitionStage
                    $"reminder '{reminder.Name}' of grain type '{grainTypeName}' requires dueTime >= 0, but {reminder.DueTime} was supplied."

            if reminder.Period <= TimeSpan.Zero then
                fail
                    DefinitionStage
                    $"reminder '{reminder.Name}' of grain type '{grainTypeName}' requires period > 0, but {reminder.Period} was supplied."

        let seenTimers = HashSet<string>(StringComparer.Ordinal)

        for timer in state.Timers do
            if isBlank timer.Name then
                fail DefinitionStage $"a timer of grain type '{grainTypeName}' has a blank name."

            if not (seenTimers.Add timer.Name) then
                fail
                    DefinitionStage
                    $"timer name '{timer.Name}' is declared more than once for grain type '{grainTypeName}'."

            if timer.Interleave then
                fail
                    DefinitionStage
                    $"timer '{timer.Name}' of grain type '{grainTypeName}' sets Interleave = true, which a whole-state timer hook rejects."

        FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>(state)

/// <summary>
/// The <c>grainFor</c> computation expression: state initialization, one handler per API field,
/// persistence attachment, lifecycle hooks, timers, reminders, and collection age.
/// </summary>
[<Sealed>]
type FunctionalGrainDefinitionBuilder<'Actor, 'Key, 'Api> internal (contract: GrainContract<'Actor, 'Key, 'Api>) =

    /// <summary>Start a definition seed for the contract.</summary>
    member _.Yield(_: unit) : FunctionalGrainDefinitionSeed<'Actor, 'Key, 'Api> =
        FunctionalGrainDefinitionSeed<'Actor, 'Key, 'Api>(contract)

    /// <summary>Validate and seal the draft into an immutable definition.</summary>
    member _.Run<'State>
        (draft: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>)
        : FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State> =
        DefinitionDraft.run draft

    /// <summary>Introduce the state type with a key-independent factory.</summary>
    [<CustomOperation("defaultState")>]
    member _.DefaultState<'State>
        (state: FunctionalGrainDefinitionSeed<'Actor, 'Key, 'Api>, factory: unit -> 'State)
        =
        DefinitionDraft.create state.Contract "defaultState" (fun _ -> factory ())

    /// <summary>Introduce the state type with a key-aware factory.</summary>
    [<CustomOperation("initialState")>]
    member _.InitialState<'State>
        (state: FunctionalGrainDefinitionSeed<'Actor, 'Key, 'Api>, factory: 'Key -> 'State)
        =
        DefinitionDraft.create state.Contract "initialState" factory

    /// <summary>Bind one handler to the operation identified by the selector.</summary>
    [<CustomOperation("handle")>]
    member _.Handle<'State, 'Argument, 'Reply>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            selector: OperationSelector<'Api, 'Argument, 'Reply>,
            handler: Handler<'Actor, 'Key, 'State, 'Argument, 'Reply>
        ) =
        let draft = state.State
        let operation = draft.Contract.Resolve("handle", selector)

        if draft.Handlers.ContainsKey operation.Index then
            fail
                DefinitionStage
                $"API field '{operation.FieldName}' of grain type '{draft.Contract.GrainTypeName}' already has a handler."

        if obj.ReferenceEquals(handler, null) then
            fail
                DefinitionStage
                $"'handle' for API field '{operation.FieldName}' of grain type '{draft.Contract.GrainTypeName}' requires a handler."

        DefinitionDraft.withState
            { draft with
                Handlers = draft.Handlers.Add(operation.Index, box handler) }

    /// <summary>Select the loaded primary persistent holder for the definition's state.</summary>
    [<CustomOperation("stateFrom")>]
    member _.StateFrom<'State>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            persistentState: PersistentStateRef<'State>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(persistentState, null) then
            fail DefinitionStage "'stateFrom' requires a PersistentStateRef value."

        DefinitionDraft.withState
            { draft with
                Primary = DefinitionDraft.single "stateFrom" draft.Contract.GrainTypeName draft.Primary persistentState }

    /// <summary>Attach an additional independently typed persistent state.</summary>
    [<CustomOperation("usePersistentState")>]
    member _.UsePersistentState<'State, 'StoredState>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            persistentState: PersistentStateRef<'StoredState>,
            initializer: 'Key -> 'StoredState
        ) =
        let draft = state.State

        if obj.ReferenceEquals(persistentState, null) then
            fail DefinitionStage "'usePersistentState' requires a PersistentStateRef value."

        if obj.ReferenceEquals(initializer, null) then
            fail
                DefinitionStage
                $"'usePersistentState' for stateName '{persistentState.StateName}' requires an initializer."

        // The blueprint is closed over the exact stored type here, where 'StoredState is still
        // a type parameter of this custom operation. No silo-side code ever closes it again.
        let attached =
            FunctionalFacet.blueprint persistentState (fun key -> box (initializer (unbox<'Key> key)))

        DefinitionDraft.withState
            { draft with
                Additional = draft.Additional @ [ attached ] }

    /// <summary>Set the Orleans idle collection age for this grain type.</summary>
    [<CustomOperation("collectionAge")>]
    member _.CollectionAge<'State>(state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>, age: TimeSpan) =
        let draft = state.State

        DefinitionDraft.withState
            { draft with
                CollectionAge =
                    DefinitionDraft.single "collectionAge" draft.Contract.GrainTypeName draft.CollectionAge age }

    /// <summary>Run a hook after persistent-state setup; its returned state is published in memory.</summary>
    [<CustomOperation("onActivate")>]
    member _.OnActivate<'State>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            hook: ActivateHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        DefinitionDraft.withState
            { draft with
                OnActivate = DefinitionDraft.single "onActivate" draft.Contract.GrainTypeName draft.OnActivate hook }

    /// <summary>Run a cleanup hook during deactivation; it returns no replacement state.</summary>
    [<CustomOperation("onDeactivate")>]
    member _.OnDeactivate<'State>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            hook: DeactivateHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        DefinitionDraft.withState
            { draft with
                OnDeactivate =
                    DefinitionDraft.single "onDeactivate" draft.Contract.GrainTypeName draft.OnDeactivate hook }

    /// <summary>Declare a durable reminder with an explicit due time and period.</summary>
    [<CustomOperation("onReminder")>]
    member _.OnReminder<'State>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            name: string,
            dueTime: TimeSpan,
            period: TimeSpan,
            hook: ReminderHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        let declaration =
            { Name = name
              DueTime = dueTime
              Period = period
              Hook = hook }

        DefinitionDraft.withState
            { draft with
                Reminders = draft.Reminders @ [ declaration ] }

    /// <summary>Declare an activation-local timer.</summary>
    [<CustomOperation("onTimer")>]
    member _.OnTimer<'State>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            name: string,
            options: GrainTimerCreationOptions,
            hook: TimerHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        let declaration =
            { Name = name
              DueTime = options.DueTime
              Period = options.Period
              Interleave = options.Interleave
              KeepAlive = options.KeepAlive
              Hook = hook }

        DefinitionDraft.withState
            { draft with
                Timers = draft.Timers @ [ declaration ] }
