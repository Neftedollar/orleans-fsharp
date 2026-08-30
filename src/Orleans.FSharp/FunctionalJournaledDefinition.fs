namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Threading.Tasks
open Orleans.BroadcastChannel
open Orleans.EventSourcing
open Orleans.Runtime
open Orleans.Streams
open Orleans.Streams.Core
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

/// <summary>A declared durable reminder whose successful tick raises an atomic event batch.</summary>
[<ReferenceEquality>]
type internal JournaledReminderDeclaration<'Actor, 'Key, 'State, 'Event> =
    { Name: string
      DueTime: TimeSpan
      Period: TimeSpan
      Hook: JournaledReminderHook<'Actor, 'Key, 'State, 'Event> }

/// <summary>A declared activation-local timer whose successful tick raises an atomic event batch.</summary>
[<ReferenceEquality>]
type internal JournaledTimerDeclaration<'Actor, 'Key, 'State, 'Event> =
    { Name: string
      DueTime: TimeSpan
      Period: TimeSpan
      Interleave: bool
      KeepAlive: bool
      Hook: JournaledTimerHook<'Actor, 'Key, 'State, 'Event> }

/// <summary>Accumulated, not yet sealed, journaled-definition configuration.</summary>
[<ReferenceEquality>]
type internal JournaledDraftState<'Actor, 'Key, 'Api, 'State, 'Event> =
    {
        /// The contract this definition is being built for.
        Contract: GrainContract<'Actor, 'Key, 'Api>
        /// The declared initial state, before any event has been folded in.
        Initial: 'Key -> 'State
        /// The replay fold.
        Apply: 'State -> 'Event -> 'State
        /// The named log-consistency provider and its storage, when 'logProvider' has been declared.
        Journal: JournalConfiguration option
        /// The definition-level durable payload codec override for journal views and entries.
        JournalCodec: FunctionalPersistenceCodec option
        /// The schema version and upcasters for materialized journal state/snapshots.
        StateSchema: FunctionalSchema<'State> option
        /// The schema version and upcasters for journal events.
        EventSchema: FunctionalSchema<'Event> option
        /// Resolves the typed storage used by Orleans' CustomStorage provider.
        CustomStorage: (IServiceProvider -> IFunctionalJournalStorage<'Key, 'State, 'Event>) option
        /// The per-definition snapshot override; absence inherits the silo default.
        SnapshotPolicy: FunctionalJournalSnapshotPolicy<'State> option
        /// The declared idle collection age, when 'collectionAge' has been declared.
        CollectionAge: TimeSpan option
        /// The declared activation hook, when 'onActivate' has been declared.
        OnActivate: JournaledActivateHook<'Actor, 'Key, 'State> option
        /// The declared deactivation hook, when 'onDeactivate' has been declared.
        OnDeactivate: JournaledDeactivateHook<'Actor, 'Key, 'State> option
        /// Declared reminders, in declaration order.
        Reminders: JournaledReminderDeclaration<'Actor, 'Key, 'State, 'Event> list
        /// Declared timers, in declaration order.
        Timers: JournaledTimerDeclaration<'Actor, 'Key, 'State, 'Event> list
        /// Declared implicit stream and broadcast subscriptions, in declaration order.
        StreamBindings: FunctionalStreamDeclaration list
        /// Notification raised when the tentative state may have changed.
        OnTentativeStateChanged: JournaledStateChangedHook<'Actor, 'Key, 'State> option
        /// Notification raised when the confirmed state may have changed.
        OnStateChanged: JournaledStateChangedHook<'Actor, 'Key, 'State> option
        /// Notification raised when the log-consistency protocol reports a connection issue.
        OnConnectionIssue: JournaledConnectionIssueHook<'Actor, 'Key, 'State> option
        /// Notification raised when a reported connection issue is resolved.
        OnConnectionIssueResolved: JournaledConnectionIssueHook<'Actor, 'Key, 'State> option
        /// The declared placement configuration, when 'placement' has been declared.
        Placement: PlacementConfiguration option
        /// Activation-scoped Orleans migration-participant factories, in declaration order.
        MigrationParticipants: FunctionalMigrationParticipantFactory<'Actor, 'Key> list
        /// Boxed handlers keyed by API-record field index.
        Handlers: Map<int, obj>
    }

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

    /// <summary>The definition-level journal payload codec override, when configured.</summary>
    member internal _.JournalCodec = state.JournalCodec

    /// <summary>The materialized-state schema and upcaster pipeline, when configured.</summary>
    member internal _.StateSchema = state.StateSchema

    /// <summary>The event schema and upcaster pipeline, when configured.</summary>
    member internal _.EventSchema = state.EventSchema

    /// <summary>The typed custom-storage resolver, when declared.</summary>
    member internal _.CustomStorage = state.CustomStorage

    /// <summary>The per-definition snapshot override, when declared.</summary>
    member internal _.SnapshotPolicy = state.SnapshotPolicy

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

    /// <summary>The tentative-state notification hook, when configured.</summary>
    member internal _.OnTentativeStateChanged = state.OnTentativeStateChanged

    /// <summary>The confirmed-state notification hook, when configured.</summary>
    member internal _.OnStateChanged = state.OnStateChanged

    /// <summary>The connection-issue hook, when configured.</summary>
    member internal _.OnConnectionIssue = state.OnConnectionIssue

    /// <summary>The connection-issue-resolved hook, when configured.</summary>
    member internal _.OnConnectionIssueResolved = state.OnConnectionIssueResolved

    /// <summary>The configured placement, when <c>placement</c> was declared.</summary>
    member internal _.Placement = state.Placement

    /// <summary>Activation-scoped migration-participant factories in declaration order.</summary>
    member internal _.MigrationParticipants = state.MigrationParticipants

    /// <summary>Boxed handlers keyed by API-record field index.</summary>
    member internal _.Handlers = state.Handlers

    /// <summary>The boxed handler for one operation descriptor.</summary>
    /// <param name="operation">The operation descriptor whose handler to look up.</param>
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

    /// <summary>Wrap an accumulated draft state back into a draft value.</summary>
    /// <param name="state">The accumulated state to wrap.</param>
    let withState (state: JournaledDraftState<'Actor, 'Key, 'Api, 'State, 'Event>) =
        FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>(state)

    /// <summary>Seal a journaled draft into an immutable definition.</summary>
    /// <param name="draft">The accumulated draft to validate and seal.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the draft fails sealing validation: a derived (not explicit) 'grainType'; a
    /// missing handler for an API field; no 'logProvider' declared, or a blank or NUL-containing
    /// 'logProvider' or 'journalStorage' name; 'transactional' declared on any operation (a
    /// log-view adaptor is not a transaction participant); 'placement' combined with a stateless
    /// worker; or no 'apply' fold declared.
    /// </exception>
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
            fail DefinitionStage $"'logProvider' of grain type '{grainTypeName}' must not contain a NUL character."

        match journal.StorageName with
        | Some storageName when isBlank storageName ->
            fail DefinitionStage $"'journalStorage' of grain type '{grainTypeName}' must be a non-blank name."
        | Some storageName when containsNul storageName ->
            fail DefinitionStage $"'journalStorage' of grain type '{grainTypeName}' must not contain a NUL character."
        | _ -> ()

        match state.CustomStorage, journal.StorageName with
        | Some _, Some _ ->
            fail
                DefinitionStage
                $"grain type '{grainTypeName}' declares both 'customStorage' and 'journalStorage'. Orleans' CustomStorage log-consistency provider calls the grain-owned storage interface directly and reports UsesStorageProvider = false, so an IGrainStorage name would never be used. Remove 'journalStorage'."
        | _ -> ()

        match state.SnapshotPolicy with
        | Some policy when obj.ReferenceEquals(policy, null) ->
            fail DefinitionStage $"'snapshotPolicy' of grain type '{grainTypeName}' cannot be null."
        | Some(FunctionalJournalSnapshotPolicy.Every eventCount) when eventCount <= 0 ->
            fail
                DefinitionStage
                $"'snapshotPolicy' Every of grain type '{grainTypeName}' requires a positive event count, but {eventCount} was supplied."
        | Some(FunctionalJournalSnapshotPolicy.When predicate) when obj.ReferenceEquals(predicate, null) ->
            fail
                DefinitionStage
                $"'snapshotPolicy' When of grain type '{grainTypeName}' requires a predicate."
        | Some _ when state.CustomStorage.IsNone ->
            fail
                DefinitionStage
                $"grain type '{grainTypeName}' declares 'snapshotPolicy' but no 'customStorage'. LogStorage retains the complete event log and StateStorage persists the latest view on every write; an application-controlled snapshot is available only through Orleans' CustomStorage provider. Declare 'customStorage', or remove 'snapshotPolicy'."
        | Some FunctionalJournalSnapshotPolicy.Inherit
        | None -> ()
        | Some _ -> ()

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

        let seenBindings = HashSet<struct (bool * string * string)>(HashIdentity.Structural)

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
/// Reminder, timer, stream, and broadcast hooks return events instead of replacement state, so
/// they have the same durable semantics as a journaled request handler. Operations which would
/// introduce a second source of truth (<c>stateFrom</c>/<c>usePersistentState</c>), an Orleans
/// transaction participant, or stateless-worker activations remain deliberately unavailable.
/// </para>
/// </remarks>
[<Sealed>]
type FunctionalJournaledGrainDefinitionBuilder<'Actor, 'Key, 'Api>
    internal (contract: GrainContract<'Actor, 'Key, 'Api>) =

    /// <summary>Start a journaled definition seed for the contract.</summary>
    member _.Yield(_: unit) : FunctionalJournaledSeed<'Actor, 'Key, 'Api> =
        FunctionalJournaledSeed<'Actor, 'Key, 'Api>(contract)

    /// <summary>Validate and seal the draft into an immutable journaled definition.</summary>
    /// <param name="draft">The accumulated draft to validate and seal.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when sealing validation fails; see
    /// <see cref="M:Orleans.FSharp.JournaledDefinitionDraft.run"/> for the complete list of checks.
    /// </exception>
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
    /// <param name="factory">Produces the initial (pre-fold) state from the activation's domain key.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when <paramref name="factory"/> is null.</exception>
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
    /// <param name="fold">The replay fold: how one event changes the state.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when <paramref name="fold"/> is null.</exception>
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
              JournalCodec = None
              StateSchema = None
              EventSchema = None
              CustomStorage = None
              SnapshotPolicy = None
              CollectionAge = None
              OnActivate = None
              OnDeactivate = None
              Reminders = []
              Timers = []
              StreamBindings = []
              OnTentativeStateChanged = None
              OnStateChanged = None
              OnConnectionIssue = None
              OnConnectionIssueResolved = None
              Placement = None
              MigrationParticipants = []
              Handlers = Map.empty }

    /// <summary>
    /// Bind one handler to the operation identified by the selector. The handler returns the
    /// events to append and the reply; it never returns a replacement state.
    /// </summary>
    /// <param name="selector">The API field to bind the handler to.</param>
    /// <param name="handler">The handler to run for the operation.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; when that field already has a handler; or when
    /// <paramref name="handler"/> is null.
    /// </exception>
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
    /// Bind one <b>query</b> handler -- reply only, no events -- to the operation identified by the
    /// selector. The operation must be declared <c>readOnly</c> in the contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sugar over <c>handle</c>, and nothing more: the handler is wrapped into an ordinary
    /// <see cref="T:Orleans.FSharp.JournaledHandler`6"/> that raises the empty event list, stored
    /// in the same handler map, and dispatched down the same path. What it removes is the
    /// <c>return ([]: 'Event list), reply</c> ceremony -- including the type annotation the empty
    /// list needs when nothing else in the handler mentions the event type -- from an operation
    /// that was never going to append anything.
    /// </para>
    /// <para>
    /// <b>Why <c>readOnly</c> is required.</b> A journaled operation changes the grain only by
    /// raising events, so a handler that cannot raise any is an operation that cannot change the
    /// grain. On a read-only operation that is already the rule the runtime enforces -- it refuses
    /// the append outright, because such an operation may run beside another turn and its events
    /// could be ordered against nothing -- so the sugar states what was true anyway. Anywhere else
    /// it would silently turn a write into a no-op. The rule is strict on purpose: relaxing it
    /// later is additive, tightening it later would break every definition that had leaned on the
    /// looser form.
    /// </para>
    /// </remarks>
    /// <param name="selector">The API field to bind the handler to.</param>
    /// <param name="handler">The query handler to run for the operation; it returns the reply only.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; when that field already has a handler; when
    /// <paramref name="handler"/> is null; or when the resolved operation is not declared
    /// <c>readOnly</c> in the contract.
    /// </exception>
    [<CustomOperation("handleQuery")>]
    member _.HandleQuery<'State, 'Event, 'Argument, 'Reply>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            selector: OperationSelector<'Api, 'Argument, 'Reply>,
            handler: QueryHandler<'Actor, 'Key, 'State, 'Argument, 'Reply>
        ) =
        let draft = state.State
        let operation = draft.Contract.Resolve("handleQuery", selector)

        if draft.Handlers.ContainsKey operation.Index then
            fail
                DefinitionStage
                $"API field '{operation.FieldName}' of grain type '{draft.Contract.GrainTypeName}' already has a handler."

        if obj.ReferenceEquals(handler, null) then
            fail
                DefinitionStage
                $"'handleQuery' for API field '{operation.FieldName}' of grain type '{draft.Contract.GrainTypeName}' requires a handler."

        if not operation.IsReadOnly then
            fail
                DefinitionStage
                $"'handleQuery' binds API field '{operation.FieldName}' of grain type '{draft.Contract.GrainTypeName}', which is not declared 'readOnly'. A query handler raises no events, and a journaled operation changes the grain only by raising them, so this operation could never change anything. Declare the operation 'readOnly' in the contract, or use 'handle' and return the events explicitly."

        let wrapped: JournaledHandler<'Actor, 'Key, 'State, 'Event, 'Argument, 'Reply> =
            fun context current argument ->
                task {
                    let! reply = handler context current argument
                    return ([]: 'Event list), reply
                }

        JournaledDefinitionDraft.withState
            { draft with
                Handlers = draft.Handlers.Add(operation.Index, box wrapped) }

    /// <summary>
    /// Bind one <b>streaming</b> handler to the operation identified by the selector. Spec 004
    /// item 6.
    /// </summary>
    /// <remarks>
    /// The same <see cref="T:Orleans.FSharp.StreamHandler`5"/> shape an ordinary definition uses,
    /// deliberately: a streaming handler of a journaled definition raises no events for exactly the
    /// reason it publishes no replacement state — it produces across many turns of the activation,
    /// so an append from it could not be ordered against the appends of the turns it overlaps. It
    /// reads the confirmed state as it stood when the enumeration started.
    /// </remarks>
    /// <param name="selector">The streaming API field to bind the handler to.</param>
    /// <param name="handler">The streaming handler to run for the operation.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; when that field already has a handler; or when
    /// <paramref name="handler"/> is null.
    /// </exception>
    [<CustomOperation("handleStream")>]
    member _.HandleStream<'State, 'Event, 'Argument, 'Item>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            selector: StreamSelector<'Api, 'Argument, 'Item>,
            handler: StreamHandler<'Actor, 'Key, 'State, 'Argument, 'Item>
        ) =
        let draft = state.State
        let operation = draft.Contract.ResolveStream("handleStream", selector)

        if draft.Handlers.ContainsKey operation.Index then
            fail
                DefinitionStage
                $"API field '{operation.FieldName}' of grain type '{draft.Contract.GrainTypeName}' already has a handler."

        if obj.ReferenceEquals(handler, null) then
            fail
                DefinitionStage
                $"'handleStream' for API field '{operation.FieldName}' of grain type '{draft.Contract.GrainTypeName}' requires a handler."

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
    /// <exception cref="System.InvalidOperationException">Thrown when 'logProvider' is already declared for this draft.</exception>
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
    /// Override the silo-wide payload codec for this journal's encoded state and event cells.
    /// Existing entries retain their stored codec identifier and remain readable.
    /// </summary>
    [<CustomOperation("journalCodec")>]
    member _.JournalCodec<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            codec: FunctionalPersistenceCodec
        ) =
        let draft = state.State

        if obj.ReferenceEquals(codec, null) then
            fail
                DefinitionStage
                $"'journalCodec' of grain type '{draft.Contract.GrainTypeName}' cannot be null."

        if draft.JournalCodec.IsSome then
            fail
                DefinitionStage
                $"'journalCodec' is declared more than once for grain type '{draft.Contract.GrainTypeName}'. A repeated singleton operation is a definition error."

        JournaledDefinitionDraft.withState
            { draft with
                JournalCodec = Some codec }

    /// <summary>
    /// Version materialized journal state and snapshots, and register the typed upcasters needed
    /// to read older durable views. Records written before this feature have schema version zero.
    /// </summary>
    [<CustomOperation("stateSchema")>]
    member _.StateSchema<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            schema: FunctionalSchema<'State>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(schema, null) then
            fail
                DefinitionStage
                $"'stateSchema' of grain type '{draft.Contract.GrainTypeName}' cannot be null."

        if draft.StateSchema.IsSome then
            fail
                DefinitionStage
                $"'stateSchema' is declared more than once for grain type '{draft.Contract.GrainTypeName}'. A repeated singleton operation is a definition error."

        JournaledDefinitionDraft.withState
            { draft with
                StateSchema = Some schema }

    /// <summary>
    /// Version journal entries and register the typed upcasters needed to replay older events.
    /// Records written before this feature have schema version zero.
    /// </summary>
    [<CustomOperation("eventSchema")>]
    member _.EventSchema<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            schema: FunctionalSchema<'Event>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(schema, null) then
            fail
                DefinitionStage
                $"'eventSchema' of grain type '{draft.Contract.GrainTypeName}' cannot be null."

        if draft.EventSchema.IsSome then
            fail
                DefinitionStage
                $"'eventSchema' is declared more than once for grain type '{draft.Contract.GrainTypeName}'. A repeated singleton operation is a definition error."

        JournaledDefinitionDraft.withState
            { draft with
                EventSchema = Some schema }

    /// <summary>
    /// Name the storage provider the log-consistency provider writes through. Optional: without
    /// it the silo's default <c>IGrainStorage</c> is used, exactly as an unattributed
    /// <c>JournaledGrain</c> would.
    /// </summary>
    /// <param name="storageName">The registered <c>IGrainStorage</c> name the log-consistency provider writes through.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when 'journalStorage' is declared before 'logProvider', or when it is already
    /// declared for this draft.
    /// </exception>
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

    /// <summary>
    /// Supply the typed application storage implemented behind Orleans' CustomStorage
    /// log-consistency provider. The resolver runs once per activation against that activation's
    /// service provider.
    /// </summary>
    /// <remarks>
    /// Register <c>AddCustomStorageBasedLogConsistencyProvider</c> under the name supplied to
    /// <c>logProvider</c>. Silo startup rejects a different provider implementation instead of
    /// leaving Orleans' adaptor cast to fail on first activation.
    /// </remarks>
    [<CustomOperation("customStorage")>]
    member _.CustomStorage<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            resolve: IServiceProvider -> IFunctionalJournalStorage<'Key, 'State, 'Event>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(resolve, null) then
            fail
                DefinitionStage
                $"'customStorage' of grain type '{draft.Contract.GrainTypeName}' requires a service resolver."

        if draft.CustomStorage.IsSome then
            fail
                DefinitionStage
                $"'customStorage' is declared more than once for grain type '{draft.Contract.GrainTypeName}'. A repeated singleton operation is a definition error."

        JournaledDefinitionDraft.withState
            { draft with
                CustomStorage = Some resolve }

    /// <summary>
    /// Override the silo-wide snapshot rule for this custom-storage journal. Without this
    /// operation the definition inherits <c>FunctionalJournalSnapshotOptions.Policy</c>.
    /// </summary>
    [<CustomOperation("snapshotPolicy")>]
    member _.SnapshotPolicy<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            policy: FunctionalJournalSnapshotPolicy<'State>
        ) =
        let draft = state.State

        if draft.SnapshotPolicy.IsSome then
            fail
                DefinitionStage
                $"'snapshotPolicy' is declared more than once for grain type '{draft.Contract.GrainTypeName}'. A repeated singleton operation is a definition error."

        JournaledDefinitionDraft.withState
            { draft with
                SnapshotPolicy = Some policy }

    /// <summary>
    /// Declare a durable reminder. A successful tick appends and confirms the returned events as
    /// one atomic batch before Orleans observes completion.
    /// </summary>
    [<CustomOperation("onReminder")>]
    member _.OnReminder<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            name: string,
            dueTime: TimeSpan,
            period: TimeSpan,
            hook: JournaledReminderHook<'Actor, 'Key, 'State, 'Event>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail
                DefinitionStage
                $"'onReminder' '{name}' of grain type '{draft.Contract.GrainTypeName}' requires a hook."

        let declaration =
            { Name = name
              DueTime = dueTime
              Period = period
              Hook = hook }

        JournaledDefinitionDraft.withState
            { draft with
                Reminders = draft.Reminders @ [ declaration ] }

    /// <summary>
    /// Declare an activation-local timer. Its returned events are appended atomically and
    /// confirmed. Unlike a whole-state timer, <c>Interleave = true</c> is supported because
    /// Orleans' log-view adaptor serializes concurrent submissions.
    /// </summary>
    [<CustomOperation("onTimer")>]
    member _.OnTimer<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            name: string,
            options: GrainTimerCreationOptions,
            hook: JournaledTimerHook<'Actor, 'Key, 'State, 'Event>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail DefinitionStage $"'onTimer' '{name}' of grain type '{draft.Contract.GrainTypeName}' requires a hook."

        let declaration =
            { Name = name
              DueTime = options.DueTime
              Period = options.Period
              Interleave = options.Interleave
              KeepAlive = options.KeepAlive
              Hook = hook }

        JournaledDefinitionDraft.withState
            { draft with
                Timers = draft.Timers @ [ declaration ] }

    /// <summary>
    /// Subscribe implicitly to one Orleans stream namespace. A successful delivery appends and
    /// confirms the returned event batch before acknowledging the item.
    /// </summary>
    [<CustomOperation("onStream")>]
    member _.OnStream<'State, 'Event, 'Item>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            providerName: string,
            streamNamespace: string,
            hook: JournaledStreamHook<'Actor, 'Key, 'State, 'Event, 'Item>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail
                DefinitionStage
                $"'onStream' for provider '{providerName}' and namespace '{streamNamespace}' on grain type '{draft.Contract.GrainTypeName}' requires a hook."

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
                    let! events = hook context (unbox<'State> currentState) (unbox<'Item> item)
                    return box (events |> List.map box)
                })

        let declaration =
            { Attachment = StreamAttachment attach
              ProviderName = providerName
              Namespace = streamNamespace
              ItemType = typeof<'Item>
              Adapter = adapter }

        JournaledDefinitionDraft.withState
            { draft with
                StreamBindings = draft.StreamBindings @ [ declaration ] }

    /// <summary>
    /// Subscribe implicitly to one Orleans broadcast-channel namespace. A successful delivery
    /// appends and confirms the returned event batch before acknowledging the item.
    /// </summary>
    [<CustomOperation("onBroadcast")>]
    member _.OnBroadcast<'State, 'Event, 'Item>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            providerName: string,
            channelNamespace: string,
            hook: JournaledStreamHook<'Actor, 'Key, 'State, 'Event, 'Item>
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
                    Func<exn, Task>(fun error -> Task.FromException error)
                ))

        let adapter =
            FunctionalStreamHookAdapter(fun key core currentState item ->
                task {
                    let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                    let! events = hook context (unbox<'State> currentState) (unbox<'Item> item)
                    return box (events |> List.map box)
                })

        let declaration =
            { Attachment = ChannelAttachment attach
              ProviderName = providerName
              Namespace = channelNamespace
              ItemType = typeof<'Item>
              Adapter = adapter }

        JournaledDefinitionDraft.withState
            { draft with
                StreamBindings = draft.StreamBindings @ [ declaration ] }

    /// <summary>Set the Orleans idle collection age for this grain type.</summary>
    /// <param name="age">The idle duration after which Orleans may collect an inactive activation.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when 'collectionAge' is already declared for this draft.</exception>
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
    /// <param name="strategy">The stock Orleans placement strategy to use.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when 'placement' is already declared for this draft.</exception>
    [<CustomOperation("placement")>]
    member _.Placement<'State, 'Event>
        (state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>, strategy: Orleans.FSharp.PlacementStrategy) =
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
    /// Register an activation-scoped Orleans migration participant. The factory runs before
    /// rehydration, once for each source or destination activation.
    /// </summary>
    /// <param name="factory">Creates a fresh participant for the activation.</param>
    [<CustomOperation("migrationParticipant")>]
    member _.MigrationParticipant<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            factory: FunctionalMigrationParticipantFactory<'Actor, 'Key>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(factory, null) then
            fail
                DefinitionStage
                $"'migrationParticipant' of grain type '{draft.Contract.GrainTypeName}' requires a factory."

        JournaledDefinitionDraft.withState
            { draft with
                MigrationParticipants = draft.MigrationParticipants @ [ factory ] }

    /// <summary>
    /// Run a hook once the journal has been replayed and before the activation serves its first
    /// call. It returns no replacement state: the state is the fold of the journal.
    /// </summary>
    /// <param name="hook">The activation hook.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="hook"/> is null, or 'onActivate' is already declared for this
    /// draft.
    /// </exception>
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
    /// <param name="hook">The deactivation hook.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="hook"/> is null, or 'onDeactivate' is already declared for this
    /// draft.
    /// </exception>
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

    /// <summary>
    /// Run synchronously whenever Orleans reports that the tentative state may have changed.
    /// The supplied state includes confirmed and unconfirmed events.
    /// </summary>
    [<CustomOperation("onTentativeStateChanged")>]
    member _.OnTentativeStateChanged<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            hook: JournaledStateChangedHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail
                DefinitionStage
                $"'onTentativeStateChanged' of grain type '{draft.Contract.GrainTypeName}' requires a hook."

        JournaledDefinitionDraft.withState
            { draft with
                OnTentativeStateChanged =
                    DefinitionDraft.single
                        "onTentativeStateChanged"
                        draft.Contract.GrainTypeName
                        draft.OnTentativeStateChanged
                        hook }

    /// <summary>
    /// Run synchronously whenever Orleans reports that the confirmed state may have changed.
    /// </summary>
    [<CustomOperation("onStateChanged")>]
    member _.OnStateChanged<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            hook: JournaledStateChangedHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail DefinitionStage $"'onStateChanged' of grain type '{draft.Contract.GrainTypeName}' requires a hook."

        JournaledDefinitionDraft.withState
            { draft with
                OnStateChanged =
                    DefinitionDraft.single "onStateChanged" draft.Contract.GrainTypeName draft.OnStateChanged hook }

    /// <summary>
    /// Run synchronously when Orleans' log-consistency protocol reports a connection issue. The
    /// exact Orleans <c>ConnectionIssue</c> is supplied, so the hook can customize its retry delay.
    /// </summary>
    [<CustomOperation("onConnectionIssue")>]
    member _.OnConnectionIssue<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            hook: JournaledConnectionIssueHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail DefinitionStage $"'onConnectionIssue' of grain type '{draft.Contract.GrainTypeName}' requires a hook."

        JournaledDefinitionDraft.withState
            { draft with
                OnConnectionIssue =
                    DefinitionDraft.single "onConnectionIssue" draft.Contract.GrainTypeName draft.OnConnectionIssue hook }

    /// <summary>Run synchronously when a previously reported connection issue is resolved.</summary>
    [<CustomOperation("onConnectionIssueResolved")>]
    member _.OnConnectionIssueResolved<'State, 'Event>
        (
            state: FunctionalJournaledDraft<'Actor, 'Key, 'Api, 'State, 'Event>,
            hook: JournaledConnectionIssueHook<'Actor, 'Key, 'State>
        ) =
        let draft = state.State

        if obj.ReferenceEquals(hook, null) then
            fail
                DefinitionStage
                $"'onConnectionIssueResolved' of grain type '{draft.Contract.GrainTypeName}' requires a hook."

        JournaledDefinitionDraft.withState
            { draft with
                OnConnectionIssueResolved =
                    DefinitionDraft.single
                        "onConnectionIssueResolved"
                        draft.Contract.GrainTypeName
                        draft.OnConnectionIssueResolved
                        hook }
