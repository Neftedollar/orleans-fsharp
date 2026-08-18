namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open Orleans.Streams
open Orleans.FSharp.FunctionalDiagnostics

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

    /// <summary>
    /// The length of the confirmed prefix of the journal: how many events have been appended and
    /// confirmed for this grain, ever.
    /// </summary>
    abstract ConfirmedVersion: int

    /// <summary>The definition's declared event type, so a typed call can be checked against it.</summary>
    abstract EventType: Type

    /// <summary>
    /// Append the boxed events and wait until they are confirmed. This is the per-turn
    /// confirmation the dispatch path performs for a handler's returned events.
    /// </summary>
    abstract RaiseAndConfirm: obj list -> Task

    /// <summary>
    /// Append the boxed events at the current confirmed position only, reporting whether they were
    /// accepted. <c>false</c> means another writer appended first.
    /// </summary>
    abstract RaiseConditional: obj list -> Task<bool>

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
            scope.EnsureJournalUsable "state"
            journal.Current

        member _.ConfirmedVersion =
            scope.EnsureJournalUsable "journalVersion"
            journal.ConfirmedVersion

        member _.EventType = journal.EventType

        member _.RaiseAndConfirm events =
            scope.EnsureJournalAppend "raise"
            journal.RaiseAndConfirm events

        member _.RaiseConditional events =
            scope.EnsureJournalAppend "raiseConditional"
            journal.RaiseConditional events

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
    member _.delayDeactivation(timeSpan: TimeSpan) = core.DelayDeactivation timeSpan

    /// <summary>Look up an attached persistent state facet by its logical descriptor.</summary>
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
    member _.journalVersion: int =
        match core.Journal with
        | null ->
            fail
                DefinitionStage
                "'journalVersion' is available only inside a definition built with 'journaledGrainFor'. An ordinary 'grainFor' definition has no journal."
        | journal -> journal.ConfirmedVersion

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
    member _.raiseConditional<'Event>(events: 'Event list) : Task<bool> =
        match core.Journal with
        | null ->
            fail
                DefinitionStage
                "'raiseConditional' is available only inside a definition built with 'journaledGrainFor'. An ordinary 'grainFor' definition has no journal."
        | journal ->
            if journal.EventType <> typeof<'Event> then
                fail
                    DefinitionStage
                    $"'raiseConditional' was called with events of type '{typeof<'Event>.FullName}', but this definition's declared event type is '{journal.EventType.FullName}'."

            journal.RaiseConditional(events |> List.map box)

    /// <summary>Read a typed value from the Orleans request context.</summary>
    member _.tryGetRequestContext<'Value>(name: string) : 'Value option =
        match RequestContext.Get name with
        | null -> None
        | :? 'Value as value -> Some value
        | _ -> None

    /// <summary>Write a value into the Orleans request context.</summary>
    member _.setRequestContext<'Value> (name: string) (value: 'Value) : unit = RequestContext.Set(name, box value)

    /// <summary>Remove a value from the Orleans request context.</summary>
    member _.removeRequestContext(name: string) : unit = RequestContext.Remove name |> ignore

/// <summary>
/// A handler for one API operation. It receives the invocation context, the current primary
/// state, and the exact argument, and returns the replacement state with the exact reply.
/// </summary>
type Handler<'Actor, 'Key, 'State, 'Argument, 'Reply> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> 'Argument -> Task<'State * 'Reply>

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

/// <summary>An activation hook; its returned state is published in memory only.</summary>
type ActivateHook<'Actor, 'Key, 'State> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> Task<'State>

/// <summary>A deactivation hook; it performs cleanup and returns no replacement state.</summary>
type DeactivateHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> -> DeactivationReason -> 'State -> Task<unit>

/// <summary>A reminder hook; whole-state replacement under ordinary Orleans scheduling.</summary>
type ReminderHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> TickStatus -> Task<'State>

/// <summary>A timer hook; whole-state replacement under non-interleaving scheduling.</summary>
type TimerHook<'Actor, 'Key, 'State> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> Task<'State>

/// <summary>
/// An implicit stream-delivery or broadcast-delivery hook. It receives the invocation context,
/// the current primary state, and one delivered item, and returns the replacement state.
/// Whole-state replacement under the timer-hook rules: the replacement is published only when the
/// hook returns successfully, and the runtime issues no storage call of its own.
/// </summary>
/// <typeparam name="TItem">The exact item type carried on the stream or channel.</typeparam>
type StreamHook<'Actor, 'Key, 'State, 'Item> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> 'Item -> Task<'State>

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
