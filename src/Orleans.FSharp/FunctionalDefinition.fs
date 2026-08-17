namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Threading.Tasks
open Orleans.BroadcastChannel
open Orleans.Runtime
open Orleans.Streams
open Orleans.Streams.Core
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// A closed set mirroring the Orleans stock placement strategies present on both supported
/// Orleans versions (10.1.0 and 10.2.2 -- verified by reflection: identical types and identical
/// <c>IGrainPropertiesProviderAttribute.Populate</c> output on both). <c>Random</c> is Orleans'
/// own default and needs no explicit configuration; it is included so an application can still
/// name it, matching the same brief's other cases rather than special-casing it away.
/// </summary>
/// <remarks>
/// Orleans also ships <c>HashBasedPlacement</c>, <c>SiloRoleBasedPlacement</c>, and the internal
/// <c>ClientObserversPlacement</c> / <c>SystemTargetPlacementStrategy</c>, all present on both
/// versions too. They are deliberately not mirrored here: hash-based and silo-role placement
/// address separate, more specialized concerns (consistent hashing and silo-role affinity) that
/// spec 004 item 4's design sketch does not name as a candidate, and the other two are not
/// meant for application grains at all. No strategy here is version-gated -- every case mirrors a
/// type present, with identical published properties, on Orleans 10.1.0 and 10.2.2 alike.
/// </remarks>
type PlacementStrategy =
    /// <summary>Orleans' default: activate anywhere.</summary>
    | Random
    /// <summary>Prefer activating on the silo that received the call, when eligible.</summary>
    | PreferLocal
    /// <summary>Balance placement across silos by relative recently-active-grain count.</summary>
    | ActivationCountBased
    /// <summary>Balance placement across silos by resource usage (CPU, memory).</summary>
    | ResourceOptimized

/// <summary>A definition's placement configuration: at most one of a stock strategy or
/// stateless-worker multiplexing (mutually exclusive -- see <c>DefinitionDraft.run</c>).</summary>
type internal PlacementConfiguration =
    /// <summary>One stock Orleans placement strategy.</summary>
    | Strategy of PlacementStrategy
    /// <summary>Stateless-worker placement with the given maximum local activation count.</summary>
    | StatelessWorker of maxLocalWorkers: int

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

/// <summary>
/// Hands one delivered item back to the runtime: the boxed item and the Orleans cursor of that
/// item (<c>null</c> when the transport carries none, which is always the case for broadcast
/// channels and for non-rewindable stream providers).
/// </summary>
type internal FunctionalStreamDelivery = delegate of obj * StreamSequenceToken -> Task

/// <summary>
/// The preclosed typed attach of one declared implicit stream subscription. It creates the
/// exactly-typed subscription handle from the factory Orleans supplies and resumes it with an
/// observer that forwards every item to the runtime's delivery callback.
/// </summary>
type internal FunctionalStreamAttach =
    delegate of IStreamSubscriptionHandleFactory * FunctionalStreamDelivery -> Task

/// <summary>
/// The preclosed typed attach of one declared implicit broadcast-channel subscription. It calls
/// <c>IBroadcastChannelSubscription.Attach</c> at the exact item type.
/// </summary>
type internal FunctionalChannelAttach =
    delegate of IBroadcastChannelSubscription * FunctionalStreamDelivery -> Task

/// <summary>Which Orleans implicit-subscription transport a declared binding rides.</summary>
type internal FunctionalStreamAttachment =
    /// <summary>An <c>onStream</c> declaration: Orleans streams, bound by stream namespace.</summary>
    | StreamAttachment of FunctionalStreamAttach
    /// <summary>An <c>onBroadcast</c> declaration: broadcast channels, bound by channel namespace.</summary>
    | ChannelAttachment of FunctionalChannelAttach

/// <summary>
/// The preclosed typed adapter of one declared stream or broadcast hook: the boxed domain key,
/// the invocation context core, the boxed current primary state, and the boxed item, returning
/// the boxed replacement state.
/// </summary>
type internal FunctionalStreamHookAdapter = delegate of obj * FunctionalContextCore * obj * obj -> Task<obj>

/// <summary>
/// One declared implicit subscription, frozen into definition metadata. Deliberately
/// non-generic: the item type is erased into the two preclosed adapters at declaration time,
/// where the custom operation's own <c>'Item</c> parameter is still in scope, so no silo-side
/// code ever closes a generic again. The same value is carried by the hosted view.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalStreamDeclaration =
    {
        /// The transport and its preclosed typed attach.
        Attachment: FunctionalStreamAttachment
        /// The named Orleans stream or broadcast-channel provider this declaration subscribes on.
        ProviderName: string
        /// The stream or channel namespace this declaration subscribes to.
        Namespace: string
        /// The exact item type carried on the stream or channel.
        ItemType: Type
        /// The preclosed typed hook adapter.
        Adapter: FunctionalStreamHookAdapter
    }

    /// <summary>The custom operation which produced this declaration; used in diagnostics.</summary>
    member this.OperationName =
        match this.Attachment with
        | StreamAttachment _ -> "onStream"
        | ChannelAttachment _ -> "onBroadcast"

    /// <summary>True when this declaration rides Orleans streams rather than broadcast channels.</summary>
    member this.IsStream =
        match this.Attachment with
        | StreamAttachment _ -> true
        | ChannelAttachment _ -> false

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
      StreamBindings: FunctionalStreamDeclaration list
      Placement: PlacementConfiguration option
      LifecycleHooks: Map<LifecycleStage, LifecycleHook<'Actor, 'Key>>
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

    /// <summary>Declared implicit stream and broadcast subscriptions in declaration order.</summary>
    member internal _.StreamBindings = state.StreamBindings

    /// <summary>The configured placement, when <c>statelessWorker</c> or <c>placement</c> was
    /// declared.</summary>
    member internal _.Placement = state.Placement

    /// <summary>Declared lifecycle-stage hooks, keyed by their unique stage.</summary>
    member internal _.LifecycleHooks = state.LifecycleHooks

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
              StreamBindings = []
              Placement = None
              LifecycleHooks = Map.empty
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

    /// <summary>
    /// Reject a second placement operation: <c>statelessWorker</c> and <c>placement</c> are
    /// mutually exclusive, in either order, so the message does not name which one came first.
    /// </summary>
    let singlePlacement
        (operationName: string)
        (grainTypeName: string)
        (current: PlacementConfiguration option)
        (value: PlacementConfiguration)
        =
        match current with
        | Some _ ->
            fail
                DefinitionStage
                $"'{operationName}' cannot be combined with an earlier 'statelessWorker' or 'placement' operation on grain type '{grainTypeName}'. At most one placement configuration is allowed."
        | None -> Some value

    /// <summary>Seal a draft into an immutable definition.</summary>
    let run
        (draft: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>)
        : FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State> =
        let state = draft.State
        let grainTypeName = state.Contract.GrainTypeName

        // A derived grain type (no explicit 'grainType' on the contract) moves silently if the
        // actor brand is ever renamed, because the brand's CLR simple name IS the grain type in
        // that case. That is harmless for an ephemeral definition, but it would orphan persisted
        // state or lose durable reminders registered under the old name, so any definition with a
        // durable attachment requires an explicit 'grainType'.
        let hasDurableAttachment =
            state.Primary.IsSome
            || not (List.isEmpty state.Additional)
            || not (List.isEmpty state.Reminders)

        if hasDurableAttachment && not state.Contract.IsGrainTypeExplicit then
            fail
                DefinitionStage
                $"grain type '{grainTypeName}' attaches 'stateFrom', 'usePersistentState', or declares 'onReminder', but its contract derives 'grainType' from the actor brand '{typeof<'Actor>.FullName}' instead of declaring one explicitly. A brand rename would then silently move routing AND storage identity, orphaning persisted state and losing durable reminders. Declare an explicit 'grainType' on the contract before attaching durable state or a reminder."

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

        // "statelessWorker rejects stateFrom, usePersistentState, onReminder (durable identity is
        // meaningless for multiplexed local activations) and rejects collectionAge (Orleans
        // ignores it for stateless workers)." Checked at sealing so it applies regardless of the
        // order 'statelessWorker' and the rejected operation were declared in.
        match state.Placement with
        | Some(StatelessWorker maxLocalWorkers) ->
            if maxLocalWorkers <= 0 then
                fail
                    DefinitionStage
                    $"'statelessWorker' for grain type '{grainTypeName}' requires a strictly positive maxLocalWorkers, but {maxLocalWorkers} was supplied."

            if state.Primary.IsSome then
                fail
                    DefinitionStage
                    $"grain type '{grainTypeName}' combines 'statelessWorker' with 'stateFrom'. Durable identity is meaningless for multiplexed local activations that Orleans may create, deactivate, and re-create at will."

            if not (List.isEmpty state.Additional) then
                fail
                    DefinitionStage
                    $"grain type '{grainTypeName}' combines 'statelessWorker' with 'usePersistentState'. Durable identity is meaningless for multiplexed local activations that Orleans may create, deactivate, and re-create at will."

            if not (List.isEmpty state.Reminders) then
                fail
                    DefinitionStage
                    $"grain type '{grainTypeName}' combines 'statelessWorker' with 'onReminder'. Durable identity is meaningless for multiplexed local activations that Orleans may create, deactivate, and re-create at will."

            if state.CollectionAge.IsSome then
                fail
                    DefinitionStage
                    $"grain type '{grainTypeName}' combines 'statelessWorker' with 'collectionAge'. Orleans ignores the idle collection age for stateless-worker activations."

            // Orleans itself refuses to bind a grain extension to a stateless worker:
            // SiloStreamProviderRuntime.BindExtension throws "The extension
            // Orleans.Streams.StreamConsumerExtension cannot be bound to a Stateless Worker."
            // Implicit delivery is routed to ONE activation identity derived from the stream key,
            // which multiplexed local activations have no way to honor, so the combination can
            // never work and is rejected here rather than failing an activation later.
            match state.StreamBindings |> List.tryHead with
            | Some binding ->
                fail
                    DefinitionStage
                    $"grain type '{grainTypeName}' combines 'statelessWorker' with '{binding.OperationName}'. Orleans refuses to bind a stream or broadcast consumer extension to a stateless worker (SiloStreamProviderRuntime.BindExtension), and implicit delivery addresses one activation identity derived from the stream key, which multiplexed local activations cannot honor."
            | None -> ()
        | Some(Strategy _)
        | None -> ()

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

        // Declared implicit subscriptions: non-blank provider and namespace, and one hook per
        // (transport, provider, namespace) triple. The triple -- not the namespace alone -- is
        // the key: the same namespace on two different providers is two different streams, and
        // the delivery path matches on both, so both may be declared. A repeated triple would be
        // an unreachable second hook.
        let seenBindings =
            HashSet<struct (bool * string * string)>(
                HashIdentity.Structural
            )

        for binding in state.StreamBindings do
            if isBlank binding.ProviderName then
                fail
                    DefinitionStage
                    $"an '{binding.OperationName}' declaration of grain type '{grainTypeName}' has a blank provider name."

            if isBlank binding.Namespace then
                fail
                    DefinitionStage
                    $"an '{binding.OperationName}' declaration of grain type '{grainTypeName}' (provider '{binding.ProviderName}') has a blank namespace."

            if not (seenBindings.Add(struct (binding.IsStream, binding.ProviderName, binding.Namespace))) then
                fail
                    DefinitionStage
                    $"'{binding.OperationName}' is declared more than once for provider '{binding.ProviderName}' and namespace '{binding.Namespace}' on grain type '{grainTypeName}'. Each (provider, namespace) pair accepts at most one hook."

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
    /// <remarks>
    /// <para>
    /// Every successful activation reconciles the declared reminders through
    /// <c>RegisterOrUpdateReminder</c>, so adding a declaration or changing its due time or
    /// period needs no migration: the next activation updates the durable registration in place.
    /// </para>
    /// <para>
    /// <b>Renaming or removing a declaration is different, and nothing automatic happens.</b>
    /// The registration lives in the reminder table, not in the definition, so it survives the
    /// deployment that dropped the declaration and keeps firing. Every tick then arrives at a
    /// name the definition no longer declares, is logged with the grain and reminder identity,
    /// and fails that callback — for as long as the registration exists. Retiring it is an
    /// explicit application step, because the runtime cannot tell a rename from a grain type
    /// that is temporarily not deployed, and unregistering on its own guess would silently
    /// destroy durable schedules.
    /// </para>
    /// <para>
    /// The migration is a one-off, idempotent call through the stock reminder registry, which
    /// the functional context reaches through <c>context.services</c> (the functional surface
    /// intentionally exposes no reminder API of its own):
    /// </para>
    /// <code>
    /// handle (_.retireStaleReminder) (fun context state () ->
    ///     task {
    ///         let registry = context.services.GetRequiredService&lt;IReminderRegistry&gt;()
    ///         let! stale = registry.GetReminder(context.grainId, "old-name")
    ///
    ///         if not (obj.ReferenceEquals(stale, null)) then
    ///             do! registry.UnregisterReminder(context.grainId, stale)
    ///
    ///         return state, ()
    ///     })
    /// </code>
    /// <para>
    /// Run it for every grain that carried the old name (<c>registry.GetReminders grainId</c>
    /// enumerates what a grain still has registered), and keep the retiring operation deployed
    /// until every such grain has been visited. A rename is the removal above plus the new
    /// declaration; there is no in-place rename.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Subscribe this grain type implicitly to one Orleans stream namespace on one named stream
    /// provider. An item published to <c>StreamId.Create(streamNamespace, key)</c> activates the
    /// grain whose identity encodes <c>key</c> — creating it if it does not exist — and is
    /// delivered to <paramref name="hook"/>.
    /// </summary>
    /// <param name="providerName">The named Orleans stream provider (<c>AddMemoryStreams "name"</c>
    /// and friends). It must be registered on every silo which hosts this definition; silo startup
    /// validation rejects a name no provider answers to.</param>
    /// <param name="streamNamespace">The stream namespace to subscribe to, matched exactly.</param>
    /// <param name="hook">The delivery hook. Its item type is inferred from the lambda, so an
    /// annotation is usually needed: <c>(fun context state (item: Ping) -&gt; ...)</c>.</param>
    /// <remarks>
    /// <para>
    /// <b>Scheduling and state.</b> A delivery is an ordinary non-reentrant grain call
    /// (<c>IStreamConsumerExtension</c>'s <c>DeliverImmutable</c> / <c>DeliverMutable</c> /
    /// <c>DeliverBatch</c> carry no <c>[AlwaysInterleave]</c>), so the timer-hook rules apply
    /// unchanged: the hook sees the current whole state, its returned
    /// state is published in memory when — and only when — it returns successfully, and the
    /// runtime issues no storage call of its own. The context token is
    /// <c>CancellationToken.None</c>, because <c>IAsyncObserver.OnNextAsync</c> supplies none.
    /// </para>
    /// <para>
    /// <b>A throwing hook is retried by Orleans, then the item is dropped.</b> The exception
    /// travels back to the pulling agent, which redelivers the same item with backoff for up to
    /// <c>StreamPullingAgentOptions.MaxEventDeliveryTime</c> (30 seconds by default) and then
    /// moves on. An implicit subscription is never faulted by a delivery failure — Orleans'
    /// <c>PersistentStreamPullingAgent.ErrorProtocol</c> excludes implicit subscriptions from
    /// subscription faulting explicitly — so the next item still arrives. Delivery is therefore
    /// at-least-once: a hook which is not idempotent should de-duplicate, for which
    /// <c>context.streamSequenceToken</c> is available on a rewindable provider.
    /// </para>
    /// <para>
    /// <b>Multiple declarations.</b> Several <c>onStream</c> operations are allowed, one per
    /// (provider, namespace) pair; a repeated pair is rejected at sealing. Note that Orleans'
    /// implicit-subscription binding names a namespace and NOT a provider, so an item published
    /// to a declared namespace on an UNDECLARED provider still routes to this grain type; such a
    /// delivery matches no hook, is logged as a warning, and is left for Orleans to drop.
    /// </para>
    /// <para>
    /// <b>Rejected with <c>statelessWorker</c>.</b> Orleans refuses to bind a consumer extension
    /// to a stateless worker, and implicit delivery addresses one activation identity.
    /// </para>
    /// </remarks>
    [<CustomOperation("onStream")>]
    member _.OnStream<'State, 'Item>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            providerName: string,
            streamNamespace: string,
            hook: StreamHook<'Actor, 'Key, 'State, 'Item>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail
                DefinitionStage
                $"'onStream' for provider '{providerName}' and namespace '{streamNamespace}' on grain type '{draft.Contract.GrainTypeName}' requires a hook."

        // Both delegates are closed over 'Item here, where it is still a type parameter of this
        // custom operation. No silo-side code ever closes it again.
        let attach =
            FunctionalStreamAttach(fun factory delivery ->
                let handle = factory.Create<'Item>()

                let observer =
                    { new IAsyncObserver<'Item> with
                        member _.OnNextAsync(item: 'Item, token: StreamSequenceToken) =
                            delivery.Invoke(box item, token)

                        member _.OnCompletedAsync() = Task.CompletedTask
                        member _.OnErrorAsync(_error: exn) = Task.CompletedTask }

                handle.ResumeAsync observer :> Task)

        let adapter =
            FunctionalStreamHookAdapter(fun key core currentState item ->
                task {
                    let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                    let! next = hook context (unbox<'State> currentState) (unbox<'Item> item)
                    return box next
                })

        let declaration =
            { Attachment = StreamAttachment attach
              ProviderName = providerName
              Namespace = streamNamespace
              ItemType = typeof<'Item>
              Adapter = adapter }

        DefinitionDraft.withState
            { draft with
                StreamBindings = draft.StreamBindings @ [ declaration ] }

    /// <summary>
    /// Subscribe this grain type implicitly to one broadcast-channel namespace on one named
    /// broadcast-channel provider. A publish to <c>ChannelId.Create(channelNamespace, key)</c>
    /// activates the grain whose identity encodes <c>key</c> and is delivered to
    /// <paramref name="hook"/>.
    /// </summary>
    /// <param name="providerName">The named broadcast-channel provider
    /// (<c>AddBroadcastChannel "name"</c>). Silo startup validation rejects a name no provider
    /// answers to.</param>
    /// <param name="channelNamespace">The channel namespace to subscribe to, matched exactly.
    /// This is the namespace half of a <c>ChannelId</c>, not a whole channel id: the key half is
    /// what selects the activation.</param>
    /// <param name="hook">The delivery hook, with the same shape and rules as
    /// <c>onStream</c>'s.</param>
    /// <remarks>
    /// <para>
    /// The delivery rules match <c>onStream</c>'s exactly — non-reentrant delivery, whole-state
    /// replacement, publication on successful return only — with two differences. Broadcast
    /// channels carry no cursor, so <c>context.streamSequenceToken</c> is always <c>None</c>;
    /// and there is no pulling agent, so a throwing hook fails the publisher's call rather than
    /// being retried (a broadcast publish is a direct fan-out grain call, not a queued one). In
    /// the default awaited mode the publisher's <c>Publish</c> faults with an
    /// <c>AggregateException</c> carrying the hook's exception; under
    /// <c>BroadcastChannelOptions.FireAndForgetDelivery</c> it is logged by
    /// <c>BroadcastChannelWriter</c> instead.
    /// </para>
    /// <para>
    /// <b>An item of the wrong type faults the publish too, rather than vanishing.</b> Orleans
    /// checks the runtime type inside the consumer extension and, on a mismatch, routes the item
    /// into the subscription's error callback as an <c>InvalidCastException</c> naming both types
    /// — not into the hook. This runtime faults that callback, so the mismatch reaches the
    /// publisher (or the log, in fire-and-forget mode) on exactly the path a throwing hook takes.
    /// Completing it quietly, which is the obvious thing to write, would let the extension report
    /// the item as delivered while no hook ever saw it. The hook is not entered, no state is
    /// published, and the subscription stays healthy: the next correctly-typed publish is
    /// delivered normally.
    /// </para>
    /// <para>
    /// Several <c>onBroadcast</c> operations are allowed, one per (provider, namespace) pair.
    /// Rejected in combination with <c>statelessWorker</c>, for the reasons <c>onStream</c>'s
    /// remarks give.
    /// </para>
    /// </remarks>
    [<CustomOperation("onBroadcast")>]
    member _.OnBroadcast<'State, 'Item>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            providerName: string,
            channelNamespace: string,
            hook: StreamHook<'Actor, 'Key, 'State, 'Item>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail
                DefinitionStage
                $"'onBroadcast' for provider '{providerName}' and namespace '{channelNamespace}' on grain type '{draft.Contract.GrainTypeName}' requires a hook."

        let attach =
            FunctionalChannelAttach(fun subscription delivery ->
                subscription.Attach<'Item>(
                    Func<'Item, Task>(fun item -> delivery.Invoke(box item, null)),
                    // An item whose runtime type is not 'Item never reaches the delivery callback
                    // above: BroadcastChannelConsumerExtension.Callback<T>.OnPublished routes it
                    // into THIS one, as an InvalidCastException naming both types. Completing it
                    // successfully would be silent data loss -- the extension would go on to
                    // EmitItemDelivered and the publisher's own Publish would report success
                    // while the item vanished with no signal anywhere. Faulting instead
                    // propagates through BroadcastChannelWriter.PublishToSubscriber, which
                    // rethrows in the default awaited mode (the publisher gets the mismatch) and
                    // logs it in fire-and-forget mode -- the same visibility a throwing hook has.
                    Func<exn, Task>(fun error -> Task.FromException error)
                ))

        let adapter =
            FunctionalStreamHookAdapter(fun key core currentState item ->
                task {
                    let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                    let! next = hook context (unbox<'State> currentState) (unbox<'Item> item)
                    return box next
                })

        let declaration =
            { Attachment = ChannelAttachment attach
              ProviderName = providerName
              Namespace = channelNamespace
              ItemType = typeof<'Item>
              Adapter = adapter }

        DefinitionDraft.withState
            { draft with
                StreamBindings = draft.StreamBindings @ [ declaration ] }

    /// <summary>
    /// Multiplex this grain type across up to <paramref name="maxLocalWorkers"/> local
    /// activations per silo (Orleans' <c>StatelessWorkerPlacement</c>). Mutually exclusive with
    /// <c>placement</c>; rejects <c>stateFrom</c>, <c>usePersistentState</c>, <c>onReminder</c>,
    /// and <c>collectionAge</c> at sealing, in either declaration order -- durable identity and
    /// idle collection age are both meaningless for activations Orleans may create, deactivate,
    /// and re-create at will. It also rejects <c>onStream</c> and <c>onBroadcast</c>: Orleans'
    /// <c>SiloStreamProviderRuntime.BindExtension</c> refuses to bind a consumer extension to a
    /// stateless worker at all, and implicit delivery addresses one activation identity derived
    /// from the stream key.
    /// </summary>
    [<CustomOperation("statelessWorker")>]
    member _.StatelessWorker<'State>
        (state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>, maxLocalWorkers: int)
        =
        let draft = state.State

        DefinitionDraft.withState
            { draft with
                Placement =
                    DefinitionDraft.singlePlacement
                        "statelessWorker"
                        draft.Contract.GrainTypeName
                        draft.Placement
                        (StatelessWorker maxLocalWorkers) }

    /// <summary>
    /// Select one stock Orleans placement strategy for this grain type. Mutually exclusive with
    /// <c>statelessWorker</c> and with a second <c>placement</c> operation.
    /// </summary>
    [<CustomOperation("placement")>]
    member _.Placement<'State>
        (state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>, strategy: PlacementStrategy)
        =
        let draft = state.State

        DefinitionDraft.withState
            { draft with
                Placement =
                    DefinitionDraft.singlePlacement "placement" draft.Contract.GrainTypeName draft.Placement (Strategy strategy) }

    /// <summary>
    /// Hook one Orleans grain-lifecycle stage. Each stage accepts at most one hook.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>Activate</c> is rejected.</b> Not because it coincides exactly with where
    /// <c>OnActivateAsync</c> runs -- verified by an integration probe, it does not: Orleans runs
    /// the entire numbered stage sequence <c>First, SetupState, Activate, Last</c> to completion
    /// FIRST, and only then runs <c>OnActivateAsync</c> (state initialization, the
    /// <c>onActivate</c> hook, reminders, timers, in that order -- see
    /// <c>FunctionalLifecycle.activate</c>) as a separate later step; a hook at the numbered
    /// <c>Activate</c> stage would in fact run BEFORE all of that, in the same no-state category
    /// as <c>First</c> and <c>SetupState</c>. It is rejected because letting an application aim at
    /// the stage literally named "Activate" while state is not yet initialized there would be a
    /// footgun, and because "the operation for activation-time behavior" should have exactly one
    /// name. <c>onActivate</c> is that name: declaring <c>onLifecycle Activate</c> fails at
    /// sealing with a diagnostic pointing at it.
    /// </para>
    /// <para>
    /// <b>Why the hook never carries <c>'State</c>, at any accepted stage.</b> All three accepted
    /// stages -- <c>First</c>, <c>SetupState</c>, and <c>Last</c> -- run strictly before
    /// <c>OnActivateAsync</c>, which is where the functional runtime's own primary state
    /// initializes (<c>env.State.Initialize</c>, step 3 of the activation order). There is
    /// therefore no "post-state" stage among the four at all -- not even <c>Last</c>, the final
    /// one: <c>'State</c> cannot be meaningful at any of them, so the question of a
    /// state-carrying hook shape for a post-state stage does not arise, and every accepted stage
    /// uses the same context-only, no-state shape. A hook that genuinely needs to read the
    /// current stored value can still do so explicitly through <c>context.persistentState</c> for
    /// a persistent primary (Orleans' own pre-load value before <c>SetupState</c>, or the
    /// unchanged prior value after); <c>onActivate</c> remains the one hook whose <c>'State</c>
    /// parameter is the functional runtime's own, meaningfully initialized value.
    /// </para>
    /// </remarks>
    [<CustomOperation("onLifecycle")>]
    member _.OnLifecycle<'State>
        (
            state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>,
            stage: LifecycleStage,
            hook: LifecycleHook<'Actor, 'Key>
        ) =
        let draft = state.State
        let grainTypeName = draft.Contract.GrainTypeName

        if stage = Activate then
            fail
                DefinitionStage
                $"'onLifecycle Activate' is rejected for grain type '{grainTypeName}': the Activate stage is where 'onActivate' already runs (state initialization, then onActivate, then reminders, then timers). Use 'onActivate' instead -- a single stage should not have two ways to hook it."

        if draft.LifecycleHooks.ContainsKey stage then
            fail
                DefinitionStage
                $"'onLifecycle {stage}' is declared more than once for grain type '{grainTypeName}'. Each lifecycle stage accepts at most one hook."

        if obj.ReferenceEquals(hook, null) then
            fail DefinitionStage $"'onLifecycle {stage}' for grain type '{grainTypeName}' requires a hook."

        DefinitionDraft.withState
            { draft with
                LifecycleHooks = draft.LifecycleHooks.Add(stage, hook) }
