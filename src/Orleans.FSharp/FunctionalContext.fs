namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.EventSourcing
open Orleans.Runtime
open Orleans.Streams
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>A materialized journal view and the number of events represented by it.</summary>
[<NoEquality; NoComparison>]
type FunctionalJournalSnapshot<'State> =
    {
        /// The number of events already folded into <see cref="P:Orleans.FSharp.FunctionalJournalSnapshot`1.State"/>.
        Version: int
        /// The materialized journal state at <see cref="P:Orleans.FSharp.FunctionalJournalSnapshot`1.Version"/>.
        State: 'State
    }

/// <summary>The durable identity handed to a functional journal's custom storage.</summary>
[<Sealed>]
type FunctionalJournalStorageIdentity<'Key>
    internal (grainTypeName: string, grainId: GrainId, key: 'Key) =

    /// <summary>The explicit Orleans grain type name.</summary>
    member _.GrainTypeName = grainTypeName

    /// <summary>The complete Orleans grain identity, including its grain type.</summary>
    member _.GrainId = grainId

    /// <summary>The definition's decoded domain key.</summary>
    member _.Key = key

/// <summary>
/// What custom journal storage returns on activation: an optional compacted view followed by the
/// still-retained events after that view.
/// </summary>
[<NoEquality; NoComparison>]
type FunctionalJournalRead<'State, 'Event> =
    {
        /// The latest stored snapshot, or <c>None</c> when replay starts at the declared initial state.
        Snapshot: FunctionalJournalSnapshot<'State> option
        /// Events strictly after the snapshot, in journal order.
        Events: IReadOnlyList<'Event>
    }

/// <summary>One compare-and-swap append issued to custom functional journal storage.</summary>
[<NoEquality; NoComparison>]
type FunctionalJournalWrite<'State, 'Event> =
    {
        /// The total journal version which must still be current for this write to succeed.
        ExpectedVersion: int
        /// The atomic event batch to append. A manual snapshot may write an empty batch.
        Events: IReadOnlyList<'Event>
        /// <summary>
        /// The resulting compacted view when the resolved snapshot policy fires; otherwise
        /// <c>None</c>. Its version is <c>ExpectedVersion + Events.Count</c>.
        /// </summary>
        Snapshot: FunctionalJournalSnapshot<'State> option
    }

/// <summary>
/// Typed application storage behind Orleans' CustomStorage log-consistency provider.
/// </summary>
/// <remarks>
/// <para>
/// <c>Append</c> is a compare-and-swap operation: it must return <c>false</c> without changing
/// storage when the durable version differs from <c>ExpectedVersion</c>. On success it appends the
/// whole event batch atomically and advances the version by its count. When <c>Snapshot</c> is
/// present, the same atomic write may discard every retained event represented by that snapshot.
/// </para>
/// <para>
/// The runtime, not the storage implementation, folds the tail returned by <c>Read</c>. There is
/// therefore one authoritative <c>apply</c> function: the one declared by
/// <c>journaledGrainFor</c>.
/// </para>
/// </remarks>
type IFunctionalJournalStorage<'Key, 'State, 'Event> =

    /// <summary>Read the latest snapshot and every retained event after it.</summary>
    abstract Read:
        identity: FunctionalJournalStorageIdentity<'Key> -> Task<FunctionalJournalRead<'State, 'Event>>

    /// <summary>Atomically append one batch, optionally replacing the compacted snapshot.</summary>
    abstract Append:
        identity: FunctionalJournalStorageIdentity<'Key> * write: FunctionalJournalWrite<'State, 'Event> -> Task<bool>

    /// <summary>Delete the complete stored journal for one grain identity.</summary>
    abstract Clear: identity: FunctionalJournalStorageIdentity<'Key> -> Task

/// <summary>A per-definition snapshot rule. Absence and <c>Inherit</c> use the silo default.</summary>
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type FunctionalJournalSnapshotPolicy<'State> =
    /// <summary>Use the silo-wide default rule.</summary>
    | Inherit
    /// <summary>Disable automatic snapshots for this definition.</summary>
    | Disabled
    /// <summary>Snapshot whenever an append crosses the next positive event-count boundary.</summary>
    | Every of eventCount: int
    /// <summary>Pure decision from the resulting version and state; CAS retries may evaluate it more than once.</summary>
    | When of predicate: (int -> 'State -> bool)

/// <summary>Information available to a silo-wide conditional snapshot rule.</summary>
[<Sealed>]
type FunctionalJournalSnapshotContext
    internal (grainTypeName: string, grainId: GrainId, key: obj, version: int, stateType: Type, state: obj) =

    /// <summary>The explicit Orleans grain type name.</summary>
    member _.GrainTypeName = grainTypeName

    /// <summary>The complete Orleans grain identity.</summary>
    member _.GrainId = grainId

    /// <summary>The boxed domain key.</summary>
    member _.Key = key

    /// <summary>The resulting journal version.</summary>
    member _.Version = version

    /// <summary>The definition's exact state type.</summary>
    member _.StateType = stateType

    /// <summary>The resulting state, boxed because one silo default serves heterogeneous definitions.</summary>
    member _.State = state

/// <summary>The silo-wide snapshot rule inherited by custom-storage definitions.</summary>
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type FunctionalJournalSnapshotDefault =
    /// <summary>Do not create automatic snapshots.</summary>
    | Disabled
    /// <summary>Snapshot whenever an append crosses the next positive event-count boundary.</summary>
    | Every of eventCount: int
    /// <summary>Pure decision from identity and resulting state; CAS retries may evaluate it more than once.</summary>
    | When of predicate: (FunctionalJournalSnapshotContext -> bool)

/// <summary>Options for the snapshot rule inherited by functional custom-storage journals.</summary>
[<Sealed>]
type FunctionalJournalSnapshotOptions() =

    /// <summary>The silo-wide default. Per-definition <c>snapshotPolicy</c> overrides it.</summary>
    member val Policy = FunctionalJournalSnapshotDefault.Disabled with get, set

/// <summary>
/// The activation-side journal of a <c>journaledGrainFor</c> definition, as an invocation context
/// and the dispatch path see it. Implemented by the runtime over an Orleans log-view adaptor.
/// </summary>
/// <remarks>
/// Every member is boxed rather than generic because a hosted definition is non-generic by the
/// time an activation exists — exactly the reason the primary-state seam is boxed too. The exact
/// state and event types are recovered by the definition's preclosed encoders.
/// </remarks>
[<AllowNullLiteral>]
type internal IFunctionalJournalAccess =

    /// <summary>The confirmed view, decoded into the definition's state type and boxed.</summary>
    abstract Current: obj

    /// <summary>The tentative view, decoded into the definition's state type and boxed.</summary>
    abstract Tentative: obj

    /// <summary>
    /// The length of the confirmed prefix of the journal: how many events have been appended and
    /// confirmed for this grain, ever.
    /// </summary>
    abstract ConfirmedVersion: int

    /// <summary>The currently submitted but not yet confirmed events, decoded and boxed.</summary>
    abstract Unconfirmed: obj list

    /// <summary>The definition's declared state type, so a typed call can be checked against it.</summary>
    abstract StateType: Type

    /// <summary>The definition's declared event type, so a typed call can be checked against it.</summary>
    abstract EventType: Type

    /// <summary>Submit boxed events without waiting for confirmation.</summary>
    abstract Raise: obj list -> unit

    /// <summary>
    /// Append the boxed events and wait until they are confirmed. This is the per-turn
    /// confirmation the dispatch path performs for a handler's returned events.
    /// </summary>
    abstract RaiseAndConfirm: events: obj list * forceSnapshot: bool -> Task

    /// <summary>Request a snapshot after this callback's events have been confirmed.</summary>
    abstract RequestSnapshot: unit -> unit

    /// <summary>
    /// Append the boxed events at the current confirmed position only, reporting whether they were
    /// accepted. <c>false</c> means another writer appended first.
    /// </summary>
    abstract RaiseConditional: obj list -> Task<bool>

    /// <summary>Wait until all previously submitted events have been confirmed.</summary>
    abstract Confirm: unit -> Task

    /// <summary>Refresh the confirmed view and confirm all previously submitted events.</summary>
    abstract Refresh: unit -> Task

    /// <summary>Retrieve and decode a segment of the confirmed event sequence.</summary>
    abstract Retrieve: fromVersion: int * toVersion: int -> Task<obj list>

    /// <summary>Clear every confirmed and unconfirmed event and restore the initial state.</summary>
    abstract Clear: CancellationToken -> Task

    /// <summary>Enable Orleans log-consistency statistics collection.</summary>
    abstract EnableStats: unit -> unit

    /// <summary>Disable Orleans log-consistency statistics collection.</summary>
    abstract DisableStats: unit -> unit

    /// <summary>Get the currently collected Orleans log-consistency statistics.</summary>
    abstract GetStats: unit -> LogConsistencyStatistics

/// <summary>
/// One callback's view of the activation's journal: the activation-wide journal, bound to the
/// scope of the callback that resolved it.
/// </summary>
/// <remarks>
/// The activation-wide journal itself is unscoped, because the dispatch path uses it directly to
/// confirm a handler's returned events after that handler's scope has already expired. What a
/// callback receives is this wrapper, so a captured context cannot append after its turn and a
/// state-neutral callback cannot append at all.
/// </remarks>
[<Sealed>]
type internal FunctionalScopedJournal internal (journal: IFunctionalJournalAccess, scope: FunctionalStateScope) =

    interface IFunctionalJournalAccess with
        member _.Current =
            scope.EnsureJournalUsable "journalState"
            journal.Current

        member _.Tentative =
            scope.EnsureJournalUsable "journalTentativeState"
            journal.Tentative

        member _.ConfirmedVersion =
            scope.EnsureJournalUsable "journalVersion"
            journal.ConfirmedVersion

        member _.Unconfirmed =
            scope.EnsureJournalUsable "unconfirmedEvents"
            journal.Unconfirmed

        member _.StateType = journal.StateType

        member _.EventType = journal.EventType

        member _.Raise events =
            scope.EnsureJournalAppend "raiseEvents"
            journal.Raise events

        member _.RaiseAndConfirm(events, forceSnapshot) =
            scope.EnsureJournalAppend "raise"
            journal.RaiseAndConfirm(events, forceSnapshot)

        member _.RequestSnapshot() = scope.RequestJournalSnapshot()

        member _.RaiseConditional events =
            scope.EnsureJournalAppend "raiseConditional/raiseConditionalEvent"
            journal.RaiseConditional events

        member _.Confirm() =
            scope.EnsureJournalUsable "confirmEvents"
            journal.Confirm()

        member _.Refresh() =
            scope.EnsureJournalUsable "refreshJournal"
            journal.Refresh()

        member _.Retrieve(fromVersion, toVersion) =
            scope.EnsureJournalUsable "retrieveConfirmedEvents"
            journal.Retrieve(fromVersion, toVersion)

        member _.Clear cancellationToken =
            scope.EnsureJournalAppend "clearJournal"
            journal.Clear cancellationToken

        member _.EnableStats() =
            scope.EnsureJournalUsable "enableJournalStats"
            journal.EnableStats()

        member _.DisableStats() =
            scope.EnsureJournalUsable "disableJournalStats"
            journal.DisableStats()

        member _.GetStats() =
            scope.EnsureJournalUsable "getJournalStats"
            journal.GetStats()

/// <summary>
/// Activation-supplied services behind one invocation context. Phase 4 fills this record for
/// every request, hook, timer, and reminder callback.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalContextCore =
    {
        /// The Orleans identity of the activation.
        GrainId: GrainId
        /// The activation's grain factory.
        GrainFactory: IGrainFactory
        /// The activation's service provider.
        Services: IServiceProvider
        /// A scoped logger for the activation.
        Logger: ILogger
        /// The registered time provider.
        TimeProvider: TimeProvider
        /// The single <c>utcNow</c> value for this context, read once from
        /// <see cref="TimeProvider"/> at context creation. Every access through the public
        /// <c>utcNow</c> member returns this same frozen value, so two reads inside one callback
        /// can never observe different instants.
        UtcNow: DateTimeOffset
        /// The token selected by callback kind.
        CancellationToken: CancellationToken
        /// <summary>
        /// The Orleans stream cursor of the item being delivered, or <c>null</c> for every
        /// callback which is not an <c>onStream</c> delivery (and for <c>onBroadcast</c>, whose
        /// transport carries no cursor at all). Surfaced through
        /// <c>context.streamSequenceToken</c>.
        /// </summary>
        StreamSequenceToken: StreamSequenceToken
        /// Wrapper for the protected Orleans deactivate-on-idle method.
        DeactivateOnIdle: unit -> unit
        /// Wrapper for the protected Orleans delay-deactivation method.
        DelayDeactivation: TimeSpan -> unit
        /// Typed lookup of an attached persistent state facet, boxed as <c>IPersistentState&lt;_&gt;</c>.
        ResolvePersistentState: PersistentStateDescriptor -> obj
        /// Typed lookup of an attached transactional state facet, boxed as
        /// <c>FunctionalTransactionalState&lt;_&gt;</c>.
        ResolveTransactionalState: TransactionalStateDescriptor -> obj
        /// <summary>
        /// The activation's journal, or <c>null</c> for every definition that is not a
        /// <c>journaledGrainFor</c> one.
        /// </summary>
        Journal: IFunctionalJournalAccess
    }

/// <summary>
/// The immutable per-invocation context supplied to every functional handler, lifecycle hook,
/// timer, and reminder callback.
/// </summary>
[<Sealed>]
type FunctionalGrainContext<'Actor, 'Key> internal (key: 'Key, core: FunctionalContextCore) =

    let journalFor (operationName: string) =
        match core.Journal with
        | null ->
            fail
                DefinitionStage
                $"'{operationName}' is available only inside a definition built with 'journaledGrainFor'. An ordinary 'grainFor' definition has no journal."
        | journal -> journal

    let requireJournalState (operationName: string) (stateType: Type) =
        let journal = journalFor operationName

        if journal.StateType <> stateType then
            fail
                DefinitionStage
                $"'{operationName}' was called with state type '{stateType.FullName}', but this definition's declared state type is '{journal.StateType.FullName}'."

        journal

    let requireJournalEvent (operationName: string) (eventType: Type) =
        let journal = journalFor operationName

        if journal.EventType <> eventType then
            fail
                DefinitionStage
                $"'{operationName}' was called with event type '{eventType.FullName}', but this definition's declared event type is '{journal.EventType.FullName}'."

        journal

    /// <summary>The domain key decoded once from the supplied grain identity.</summary>
    member _.key = key

    /// <summary>The Orleans identity of this activation.</summary>
    member _.grainId = core.GrainId

    /// <summary>The grain factory used to bind further references.</summary>
    member _.grainFactory = core.GrainFactory

    /// <summary>The activation service provider.</summary>
    member _.services = core.Services

    /// <summary>A logger scoped to this activation.</summary>
    member _.logger = core.Logger

    /// <summary>The registered time provider.</summary>
    member _.timeProvider = core.TimeProvider

    /// <summary>
    /// The instant this context was created, read once from
    /// <see cref="P:Orleans.FSharp.FunctionalGrainContext`2.timeProvider"/>. Stable for the whole
    /// callback: two reads of <c>utcNow</c> in the same handler, hook, timer, or reminder
    /// callback always agree.
    /// </summary>
    member _.utcNow = core.UtcNow

    /// <summary>The cancellation token selected by this callback kind.</summary>
    member _.cancellationToken = core.CancellationToken

    /// <summary>
    /// The Orleans cursor of the item currently being delivered to an <c>onStream</c> hook, and
    /// <c>None</c> in every other callback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is <c>Some</c> only inside an <c>onStream</c> hook, and only for a stream provider whose
    /// streams are rewindable. Orleans' in-memory streams <b>are</b> rewindable, so a delivery
    /// through them surfaces a real sequence number here (<c>examples/feature-tour</c> §11 prints
    /// one); a provider whose streams are not rewindable hands the consumer a <c>null</c> cursor,
    /// which surfaces as <c>None</c>. An <c>onBroadcast</c> hook always observes <c>None</c>:
    /// broadcast channels have no cursor concept at all
    /// (<c>IBroadcastChannelSubscription.Attach</c> delivers the item alone).
    /// </para>
    /// <para>
    /// <b>The runtime never rewinds with it.</b> A functional activation resumes its implicit
    /// subscription with no token, so delivery starts at the subscription's current position. The
    /// token is exposed so an application can checkpoint or de-duplicate against it — Orleans
    /// redelivers an item whose hook threw (see the <c>onStream</c> operation's remarks) — not so
    /// the runtime can replay from it.
    /// </para>
    /// </remarks>
    member _.streamSequenceToken: StreamSequenceToken option =
        match core.StreamSequenceToken with
        | null -> None
        | token -> Some token

    /// <summary>Request deactivation once the current turn completes.</summary>
    member _.deactivateOnIdle() = core.DeactivateOnIdle()

    /// <summary>Extend the activation's idle lifetime.</summary>
    /// <param name="timeSpan">The duration to extend the idle deadline by.</param>
    member _.delayDeactivation(timeSpan: TimeSpan) = core.DelayDeactivation timeSpan

    /// <summary>Look up an attached persistent state facet by its logical descriptor.</summary>
    /// <param name="state">The persistent-state reference to look up.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="state"/> is null, or when no persistent state matching its
    /// descriptor (name, provider, and stored type) is attached to this definition.
    /// </exception>
    member _.persistentState<'State>(state: PersistentStateRef<'State>) : IPersistentState<'State> =
        if obj.ReferenceEquals(state, null) then
            fail DefinitionStage "persistentState requires a PersistentStateRef value."

        match core.ResolvePersistentState state.Descriptor with
        | :? IPersistentState<'State> as facet -> facet
        | _ ->
            let descriptor = state.Descriptor

            fail
                DefinitionStage
                $"no persistent state named '{descriptor.StateName}' with provider '{descriptor.ProviderName}' and stored type '{descriptor.StoredType.FullName}' is attached to this definition."

    /// <summary>Look up an attached transactional state facet by its logical descriptor.</summary>
    /// <remarks>
    /// The returned facade is bound to this invocation and to this callback's transaction access:
    /// it rejects every member once the callback has completed, rejects reads and updates in a
    /// callback that can never carry a transaction context, and rejects updates in a
    /// <c>readOnly</c> transactional operation.
    /// </remarks>
    /// <param name="state">The transactional-state reference to look up.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="state"/> is null, or when no transactional state matching its
    /// descriptor (name, storage, and stored type) is attached to this definition.
    /// </exception>
    member _.transactionalState<'State>(state: TransactionalStateRef<'State>) : FunctionalTransactionalState<'State> =
        if obj.ReferenceEquals(state, null) then
            fail TransactionalStage "transactionalState requires a TransactionalStateRef value."

        match core.ResolveTransactionalState state.Descriptor with
        | :? FunctionalTransactionalState<'State> as facet -> facet
        | _ ->
            let descriptor = state.Descriptor

            fail
                TransactionalStage
                $"no transactional state named '{descriptor.StateName}' with storage '{descriptor.StorageName}' and stored type '{descriptor.StoredType.FullName}' is attached to this definition."

    /// <summary>
    /// The version of this activation's journal: how many events have been appended and confirmed
    /// for this grain, ever. It is Orleans' <c>ConfirmedVersion</c>, the same number
    /// <c>JournaledGrain.Version</c> reports.
    /// </summary>
    /// <remarks>
    /// It is the version of the state the handler was handed, because the runtime confirms a
    /// turn's events after the handler returns: a handler that raises three events still observes
    /// the version it started from. Available only on a <c>journaledGrainFor</c> definition.
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when this definition was not built with 'journaledGrainFor'.
    /// </exception>
    member _.journalVersion: int = (journalFor "journalVersion").ConfirmedVersion

    /// <summary>Read the current confirmed journal state.</summary>
    /// <remarks>
    /// Unlike the state argument handed to a handler, this value is read when the member is
    /// called. After <c>refreshJournal</c> it therefore observes the refreshed confirmed view,
    /// matching <c>JournaledGrain.State</c>.
    /// </remarks>
    member _.journalState<'State>() : 'State =
        let journal = requireJournalState "journalState" typeof<'State>
        unbox<'State> journal.Current

    /// <summary>
    /// Read the tentative journal state, including both confirmed and locally submitted events.
    /// </summary>
    member _.journalTentativeState<'State>() : 'State =
        let journal = requireJournalState "journalTentativeState" typeof<'State>
        unbox<'State> journal.Tentative

    /// <summary>Read the locally submitted events which are not confirmed yet.</summary>
    member _.unconfirmedEvents<'Event>() : 'Event list =
        let journal = requireJournalEvent "unconfirmedEvents" typeof<'Event>
        journal.Unconfirmed |> List.map unbox<'Event>

    /// <summary>
    /// Submit one event without waiting for confirmation. The tentative state changes
    /// synchronously; call <c>confirmEvents</c> before relying on durability.
    /// </summary>
    member _.raiseEvent<'Event>(event: 'Event) : unit =
        let journal = requireJournalEvent "raiseEvent" typeof<'Event>
        journal.Raise [ box event ]

    /// <summary>
    /// Submit an atomic event batch without waiting for confirmation. The tentative state changes
    /// synchronously; call <c>confirmEvents</c> before relying on durability.
    /// </summary>
    member _.raiseEvents<'Event>(events: 'Event list) : unit =
        let journal = requireJournalEvent "raiseEvents" typeof<'Event>
        journal.Raise(events |> List.map box)

    /// <summary>Wait until every previously submitted event has been confirmed.</summary>
    member _.confirmEvents() : Task = (journalFor "confirmEvents").Confirm()

    /// <summary>
    /// Force one custom-storage snapshot after this callback's submitted and returned events have
    /// been confirmed. A manual request overrides both a per-definition and a silo-wide
    /// <c>Disabled</c> rule.
    /// </summary>
    /// <remarks>
    /// Available only when the definition declares <c>customStorage</c>. The request is bound to
    /// this callback: if the callback fails, no snapshot is written; in a read-only or otherwise
    /// state-neutral callback it is rejected like an event append. It is also rejected by the
    /// synchronous state/connection notification hooks, which have no asynchronous completion at
    /// which a storage write could be awaited.
    /// </remarks>
    member _.snapshotNow() : unit = (journalFor "snapshotNow").RequestSnapshot()

    /// <summary>
    /// Refresh the confirmed state from the global journal and confirm every submitted event.
    /// </summary>
    member _.refreshJournal() : Task = (journalFor "refreshJournal").Refresh()

    /// <summary>Retrieve a half-open segment of the confirmed event sequence.</summary>
    member _.retrieveConfirmedEvents<'Event>(fromVersion: int, toVersion: int) : Task<'Event list> =
        let journal = requireJournalEvent "retrieveConfirmedEvents" typeof<'Event>

        task {
            let! events = journal.Retrieve(fromVersion, toVersion)
            return events |> List.map unbox<'Event>
        }

    /// <summary>
    /// Clear confirmed and unconfirmed events and restore the declared initial state. The current
    /// callback's cancellation token is forwarded to Orleans.
    /// </summary>
    member _.clearJournal() : Task =
        (journalFor "clearJournal").Clear core.CancellationToken

    /// <summary>Enable Orleans log-consistency statistics collection for this activation.</summary>
    member _.enableJournalStats() : unit =
        (journalFor "enableJournalStats").EnableStats()

    /// <summary>Disable Orleans log-consistency statistics collection for this activation.</summary>
    member _.disableJournalStats() : unit =
        (journalFor "disableJournalStats").DisableStats()

    /// <summary>Get the currently collected Orleans log-consistency statistics.</summary>
    member _.getJournalStats() : LogConsistencyStatistics =
        (journalFor "getJournalStats").GetStats()

    /// <summary>
    /// Append events at the journal's current confirmed position <b>only</b>, and report whether
    /// they were accepted. <c>false</c> means the position moved first and nothing was appended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is Orleans' <c>TryAppendRange</c> — <c>JournaledGrain.RaiseConditionalEvents</c> —
    /// and it is the only way a handler can make an append conditional on the state it read. The
    /// events returned by the handler are appended unconditionally instead, which is what makes
    /// the ordinary path a single round trip.
    /// </para>
    /// <para>
    /// <b>It can only ever answer <c>false</c> when something else can write this grain's journal
    /// between the read and the append.</b> With a non-reentrant definition on a single cluster
    /// nothing can: the activation is the sole writer and Orleans does not interleave its turns,
    /// so a conditional append always succeeds. It becomes meaningful for a <c>reentrant</c> or
    /// <c>mayInterleave</c> contract, where a second turn of the same activation can append
    /// between this handler's read and its append.
    /// </para>
    /// </remarks>
    /// <param name="events">The events to append conditionally.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when this definition was not built with 'journaledGrainFor', or when
    /// <typeparamref name="'Event"/> does not match the definition's declared event type.
    /// </exception>
    member _.raiseConditional<'Event>(events: 'Event list) : Task<bool> =
        let journal = requireJournalEvent "raiseConditional" typeof<'Event>
        journal.RaiseConditional(events |> List.map box)

    /// <summary>Conditionally append one event at the current confirmed position.</summary>
    member _.raiseConditionalEvent<'Event>(event: 'Event) : Task<bool> =
        let journal = requireJournalEvent "raiseConditionalEvent" typeof<'Event>
        journal.RaiseConditional [ box event ]

    /// <summary>Read a typed value from the Orleans request context.</summary>
    /// <param name="name">The request-context key to read.</param>
    /// <returns>
    /// <c>Some</c> the value when present and assignable to <typeparamref name="'Value"/>;
    /// otherwise <c>None</c>.
    /// </returns>
    member _.tryGetRequestContext<'Value>(name: string) : 'Value option =
        match RequestContext.Get name with
        | null -> None
        | :? 'Value as value -> Some value
        | _ -> None

    /// <summary>Write a value into the Orleans request context.</summary>
    /// <param name="name">The request-context key to write.</param>
    /// <param name="value">The value to store.</param>
    member _.setRequestContext<'Value> (name: string) (value: 'Value) : unit = RequestContext.Set(name, box value)

    /// <summary>Remove a value from the Orleans request context.</summary>
    /// <param name="name">The request-context key to remove.</param>
    member _.removeRequestContext(name: string) : unit = RequestContext.Remove name |> ignore

/// <summary>
/// A handler for one API operation. It receives the invocation context, the current primary
/// state, and the exact argument, and returns the replacement state with the exact reply.
/// </summary>
type Handler<'Actor, 'Key, 'State, 'Argument, 'Reply> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> 'Argument -> Task<'State * 'Reply>

/// <summary>
/// A handler for one <b>query</b> API operation: it receives the invocation context, the current
/// state, and the exact argument, and returns the exact reply only. There is no replacement state
/// and no event list.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape <c>handleQuery</c> binds, on the ordinary definition builder and on the
/// journaled one alike -- hence one alias for both. A query changes nothing, so neither builder has
/// anything of its own to add to the return type: the ordinary form's <c>'State</c> and the
/// journaled form's <c>'Event list</c> are both fixed by the operation being a query, and only the
/// reply is left for the handler to produce.
/// </para>
/// <para>
/// <c>handleQuery</c> requires the operation to be declared <c>readOnly</c> in the contract,
/// because that declaration is what makes the discarded state honest: the runtime already throws a
/// read-only invocation's replacement state away and rejects its persistent-state setter
/// (<c>FunctionalDispatch.dispatch</c>). Binding this shape to an operation that is <i>not</i>
/// read-only would silently freeze that operation's state instead, which is why the definition
/// builder refuses it. Use <see cref="T:Orleans.FSharp.Handler`5"/> (or
/// <see cref="T:Orleans.FSharp.JournaledHandler`6"/>) and say what changes, instead.
/// </para>
/// </remarks>
type QueryHandler<'Actor, 'Key, 'State, 'Argument, 'Reply> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> 'Argument -> Task<'Reply>

/// <summary>
/// A handler for one <b>server-streaming</b> API operation. Spec 004 item 6. It receives the
/// invocation context, the current primary state, and the exact argument, and returns the sequence
/// of exact items.
/// </summary>
/// <remarks>
/// <para>
/// <b>The return type is the BCL interface.</b> No wrapper, no library type: the handler body is
/// free to be a <c>taskSeq { … }</c> (the <c>FSharp.Control.TaskSeq</c> package this library
/// already depends on, so nothing new is added to an application's closure), a C# async iterator,
/// an <c>IAsyncEnumerable</c> obtained from somewhere else, or a hand-written enumerator. What the
/// runtime needs is only that it can be enumerated once, with the enumeration's cancellation token.
/// </para>
/// <para>
/// <b>There is no replacement state.</b> Unlike <see cref="T:Orleans.FSharp.Handler`5"/> the
/// handler returns items only. A stream produces across many turns of the activation — Orleans
/// pulls it with a separate, always-interleaving <c>MoveNext</c> call per batch — so a whole-state
/// replacement published when the sequence ended would overwrite everything every other turn did
/// while it ran. The state the handler receives is therefore the snapshot taken when the
/// enumeration started, its persistent-state facades reject every mutation, and a journaled
/// definition's streaming handler raises no events. Publish from an ordinary operation and read
/// from the stream.
/// </para>
/// </remarks>
type StreamHandler<'Actor, 'Key, 'State, 'Argument, 'Item> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> 'Argument -> IAsyncEnumerable<'Item>

/// <summary>
/// A handler for one API operation of a journaled definition. It receives the invocation context,
/// the current confirmed state, and the exact argument, and returns the events to append together
/// with the exact reply.
/// </summary>
/// <remarks>
/// A journaled handler never returns a replacement state: the state is the fold of the journal, so
/// the only way to change it is to raise an event and let <c>apply</c> fold it. The runtime
/// appends the returned events and confirms them before the reply leaves the activation.
/// </remarks>
type JournaledHandler<'Actor, 'Key, 'State, 'Event, 'Argument, 'Reply> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> 'Argument -> Task<'Event list * 'Reply>

/// <summary>
/// An activation hook of a journaled definition. It runs after the journal has been replayed and
/// returns no replacement state — the journal is the state.
/// </summary>
type JournaledActivateHook<'Actor, 'Key, 'State> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> Task<unit>

/// <summary>A deactivation hook of a journaled definition.</summary>
type JournaledDeactivateHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> -> DeactivationReason -> 'State -> Task<unit>

/// <summary>A durable reminder hook of a journaled definition; its returned events are confirmed.</summary>
type JournaledReminderHook<'Actor, 'Key, 'State, 'Event> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> TickStatus -> Task<'Event list>

/// <summary>An activation-local timer hook of a journaled definition; its returned events are confirmed.</summary>
type JournaledTimerHook<'Actor, 'Key, 'State, 'Event> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> Task<'Event list>

/// <summary>
/// An implicit stream or broadcast delivery hook of a journaled definition; its returned events
/// are confirmed atomically before Orleans observes successful delivery.
/// </summary>
type JournaledStreamHook<'Actor, 'Key, 'State, 'Event, 'Item> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> 'Item -> Task<'Event list>

/// <summary>A synchronous notification that the confirmed or tentative journal state changed.</summary>
type JournaledStateChangedHook<'Actor, 'Key, 'State> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> unit

/// <summary>A synchronous notification from Orleans' log-consistency connection monitor.</summary>
type JournaledConnectionIssueHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> ConnectionIssue -> unit

/// <summary>An activation hook; its returned state is published in memory only.</summary>
type ActivateHook<'Actor, 'Key, 'State> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> Task<'State>

/// <summary>A deactivation hook; it performs cleanup and returns no replacement state.</summary>
type DeactivateHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> -> DeactivationReason -> 'State -> Task<unit>

/// <summary>A reminder hook; whole-state replacement under ordinary Orleans scheduling.</summary>
type ReminderHook<'Actor, 'Key, 'State> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> TickStatus -> Task<'State>

/// <summary>A timer hook; whole-state replacement under non-interleaving scheduling.</summary>
type TimerHook<'Actor, 'Key, 'State> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> Task<'State>

/// <summary>
/// An implicit stream-delivery or broadcast-delivery hook. It receives the invocation context,
/// the current primary state, and one delivered item, and returns the replacement state.
/// Whole-state replacement under the timer-hook rules: the replacement is published only when the
/// hook returns successfully, and the runtime issues no storage call of its own.
/// </summary>
/// <typeparam name="TItem">The exact item type carried on the stream or channel.</typeparam>
type StreamHook<'Actor, 'Key, 'State, 'Item> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> 'Item -> Task<'State>

/// <summary>
/// The closed set of documented Orleans grain-lifecycle stages an <c>onLifecycle</c> hook may
/// target -- not arbitrary ints.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>Orleans.Runtime.GrainLifecycleStage</c>'s own four documented constants exactly
/// (verified by reflection against Orleans 10.1.0 and 10.2.2, identical on both:
/// <c>First = System.Int32.MinValue</c>, <c>SetupState = 1000</c>, <c>Activate = 2000</c>,
/// <c>Last = System.Int32.MaxValue</c>). <c>Activate</c> is accepted by this type but rejected by
/// the <c>onLifecycle</c> custom operation at definition sealing -- see its remarks.
/// </para>
/// <para>
/// <b>All four numbered stages run before <c>OnActivateAsync</c>, including <c>Last</c>.</b>
/// Verified by an integration probe (not assumed): a raw witness subscribed directly at
/// <c>GrainLifecycleStage.Activate</c> observes the order
/// <c>First, SetupState, raw-Activate-stage, Last, OnActivateAsync</c>. Orleans runs the entire
/// numbered <c>ObservableLifecycle</c> "OnStart" sequence (First through Last, in ascending
/// order) to completion FIRST; <c>OnActivateAsync</c> -- and therefore the functional runtime's
/// own state initialization, the <c>onActivate</c> hook, reminder reconciliation, and timer
/// creation -- is a separate step that runs strictly after that whole sequence, not gated by any
/// single stage number. So there is no "post-state" stage among the four: not even <c>Last</c>.
/// </para>
/// </remarks>
type LifecycleStage =
    /// <summary>The first valid stage in a grain's lifecycle -- before persistent-state facets
    /// load, before <c>OnActivateAsync</c> and the ephemeral primary state it initializes.</summary>
    | First
    /// <summary>Orleans loads persistent-state facets here. Still strictly before
    /// <c>OnActivateAsync</c>, so the functional runtime's own primary state (ephemeral or
    /// facet-backed) is not yet initialized at this stage either.</summary>
    | SetupState
    /// <summary>Where application code could hook the numbered stage <c>OnActivateAsync</c> is
    /// most closely associated with -- but <c>OnActivateAsync</c> itself (state initialization,
    /// the <c>onActivate</c> hook, reminder reconciliation, timer creation, in that order) runs
    /// AFTER this stage and <c>Last</c> both complete, not during it. Rejected by
    /// <c>onLifecycle</c> regardless; use <c>onActivate</c> instead.</summary>
    | Activate
    /// <summary>The last of the four numbered stages -- still strictly BEFORE
    /// <c>OnActivateAsync</c> runs, not after. Like <c>First</c> and <c>SetupState</c>, a hook
    /// here has no meaningful primary state to read.</summary>
    | Last

/// <summary>Maps <see cref="T:Orleans.FSharp.LifecycleStage"/> to the Orleans
/// <c>GrainLifecycleStage</c> int constant it mirrors.</summary>
[<RequireQualifiedAccess>]
module LifecycleStage =

    /// <summary>The exact <c>Orleans.Runtime.GrainLifecycleStage</c> value of one stage.</summary>
    let toOrleansStage =
        function
        | First -> GrainLifecycleStage.First
        | SetupState -> GrainLifecycleStage.SetupState
        | Activate -> GrainLifecycleStage.Activate
        | Last -> GrainLifecycleStage.Last

/// <summary>
/// An <c>onLifecycle</c> hook. Deliberately state-free -- see the <c>onLifecycle</c> custom
/// operation's remarks for why every accepted stage (not only the pre-state ones) uses this same
/// shape rather than carrying <c>'State</c>.
/// </summary>
type LifecycleHook<'Actor, 'Key> = FunctionalGrainContext<'Actor, 'Key> -> Task<unit>
