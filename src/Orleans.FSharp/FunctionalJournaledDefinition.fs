namespace Orleans.FSharp

open System
open System.Threading.Tasks
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// Which log-consistency provider a journaled definition names, and which storage provider that
/// provider writes through.
/// </summary>
[<ReferenceEquality>]
type internal JournalConfiguration =
    {
        /// The name of a registered <c>ILogViewAdaptorFactory</c>: the log-consistency provider.
        ProviderName: string
        /// <summary>
        /// The name of a registered <c>IGrainStorage</c>, or <c>None</c> for the silo's default
        /// one. Only consulted when the resolved provider reports
        /// <c>UsesStorageProvider</c>; both built-in providers do.
        /// </summary>
        StorageName: string option
    }

/// <summary>Accumulated, not yet sealed, journaled-definition configuration.</summary>
[<ReferenceEquality>]
type internal JournaledDraftState<'Actor, 'Key, 'Api, 'State, 'Event> =
    { Contract: GrainContract<'Actor, 'Key, 'Api>
      Initial: 'Key -> 'State
      Apply: 'State -> 'Event -> 'State
      Journal: JournalConfiguration option
      CollectionAge: TimeSpan option
      OnActivate: JournaledActivateHook<'Actor, 'Key, 'State> option
      OnDeactivate: JournaledDeactivateHook<'Actor, 'Key, 'State> option
      Placement: PlacementConfiguration option
      Handlers: Map<int, obj> }

/// <summary>
/// A sealed journaled definition: the contract, the initial state, the replay fold, one handler
/// per API field, and the named log-consistency provider its journal lives in.
/// </summary>
[<Sealed>]
type FunctionalJournaledGrainDefinition<'Actor, 'Key, 'Api, 'State, 'Event>
    internal (state: JournaledDraftState<'Actor, 'Key, 'Api, 'State, 'Event>) =

    /// <summary>The contract this definition hosts.</summary>
    member internal _.Contract = state.Contract

    /// <summary>The explicit Orleans grain type name.</summary>
    member internal _.GrainTypeName = state.Contract.GrainTypeName

    /// <summary>The declared initial state, before any event has been folded in.</summary>
    member internal _.Initial = state.Initial

    /// <summary>The replay fold.</summary>
    member internal _.Apply = state.Apply

    /// <summary>The named log-consistency provider and its storage.</summary>
    member internal _.Journal = state.Journal

    /// <summary>The configured idle collection age, when present.</summary>
    member internal _.CollectionAge = state.CollectionAge

    /// <summary>The activation hook, when configured.</summary>
    member internal _.OnActivate = state.OnActivate

    /// <summary>The deactivation hook, when configured.</summary>
    member internal _.OnDeactivate = state.OnDeactivate

    /// <summary>The configured placement, when <c>placement</c> was declared.</summary>
    member internal _.Placement = state.Placement

    /// <summary>Boxed handlers keyed by API-record field index.</summary>
    member internal _.Handlers = state.Handlers

    /// <summary>The boxed handler for one operation descriptor.</summary>
    member internal _.HandlerFor(operation: FunctionalOperation) = state.Handlers.[operation.Index]

    override _.ToString() =
        $"FunctionalJournaledGrainDefinition(grainType = '{state.Contract.GrainTypeName}', state = '{typeof<'State>.FullName}', event = '{typeof<'Event>.FullName}')"

/// <summary>
/// The seed state of a <c>journaledGrainFor</c> expression, before <c>initialEventState</c>
/// introduces the state type.
/// </summary>
[<Sealed>]
type FunctionalJournaledSeed<'Actor, 'Key, 'Api> internal (contract: GrainContract<'Actor, 'Key, 'Api>) =

    /// <summary>The contract this definition will host.</summary>
    member internal _.Contract = contract

/// <summary>
/// The state of a <c>journaledGrainFor</c> expression between <c>initialEventState</c> and
/// <c>apply</c>: the state type is known, the event type is not yet.
/// </summary>
[<Sealed>]
type FunctionalJournaledStateDraft<'Actor, 'Key, 'Api, 'State>
    internal (contract: GrainContract<'Actor, 'Key, 'Api>, initial: 'Key -> 'State) =

    /// <summary>The contract this definition will host.</summary>
    member internal _.Contract = contract

    /// <summary>The declared initial state.</summary>
    member internal _.Initial = initial

/// <summary>The intermediate state of a <c>journaledGrainFor</c> expression.</summary>
[<Sealed>]
type FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>
    internal (state: JournaledDraftState<'Actor, 'Key, 'Api, 'State, 'Event>) =

    /// <summary>The accumulated configuration.</summary>
    member internal _.State = state

/// <summary>Journaled-definition draft helpers shared by the computation-expression builder.</summary>
module internal JournaledDefinitionDraft =

    let withState (state: JournaledDraftState<'Actor, 'Key, 'Api, 'State, 'Event>) =
        FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>(state)

    /// <summary>Seal a journaled draft into an immutable definition.</summary>
    let run
        (draft: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>)
        : FunctionalJournaledGrainDefinition<'Actor, 'Key, 'Api, 'State, 'Event> =
        let state = draft.State
        let contract = state.Contract
        let grainTypeName = contract.GrainTypeName

        // A journal is the most durable attachment there is: the grain type name is part of the
        // storage key of every stored log. A derived grain type moves silently when the actor
        // brand is renamed, which would orphan the whole journal rather than a single record, so
        // the rule the ordinary definition applies to 'stateFrom' applies here unconditionally.
        if not contract.IsGrainTypeExplicit then
            fail
                DefinitionStage
                $"grain type '{grainTypeName}' is a journaled definition, but its contract derives 'grainType' from the actor brand '{typeof<'Actor>.FullName}' instead of declaring one explicitly. The grain type name is part of the storage key of the journal, so a brand rename would orphan every stored event. Declare an explicit 'grainType' on the contract."

        // Exactly one handler for every API-record field, the same completeness rule an ordinary
        // definition has.
        let missing =
            contract.Operations
            |> Array.filter (fun operation -> not (state.Handlers.ContainsKey operation.Index))
            |> Array.map (fun operation -> operation.FieldName)

        if missing.Length > 0 then
            let missingNames = String.Join(", ", missing)

            fail DefinitionStage $"grain type '{grainTypeName}' has no handler for API field(s) {missingNames}."

        // The provider is required rather than defaulted. LogStorage and StateStorage store
        // completely different things — the whole event log versus the latest view — under the
        // same storage key, and neither can read the other's records. Picking one silently for an
        // application that never said which it wanted would make the choice invisible in the
        // definition and irreversible in storage.
        let journal =
            match state.Journal with
            | Some journal -> journal
            | None ->
                fail
                    DefinitionStage
                    $"the journaled definition of grain type '{grainTypeName}' does not declare 'logProvider'. Name the registered log-consistency provider its journal lives in, for example logProvider \"LogStorage\" together with AddLogStorageBasedLogConsistencyProvider \"LogStorage\" on every hosting silo."

        if isBlank journal.ProviderName then
            fail DefinitionStage $"'logProvider' of grain type '{grainTypeName}' must be a non-blank name."

        if containsNul journal.ProviderName then
            fail
                DefinitionStage
                $"'logProvider' of grain type '{grainTypeName}' must not contain a NUL character."

        match journal.StorageName with
        | Some storageName when isBlank storageName ->
            fail DefinitionStage $"'journalStorage' of grain type '{grainTypeName}' must be a non-blank name."
        | Some storageName when containsNul storageName ->
            fail
                DefinitionStage
                $"'journalStorage' of grain type '{grainTypeName}' must not contain a NUL character."
        | _ -> ()

        // Spec 004 item 2 meets item 3: an Orleans transaction can abort, and a confirmed journal
        // append cannot be undone. The log-view adaptor is not a transaction participant — it
        // registers nothing with the transaction manager and has no prepare/abort of its own — so
        // a transactional journaled operation would leave events behind after a rollback.
        let transactional =
            contract.Operations
            |> Array.filter (fun operation -> operation.Transaction.IsSome)
            |> Array.map (fun operation -> $"'{operation.FieldName}'")

        if transactional.Length > 0 then
            let names = String.Join(", ", transactional)

            fail
                DefinitionStage
                $"grain type '{grainTypeName}' is a journaled definition, but its contract declares 'transactional' for operation(s) {names}. An Orleans log-view adaptor is not a transaction participant, so events this operation confirmed would survive an abort of the transaction that raised them. Declare the operation without 'transactional', or keep the transactional state in an ordinary 'grainFor' definition."

        // Stateless-worker placement means many activations of one grain identity, each with its
        // own log-view adaptor over the same storage key. They would fold the same journal
        // independently and race each other's appends through the adaptor's e-tag retry loop, so
        // it is refused rather than left to produce interleaved logs.
        match state.Placement with
        | Some(StatelessWorker _) ->
            fail
                DefinitionStage
                $"grain type '{grainTypeName}' is a journaled definition and cannot use 'statelessWorker'. A stateless worker has many activations of the same grain identity, each of which would host its own log-view adaptor over the same journal and race the others' appends."
        | _ -> ()

        // A definition whose fold cannot run is a definition error, not a runtime surprise.
        if obj.ReferenceEquals(state.Apply, null) then
            fail DefinitionStage $"grain type '{grainTypeName}' has no 'apply' fold."

        FunctionalJournaledGrainDefinition<'Actor, 'Key, 'Api, 'State, 'Event>(state)

/// <summary>
/// The <c>journaledGrainFor</c> computation expression: the initial state, the replay fold, one
/// handler per API field, the named log-consistency provider, and the lifecycle hooks a journal
/// admits.
/// </summary>
/// <remarks>
/// <para>
/// <c>initialEventState</c> and <c>apply</c> are the first two operations, in that order, and are
/// both required: the first introduces the state type and the second the event type, so every
/// later operation is typed against both. Declaring them out of order is a compile error naming
/// the operation.
/// </para>
/// <para>
/// The operations an ordinary <c>grainFor</c> definition has and this one deliberately does not:
/// <c>defaultState</c>/<c>initialState</c> (replaced by <c>initialEventState</c>),
/// <c>stateFrom</c> and <c>usePersistentState</c> (the journal is the state — a second durable
/// holder on the same activation would be a second source of truth with no ordering against the
/// journal), <c>transactionalStateFrom</c> (the adaptor is not a transaction participant),
/// <c>onStream</c>/<c>onBroadcast</c>/<c>onTimer</c>/<c>onReminder</c> (every one of them is a
/// whole-state-replacement hook, which a journaled definition has no way to honour), and
/// <c>statelessWorker</c>. Each is recorded in specs/004-orleans-parity-extensions/spec.md item 3
/// with the mechanism that rules it out.
/// </para>
/// </remarks>
[<Sealed>]
type FunctionalJournaledGrainDefinitionBuilder<'Actor, 'Key, 'Api> internal (contract: GrainContract<'Actor, 'Key, 'Api>) =

    /// <summary>Start a journaled definition seed for the contract.</summary>
    member _.Yield(_: unit) : FunctionalJournaledSeed<'Actor, 'Key, 'Api> =
        FunctionalJournaledSeed<'Actor, 'Key, 'Api>(contract)

    /// <summary>Validate and seal the draft into an immutable journaled definition.</summary>
    member _.Run<'State, 'Event>
        (draft: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>)
        : FunctionalJournaledGrainDefinition<'Actor, 'Key, 'Api, 'State, 'Event> =
        JournaledDefinitionDraft.run draft

    /// <summary>
    /// The state a grain has before any event: the seed of the replay fold, derived from the
    /// domain key.
    /// </summary>
    /// <remarks>
    /// It is re-derived on every activation of a grain whose journal has never been written, and
    /// it must therefore be a pure function of the key. The two built-in providers disagree about
    /// whether a seeded view survives their first storage read, so the runtime re-materializes
    /// this value rather than trusting either of them.
    /// </remarks>
    [<CustomOperation("initialEventState")>]
    member _.InitialEventState<'State>(seed: FunctionalJournaledSeed<'Actor, 'Key, 'Api>, factory: 'Key -> 'State) =
        if obj.ReferenceEquals(factory, null) then
            fail DefinitionStage "'initialEventState' requires a state factory."

        FunctionalJournaledStateDraft<'Actor, 'Key, 'Api, 'State>(seed.Contract, factory)

    /// <summary>The replay fold: how one event changes the state.</summary>
    /// <remarks>
    /// <para>
    /// <b>It must be pure, and the API is shaped to make impurity hard.</b> It is a
    /// <c>'State -&gt; 'Event -&gt; 'State</c> function, not a method on the state and not a
    /// <c>Task</c>-returning one: it receives no invocation context, no grain factory, no service
    /// provider, no cancellation token, and no key, so it cannot call another grain, read storage,
    /// start a timer, or observe the clock through anything the runtime hands it.
    /// </para>
    /// <para>
    /// Purity is load-bearing because the fold runs <b>twice for the same event</b>, at two
    /// different times, and both runs must agree. It runs once when the event is raised, to move
    /// this activation's view forward, and again on every later activation that replays the
    /// journal from storage — hours or months later, in a different process. A fold that read the
    /// clock, generated an identifier, or called a service would produce a different state on
    /// replay than the one the application saw when the event was raised, and the difference would
    /// be silent.
    /// </para>
    /// <para>
    /// An exception thrown by the fold is <b>not</b> silently swallowed: Orleans' adaptors catch
    /// and log it and carry on with an unchanged view, so the runtime records the failure and
    /// fails the turn instead of returning a view that skipped an event.
    /// </para>
    /// </remarks>
    [<CustomOperation("apply")>]
    member _.Apply<'State, 'Event>
        (draft: FunctionalJournaledStateDraft<'Actor, 'Key, 'Api, 'State>, fold: 'State -> 'Event -> 'State)
        =
        if obj.ReferenceEquals(fold, null) then
            fail DefinitionStage "'apply' requires a fold function."

        JournaledDefinitionDraft.withState
            { Contract = draft.Contract
              Initial = draft.Initial
              Apply = fold
              Journal = None
              CollectionAge = None
              OnActivate = None
              OnDeactivate = None
              Placement = None
              Handlers = Map.empty }

    /// <summary>
    /// Bind one handler to the operation identified by the selector. The handler returns the
    /// events to append and the reply; it never returns a replacement state.
    /// </summary>
    [<CustomOperation("handle")>]
    member _.Handle<'State, 'Event, 'Argument, 'Reply>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            selector: OperationSelector<'Api, 'Argument, 'Reply>,
            handler: JournaledHandler<'Actor, 'Key, 'State, 'Event, 'Argument, 'Reply>
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

        JournaledDefinitionDraft.withState
            { draft with
                Handlers = draft.Handlers.Add(operation.Index, box handler) }

    /// <summary>
    /// Name the registered log-consistency provider this definition's journal lives in.
    /// </summary>
    /// <param name="providerName">
    /// The name a silo registered an <c>ILogViewAdaptorFactory</c> under — for example
    /// <c>AddLogStorageBasedLogConsistencyProvider "LogStorage"</c> or
    /// <c>AddStateStorageBasedLogConsistencyProvider "StateStorage"</c>. Silo startup validation
    /// fails if the name does not resolve.
    /// </param>
    [<CustomOperation("logProvider")>]
    member _.LogProvider<'State, 'Event>
        (state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>, providerName: string)
        =
        let draft = state.State

        let journal =
            match draft.Journal with
            | Some existing ->
                fail
                    DefinitionStage
                    $"'logProvider' is declared more than once for grain type '{draft.Contract.GrainTypeName}' (already '{existing.ProviderName}'). A repeated singleton operation is a definition error."
            | None ->
                { ProviderName = providerName
                  StorageName = None }

        JournaledDefinitionDraft.withState { draft with Journal = Some journal }

    /// <summary>
    /// Name the storage provider the log-consistency provider writes through. Optional: without
    /// it the silo's default <c>IGrainStorage</c> is used, exactly as an unattributed
    /// <c>JournaledGrain</c> would.
    /// </summary>
    [<CustomOperation("journalStorage")>]
    member _.JournalStorage<'State, 'Event>
        (state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>, storageName: string)
        =
        let draft = state.State

        let journal =
            match draft.Journal with
            | None ->
                fail
                    DefinitionStage
                    $"'journalStorage' of grain type '{draft.Contract.GrainTypeName}' must follow 'logProvider'."
            | Some existing when existing.StorageName.IsSome ->
                fail
                    DefinitionStage
                    $"'journalStorage' is declared more than once for grain type '{draft.Contract.GrainTypeName}' (already '{existing.StorageName.Value}'). A repeated singleton operation is a definition error."
            | Some existing ->
                { existing with
                    StorageName = Some storageName }

        JournaledDefinitionDraft.withState { draft with Journal = Some journal }

    /// <summary>Set the Orleans idle collection age for this grain type.</summary>
    [<CustomOperation("collectionAge")>]
    member _.CollectionAge<'State, 'Event>
        (state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>, age: TimeSpan)
        =
        let draft = state.State

        JournaledDefinitionDraft.withState
            { draft with
                CollectionAge =
                    DefinitionDraft.single "collectionAge" draft.Contract.GrainTypeName draft.CollectionAge age }

    /// <summary>Choose an Orleans placement strategy for this grain type.</summary>
    /// <remarks>
    /// <c>statelessWorker</c> has no journaled counterpart and is refused at sealing; every other
    /// strategy is orthogonal to the journal, which is addressed by grain identity rather than by
    /// activation.
    /// </remarks>
    [<CustomOperation("placement")>]
    member _.Placement<'State, 'Event>
        (state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>, strategy: PlacementStrategy)
        =
        let draft = state.State

        JournaledDefinitionDraft.withState
            { draft with
                Placement =
                    DefinitionDraft.singlePlacement
                        "placement"
                        draft.Contract.GrainTypeName
                        draft.Placement
                        (Strategy strategy) }

    /// <summary>
    /// Run a hook once the journal has been replayed and before the activation serves its first
    /// call. It returns no replacement state: the state is the fold of the journal.
    /// </summary>
    [<CustomOperation("onActivate")>]
    member _.OnActivate<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            hook: JournaledActivateHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail DefinitionStage $"'onActivate' of grain type '{draft.Contract.GrainTypeName}' requires a hook."

        JournaledDefinitionDraft.withState
            { draft with
                OnActivate = DefinitionDraft.single "onActivate" draft.Contract.GrainTypeName draft.OnActivate hook }

    /// <summary>Run a hook when the activation is deactivating.</summary>
    [<CustomOperation("onDeactivate")>]
    member _.OnDeactivate<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            hook: JournaledDeactivateHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail DefinitionStage $"'onDeactivate' of grain type '{draft.Contract.GrainTypeName}' requires a hook."

        JournaledDefinitionDraft.withState
            { draft with
                OnDeactivate =
                    DefinitionDraft.single "onDeactivate" draft.Contract.GrainTypeName draft.OnDeactivate hook }
