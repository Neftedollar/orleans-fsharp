namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Orleans
open Orleans.EventSourcing
open Orleans.EventSourcing.CustomStorage
open Orleans.Runtime
open Orleans.Storage
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// The activation-side journal of one <c>journaledGrainFor</c> definition: an Orleans log-view
/// adaptor obtained from a named log-consistency provider, plus the definition's preclosed fold
/// and codecs.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the adaptor is obtained.</b> Orleans installs one for a <c>JournaledGrain</c> from
/// <c>LogConsistentGrain.OnSetupState</c>, and this does exactly the same four things without
/// deriving from it: resolve the keyed <c>ILogViewAdaptorFactory</c>, resolve
/// <c>Factory&lt;IGrainContext, ILogConsistencyProtocolServices&gt;</c> (registered by every
/// <c>Add*BasedLogConsistencyProvider</c> call) and invoke it for this activation's grain context,
/// resolve the <c>IGrainStorage</c> the provider writes through, and call
/// <c>MakeLogViewAdaptor</c>. Every one of those is public Orleans surface; the only internal type
/// on the path is the protocol-services implementation, which is reached exclusively through the
/// registered factory delegate and never named.
/// </para>
/// <para>
/// <b>Why the fold failure is tracked.</b> Both Orleans adaptors invoke
/// <c>ILogViewAdaptorHost.UpdateView</c> inside a <c>try/catch</c> that logs the exception through
/// <c>ILogConsistencyProtocolServices.CaughtUserCodeException</c> and carries on with an
/// <b>unchanged</b> view. A functional definition's <c>apply</c> is the only thing that turns
/// events into state, so silently skipping one would hand the next handler a state that is not the
/// fold of its own journal. The host therefore records the first failure and the runtime fails the
/// turn — or the activation, when it happened during replay — instead.
/// </para>
/// </remarks>
[<Sealed>]
type internal FunctionalJournalHost
    (
        blueprint: FunctionalJournalBlueprint,
        grainTypeName: string,
        grainContext: IGrainContext,
        codec: IFunctionalPayloadCodec,
        persistenceCodec: FunctionalPersistenceCodec,
        logger: ILogger,
        key: obj
    ) =

    let mutable adaptor: ILogViewAdaptor<FunctionalJournalView, FunctionalJournalEntry> =
        Unchecked.defaultof<_>

    /// Builds a callback-scoped functional context. Bound by the activator after the target
    /// environment exists and before Orleans starts the activation lifecycle.
    let mutable contextFactory: (FunctionalStateScope -> FunctionalContextCore) option =
        None

    /// The first exception an <c>apply</c> fold threw, which Orleans would otherwise swallow.
    let mutable foldFailure: exn = null

    /// A deterministic custom-storage failure tunneled through Orleans' catch-all retry loop.
    /// The field stays set until this activation is deactivated: treating the adaptor's fallback
    /// view as application state, even for one more turn, would acknowledge data that was not
    /// durably read or written.
    let mutable terminalStorageFailure: exn = null

    /// The application storage instance is resolved once per activation, matching ordinary
    /// grain-scoped dependency resolution.
    let customStorage =
        blueprint.CustomStorage
        |> Option.map (fun storage -> struct (storage, storage.Resolve grainContext.ActivationServices))

    /// The silo default is read once for this activation. Per-definition rules are already in the
    /// blueprint and take precedence when a write is considered.
    let snapshotOptions =
        match grainContext.ActivationServices.GetService<IOptions<FunctionalJournalSnapshotOptions>>() with
        | null -> FunctionalJournalSnapshotOptions()
        | options -> options.Value

    let globalSnapshotPolicy = snapshotOptions.Policy
    let manualSnapshotMaxConflictRetries = snapshotOptions.ManualSnapshotMaxConflictRetries

    /// <summary>The declared initial state of this grain, boxed. Re-derived, never stored.</summary>
    member private _.InitialState = blueprint.Initial key

    /// <summary>Encode one state value with this activation's selected write codec.</summary>
    member private _.EncodeState(value: obj) =
        blueprint.EncodeState persistenceCodec codec value

    /// <summary>Decode one state value using the codec identifier stored beside it.</summary>
    member private _.DecodeState(codecId: string, payload: byte[]) =
        blueprint.DecodeState persistenceCodec codec codecId payload

    /// <summary>Decode one event using the codec identifier stored beside it.</summary>
    member private _.DecodeEvent(codecId: string, payload: byte[]) =
        blueprint.DecodeEvent persistenceCodec codec codecId payload

    /// <summary>Create a codec-tagged mutable view cell.</summary>
    member private this.View(value: obj) =
        FunctionalJournalView(
            Payload = this.EncodeState value,
            HasValue = true,
            CodecId = persistenceCodec.Id
        )

    /// <summary>Create one codec-tagged journal entry.</summary>
    member private _.Entry(event: obj, snapshotRequested: bool) =
        FunctionalJournalEntry(
            Payload = blueprint.EncodeEvent persistenceCodec codec event,
            SnapshotRequested = snapshotRequested,
            CodecId = persistenceCodec.Id
        )

    /// <summary>
    /// The state a view cell holds. A cell that was never written — a fresh <c>new()</c> instance
    /// Orleans materialized on a read that found no record — reports the declared initial state
    /// instead of a null payload.
    /// </summary>
    /// <param name="view">The log-view cell to read, or <c>null</c> for a never-materialized view.</param>
    member private this.ValueOf(view: FunctionalJournalView) : obj =
        if isNull (box view) then
            this.InitialState
        elif view.HasValue then
            if isNull view.Payload then
                fail
                    JournalStage
                    $"The functional journal view of grain type '{grainTypeName}' for grain '{grainContext.GrainId}' is marked as containing a value but has a null payload. The durable record is corrupt."

            this.DecodeState(view.CodecId, view.Payload)
        else
            this.InitialState

    /// <summary>
    /// Fold the events onto the current confirmed state before anything is submitted, so a fold
    /// that throws fails the turn with NOTHING appended.
    /// </summary>
    /// <remarks>
    /// It is not belt and braces, it is the only place the check can be made. Both Orleans
    /// adaptors fold an entry <b>after</b> the storage write that made it durable — LogStorage
    /// writes the log and then calls <c>UpdateView</c> for each new entry — so by the time a
    /// failing fold is observed inside the adaptor the event is already in the journal, and every
    /// later activation would replay it and fail again. Running the fold first turns a permanently
    /// poisoned journal into a failed call. It is sound precisely because <c>apply</c> is required
    /// to be pure: running it twice for the same event has no effect other than the cost.
    /// </remarks>
    /// <param name="events">The boxed events about to be submitted, folded in order over the current tentative state.</param>
    /// <exception cref="System.InvalidOperationException">The <c>apply</c> fold threw for one of <paramref name="events"/>.</exception>
    member private this.EnsureFoldable(events: obj list) =
        // A caller may already have used context.raiseEvent(s), so validate a later batch over
        // the same tentative prefix Orleans will actually fold it onto, not over the older
        // confirmed view.
        let mutable state = (this :> IFunctionalJournalAccess).Tentative

        for event in events do
            try
                state <- blueprint.Apply state event
            with cause ->
                failCause
                    JournalStage
                    $"the 'apply' fold of grain type '{grainTypeName}' failed for an event raised by grain '{grainContext.GrainId}'. Nothing was appended: the fold is run over the tentative state before the events are submitted, because Orleans' adaptors fold an entry only after the storage write that made it durable — an event whose fold throws would otherwise stay in the journal and fail every later replay."
                    cause

    /// <summary>Raise the fold failure this host recorded, if any, and forget it.</summary>
    /// <param name="stage">What the caller was doing, folded into the exception message (e.g. "replaying the journal").</param>
    /// <exception cref="System.InvalidOperationException">A previous <c>apply</c> fold failed and has not yet been rethrown.</exception>
    member private _.RethrowFoldFailure(stage: string) =
        match foldFailure with
        | null -> ()
        | cause ->
            foldFailure <- null

            failCause
                JournalStage
                $"the 'apply' fold of grain type '{grainTypeName}' failed while {stage} for grain '{grainContext.GrainId}'. Orleans' log-view adaptor catches and logs a failing fold and continues with an unchanged view, which would leave this activation holding a state that is not the fold of its own journal, so the failure is raised here instead."
                cause

    /// <summary>
    /// Record a non-retryable custom-storage failure. Orleans' CustomStorage adaptor catches every
    /// exception and retries forever, so its callback must subsequently return a harmless success
    /// value. The public journal operation observes this sticky failure, deactivates, and throws.
    /// </summary>
    member private _.RecordTerminalStorageFailure(cause: exn) =
        let previous = Interlocked.CompareExchange(&terminalStorageFailure, cause, null)

        if isNull previous then
            logger.LogError(
                cause,
                "The custom journal storage of grain type {GrainType} on {GrainId} reported a permanent failure; the activation will be deactivated.",
                grainTypeName,
                grainContext.GrainId
            )

    /// <summary>Fail the current operation and retire an activation whose storage is unusable.</summary>
    member private _.RethrowTerminalStorageFailure(stage: string) =
        match Volatile.Read(&terminalStorageFailure) with
        | null -> ()
        | cause ->
            let message =
                $"the custom journal storage of grain type '{grainTypeName}' failed permanently while {stage} for grain '{grainContext.GrainId}'. The activation is being deactivated; a later call will create a fresh activation and read durable state again. Cause: {cause.GetType().FullName}: {cause.Message}"

            grainContext.Deactivate(
                // Orleans may deep-copy a deactivation reason. Do not put the exception object in
                // it: Exception.TargetSite is a MethodBase, which the exact F# payload codec quite
                // correctly refuses to serialize.
                DeactivationReason(DeactivationReasonCode.ApplicationError, message),
                CancellationToken.None
            )

            // Keep the original exception in the silo log above, but do not put it in the inner-
            // exception graph sent to a functional caller for the same MethodBase reason.
            fail JournalStage message

    /// <summary>Raise either kind of failure hidden by an Orleans adaptor callback.</summary>
    member private this.RethrowJournalFailure(stage: string) =
        this.RethrowTerminalStorageFailure stage
        this.RethrowFoldFailure stage

    /// <summary>A harmless read result used only to make Orleans leave its permanent retry loop.</summary>
    member private this.TerminalReadFallback() =
        let view = this.View this.InitialState

        KeyValuePair<int, FunctionalJournalView>(0, view)

    /// <summary>The adaptor, once installed.</summary>
    /// <exception cref="System.InvalidOperationException">The journal is read before <see cref="Install"/> has run.</exception>
    member private _.Adaptor =
        match box adaptor with
        | null ->
            fail
                JournalStage
                $"the journal of grain type '{grainTypeName}' was used before its log-view adaptor was installed."
        | _ -> adaptor

    /// <summary>The resolved custom storage, or a diagnostic explaining why snapshots are unavailable.</summary>
    member private _.CustomStorage =
        match customStorage with
        | Some value -> value
        | None ->
            fail
                JournalStage
                $"grain type '{grainTypeName}' has no 'customStorage'. Application-controlled snapshots require Orleans' CustomStorage log-consistency provider and a typed IFunctionalJournalStorage implementation."

    /// <summary>Whether an append crossed a positive fixed snapshot boundary.</summary>
    member private _.CrossedBoundary(expectedVersion: int, resultingVersion: int, eventCount: int) =
        eventCount > 0
        && resultingVersion > expectedVersion
        && expectedVersion / eventCount < resultingVersion / eventCount

    /// <summary>Resolve manual, per-definition, and silo-wide snapshot precedence.</summary>
    member private this.ShouldSnapshot(expectedVersion: int, resultingVersion: int, state: obj, forced: bool) =
        if forced then
            true
        else
            let evaluateGlobal () =
                match globalSnapshotPolicy with
                | FunctionalJournalSnapshotDefault.Disabled -> false
                | FunctionalJournalSnapshotDefault.Every eventCount ->
                    if eventCount <= 0 then
                        fail
                            JournalStage
                            $"the silo-wide functional journal snapshot policy requires a positive event count, but {eventCount} was configured."

                    this.CrossedBoundary(expectedVersion, resultingVersion, eventCount)
                | FunctionalJournalSnapshotDefault.When predicate ->
                    if obj.ReferenceEquals(predicate, null) then
                        fail JournalStage "the silo-wide functional journal snapshot policy has a null predicate."

                    let context =
                        FunctionalJournalSnapshotContext(
                            grainTypeName,
                            grainContext.GrainId,
                            key,
                            resultingVersion,
                            blueprint.StateType,
                            state
                        )

                    try
                        predicate context
                    with cause ->
                        failCause
                            JournalStage
                            $"the silo-wide functional journal snapshot predicate failed for grain type '{grainTypeName}' on grain '{grainContext.GrainId}'. The event batch was not written."
                            cause

            match blueprint.SnapshotRule with
            | InheritSnapshotRule -> evaluateGlobal ()
            | DisableSnapshotRule -> false
            | EverySnapshotRule eventCount -> this.CrossedBoundary(expectedVersion, resultingVersion, eventCount)
            | ConditionalSnapshotRule predicate ->
                try
                    predicate resultingVersion state
                with cause ->
                    failCause
                        JournalStage
                        $"the 'snapshotPolicy' predicate of grain type '{grainTypeName}' failed on grain '{grainContext.GrainId}' at resulting version {resultingVersion}. The event batch was not written."
                        cause

    /// <summary>Write a snapshot without advancing the event version, retrying bounded CAS conflicts.</summary>
    member private this.WriteSnapshotAsync() : Task =
        let struct (storage, instance) = this.CustomStorage

        let rec write conflictRetries =
            task {
                let expectedVersion = this.Adaptor.ConfirmedVersion
                let state = this.ValueOf this.Adaptor.ConfirmedView

                let request: FunctionalJournalStorageWriteData =
                    { ExpectedVersion = expectedVersion
                      Events = []
                      Snapshot = Some(struct (expectedVersion, state)) }

                let! accepted =
                    task {
                        try
                            // A zero-event snapshot calls the typed store directly rather than
                            // through Orleans' adaptor. Ordinary exceptions therefore surface to
                            // the caller for an explicit retry; only the public permanent marker
                            // retires the activation.
                            return! storage.Append instance grainTypeName grainContext.GrainId key request
                        with :? FunctionalJournalPermanentStorageException as cause ->
                            this.RecordTerminalStorageFailure cause
                            this.RethrowTerminalStorageFailure "writing a manual snapshot"
                            return false
                    }

                if not accepted then
                    // A CAS rejection means durable state may already have advanced. Refresh even
                    // when this was the final permitted attempt so the surviving activation never
                    // continues from the stale confirmed view.
                    do! this.Adaptor.Synchronize()
                    this.RethrowJournalFailure "refreshing after a manual snapshot conflict"

                    if conflictRetries >= manualSnapshotMaxConflictRetries then
                        fail
                            JournalStage
                            $"manual snapshot of grain type '{grainTypeName}' on grain '{grainContext.GrainId}' was rejected by compare-and-swap after {conflictRetries + 1} attempt(s). ManualSnapshotMaxConflictRetries is {manualSnapshotMaxConflictRetries}; no snapshot was written."

                    return! write (conflictRetries + 1)
            }

        write 0 :> Task

    /// <summary>Bind the functional context factory used by journal callbacks.</summary>
    member _.BindContextFactory(factory: FunctionalStateScope -> FunctionalContextCore) =
        if obj.ReferenceEquals(factory, null) then
            invalidArg (nameof factory) "The journal context factory cannot be null."

        match contextFactory with
        | Some _ ->
            fail
                JournalStage
                $"the journal callback context factory of grain type '{grainTypeName}' was bound more than once."
        | None -> contextFactory <- Some factory

    /// <summary>Run one synchronous state-change callback in a fresh functional scope.</summary>
    member private _.InvokeStateChanged
        (operationName: string)
        (hook: FunctionalJournalStateChangedAdapter option)
        (state: obj)
        =
        match hook with
        | None -> ()
        | Some callback ->
            let makeContext =
                match contextFactory with
                | Some factory -> factory
                | None ->
                    fail
                        JournalStage
                        $"the journal of grain type '{grainTypeName}' received '{operationName}' before its functional callback context was bound."

            let scope = FunctionalStateScope(grainTypeName, operationName, true, Unavailable, false)

            try
                callback.Invoke(key, makeContext scope, state)
            finally
                scope.Expire()

    /// <summary>Run one synchronous connection callback in a fresh functional scope.</summary>
    member private this.InvokeConnectionChanged
        (operationName: string)
        (hook: FunctionalJournalConnectionIssueAdapter option)
        (issue: ConnectionIssue)
        =
        match hook with
        | None -> ()
        | Some callback ->
            let makeContext =
                match contextFactory with
                | Some factory -> factory
                | None ->
                    fail
                        JournalStage
                        $"the journal of grain type '{grainTypeName}' received '{operationName}' before its functional callback context was bound."

            let scope = FunctionalStateScope(grainTypeName, operationName, true, Unavailable, false)

            try
                callback.Invoke(key, makeContext scope, this.ValueOf this.Adaptor.ConfirmedView, issue)
            finally
                scope.Expire()

    /// <summary>
    /// Install the log-view adaptor for this activation. Runs at
    /// <c>GrainLifecycleStage.SetupState</c>, the same stage Orleans' own
    /// <c>LogConsistentGrain</c> installs at.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">The definition's named log-consistency provider is not registered on this silo, this silo has no matching protocol-services factory, or the declared (or default) journal storage is not registered.</exception>
    member this.Install() =
        let services = grainContext.ActivationServices

        let factory =
            match services.GetKeyedService<ILogViewAdaptorFactory> blueprint.ProviderName with
            | null ->
                fail
                    JournalStage
                    $"grain type '{grainTypeName}' names log-consistency provider '{blueprint.ProviderName}', which is not registered on this silo. Add it (for example AddLogStorageBasedLogConsistencyProvider \"{blueprint.ProviderName}\") to every silo which hosts this definition."
            | value -> value

        let isCustomStorageProvider =
            factory :? Orleans.EventSourcing.CustomStorage.LogConsistencyProvider

        match blueprint.CustomStorage, isCustomStorageProvider with
        | Some _, false ->
            fail
                JournalStage
                $"grain type '{grainTypeName}' declares 'customStorage', but log-consistency provider '{blueprint.ProviderName}' is '{factory.GetType().FullName}' rather than Orleans.EventSourcing.CustomStorage.LogConsistencyProvider. Register AddCustomStorageBasedLogConsistencyProvider under that name."
        | None, true ->
            fail
                JournalStage
                $"grain type '{grainTypeName}' uses Orleans CustomStorage log-consistency provider '{blueprint.ProviderName}' but declares no 'customStorage' implementation."
        | _ -> ()

        let protocolServices =
            match services.GetService typeof<Factory<IGrainContext, ILogConsistencyProtocolServices>> with
            | :? Factory<IGrainContext, ILogConsistencyProtocolServices> as make -> make.Invoke grainContext
            | _ ->
                fail
                    JournalStage
                    $"grain type '{grainTypeName}' names log-consistency provider '{blueprint.ProviderName}', but this silo has no Factory<IGrainContext, ILogConsistencyProtocolServices>. Every stock Add*BasedLogConsistencyProvider call registers one; a hand-registered ILogViewAdaptorFactory must call AddLogConsistencyProtocolServicesFactory() as well."

        let storage =
            if not factory.UsesStorageProvider then
                null
            else
                match blueprint.StorageName with
                | Some storageName ->
                    match services.GetKeyedService<IGrainStorage> storageName with
                    | null ->
                        fail
                            JournalStage
                            $"grain type '{grainTypeName}' names journal storage '{storageName}', which is not registered on this silo. Add that named IGrainStorage (for example AddMemoryGrainStorage \"{storageName}\") to every silo which hosts this definition."
                    | value -> value
                | None ->
                    match services.GetService<IGrainStorage>() with
                    | null ->
                        fail
                            JournalStage
                            $"grain type '{grainTypeName}' declares no 'journalStorage' and this silo has no default IGrainStorage, but log-consistency provider '{blueprint.ProviderName}' requires one. Declare 'journalStorage' or register a default storage provider."
                    | value -> value

        // The seed handed to the adaptor. It survives on the LogStorage provider, which folds into
        // this very cell, and is discarded by the StateStorage provider, which reads into a fresh
        // new(). ValueOf makes the two agree, so the seed here is a courtesy rather than the
        // mechanism -- but it is also what a ClearLogAsync restores on both providers, so it
        // carries the real initial state rather than an empty cell.
        let seed = this.View this.InitialState

        adaptor <-
            factory.MakeLogViewAdaptor<FunctionalJournalView, FunctionalJournalEntry>(
                this :> ILogViewAdaptorHost<FunctionalJournalView, FunctionalJournalEntry>,
                seed,
                grainTypeName,
                storage,
                protocolServices
            )

    /// <summary>
    /// Replay the journal before the activation serves anything, and surface a failing fold.
    /// </summary>
    /// <remarks>
    /// <c>PostOnActivate</c> only NOTIFIES the adaptor's batch worker: Orleans deliberately does
    /// not block an activation on the initial read, so a <c>JournaledGrain</c> can serve a call
    /// against a view that has not been read yet. A functional handler is handed its state as an
    /// argument and has no way to ask for a refresh, so the replay is forced to completion here.
    /// </remarks>
    member this.ReplayAsync() : Task =
        task {
            do! this.Adaptor.PostOnActivate()
            do! this.Adaptor.Synchronize()
            this.RethrowJournalFailure "replaying the journal"
        }
        :> Task

    /// <summary>Orleans' pre-activation adaptor callback.</summary>
    member this.PreActivateAsync() : Task = this.Adaptor.PreOnActivate()

    /// <summary>Orleans' post-deactivation adaptor callback: drain the batch worker.</summary>
    member this.DeactivateAsync() : Task =
        match box adaptor with
        | null -> Task.CompletedTask
        | _ -> adaptor.PostOnDeactivate()

    interface IConnectionIssueListener with
        /// <inheritdoc/>
        member this.OnConnectionIssue(issue: ConnectionIssue) =
            logger.LogWarning(
                "The journal of grain type {GrainType} on {GrainId} hit a storage issue and will retry: {Issue}",
                grainTypeName,
                grainContext.GrainId,
                issue
            )

            this.InvokeConnectionChanged "onConnectionIssue" blueprint.OnConnectionIssue issue

        /// <inheritdoc/>
        member this.OnConnectionIssueResolved(issue: ConnectionIssue) =
            logger.LogInformation(
                "The journal of grain type {GrainType} on {GrainId} recovered from a storage issue: {Issue}",
                grainTypeName,
                grainContext.GrainId,
                issue
            )

            this.InvokeConnectionChanged "onConnectionIssueResolved" blueprint.OnConnectionIssueResolved issue

    interface ILogViewAdaptorHost<FunctionalJournalView, FunctionalJournalEntry> with
        /// <summary>
        /// The replay fold. It runs when an event is raised and again for every event of the
        /// journal on every later activation, which is why <c>apply</c> has to be pure.
        /// </summary>
        /// <param name="view">The log-view cell to fold the event into.</param>
        /// <param name="entry">The journal entry carrying the encoded event to apply.</param>
        member this.UpdateView(view: FunctionalJournalView, entry: FunctionalJournalEntry) =
            try
                let current = this.ValueOf view
                let event = this.DecodeEvent(entry.CodecId, entry.Payload)
                let next = blueprint.Apply current event
                view.Payload <- this.EncodeState next
                view.HasValue <- true
                view.CodecId <- persistenceCodec.Id
            with cause ->
                // Orleans swallows this; remember it so the runtime can fail the turn.
                if isNull foldFailure then
                    foldFailure <- cause

                reraise ()

        /// <inheritdoc/>
        member this.OnViewChanged(tentative: bool, confirmed: bool) =
            // A permanent-storage fallback exists only to release Orleans' retry loop. Never
            // expose its synthetic state through callbacks.
            let storageIsHealthy = isNull (Volatile.Read(&terminalStorageFailure))

            if storageIsHealthy && tentative then
                this.InvokeStateChanged
                    "onTentativeStateChanged"
                    blueprint.OnTentativeStateChanged
                    (this.ValueOf this.Adaptor.TentativeView)

            if storageIsHealthy && confirmed then
                this.InvokeStateChanged
                    "onStateChanged"
                    blueprint.OnStateChanged
                    (this.ValueOf this.Adaptor.ConfirmedView)

    interface ICustomStorageInterface<FunctionalJournalView, FunctionalJournalEntry> with
        /// <summary>
        /// Load the latest application snapshot and fold its retained event tail into the mutable
        /// view Orleans' CustomStorage adaptor expects.
        /// </summary>
        member this.ReadStateFromStorage() : Task<KeyValuePair<int, FunctionalJournalView>> =
            task {
                match Volatile.Read(&terminalStorageFailure) with
                | null ->
                    let struct (storage, instance) = this.CustomStorage

                    try
                        let! read = storage.Read instance grainTypeName grainContext.GrainId key

                        let materialized =
                            try
                                if isNull (box read) then
                                    fail
                                        JournalStage
                                        $"custom storage of grain type '{grainTypeName}' returned a null read result."

                                if isNull (box read.Events) then
                                    fail
                                        JournalStage
                                        $"custom storage of grain type '{grainTypeName}' returned a null retained-event list."

                                let baseVersion, baseState =
                                    match read.Snapshot with
                                    | None -> 0, this.InitialState
                                    | Some struct (version, state) ->
                                        if version < 0 then
                                            fail
                                                JournalStage
                                                $"custom storage of grain type '{grainTypeName}' returned snapshot version {version}; versions cannot be negative."

                                        version, state

                                let resultingVersion64 = int64 baseVersion + int64 read.Events.Length

                                if resultingVersion64 > int64 Int32.MaxValue then
                                    fail
                                        JournalStage
                                        $"custom storage of grain type '{grainTypeName}' returned snapshot version {baseVersion} plus {read.Events.Length} retained event(s), which exceeds Int32.MaxValue."

                                let mutable state = baseState

                                for event in read.Events do
                                    try
                                        state <- blueprint.Apply state event
                                    with cause ->
                                        failCause
                                            JournalStage
                                            $"the 'apply' fold of grain type '{grainTypeName}' failed while replaying an event tail returned by custom storage for grain '{grainContext.GrainId}'."
                                            cause

                                let view = this.View state

                                Ok(KeyValuePair<int, FunctionalJournalView>(int resultingVersion64, view))
                            with cause ->
                                Error cause

                        match materialized with
                        | Ok value -> return value
                        | Error cause ->
                            this.RecordTerminalStorageFailure cause
                            return this.TerminalReadFallback()
                    with :? FunctionalJournalPermanentStorageException as cause ->
                        this.RecordTerminalStorageFailure cause
                        return this.TerminalReadFallback()
                | _ -> return this.TerminalReadFallback()
            }

        /// <summary>
        /// Translate Orleans' encoded delta batch into the typed CAS write, deciding whether the
        /// same atomic write carries a compacted snapshot.
        /// </summary>
        member this.ApplyUpdatesToStorage
            (updates: IReadOnlyList<FunctionalJournalEntry>, expectedVersion: int)
            : Task<bool> =
            task {
                match Volatile.Read(&terminalStorageFailure) with
                | null ->
                    let prepared =
                        try
                            if isNull (box updates) then
                                fail
                                    JournalStage
                                    $"Orleans CustomStorage supplied a null update batch to grain type '{grainTypeName}'."

                            if expectedVersion < 0 then
                                fail
                                    JournalStage
                                    $"Orleans CustomStorage supplied negative expected version {expectedVersion} to grain type '{grainTypeName}'."

                            let resultingVersion64 = int64 expectedVersion + int64 updates.Count

                            if resultingVersion64 > int64 Int32.MaxValue then
                                fail
                                    JournalStage
                                    $"the custom-storage append of grain type '{grainTypeName}' would advance version {expectedVersion} by {updates.Count}, beyond Int32.MaxValue."

                            this.RethrowFoldFailure "preparing a custom-storage append"

                            let events = ResizeArray<obj>(updates.Count)
                            let mutable state = this.ValueOf this.Adaptor.ConfirmedView
                            let mutable forced = false

                            for update in updates do
                                if isNull (box update) || isNull update.Payload then
                                    fail
                                        JournalStage
                                        $"Orleans CustomStorage supplied an empty functional journal entry to grain type '{grainTypeName}'."

                                let event = this.DecodeEvent(update.CodecId, update.Payload)
                                events.Add event
                                forced <- forced || update.SnapshotRequested

                                try
                                    state <- blueprint.Apply state event
                                with cause ->
                                    failCause
                                        JournalStage
                                        $"the 'apply' fold of grain type '{grainTypeName}' failed while preparing a custom-storage append for grain '{grainContext.GrainId}'. Nothing was written."
                                        cause

                            let resultingVersion = int resultingVersion64

                            let snapshot =
                                if this.ShouldSnapshot(expectedVersion, resultingVersion, state, forced) then
                                    Some(struct (resultingVersion, state))
                                else
                                    None

                            Ok
                                ({ ExpectedVersion = expectedVersion
                                   Events = events |> Seq.toList
                                   Snapshot = snapshot }: FunctionalJournalStorageWriteData)
                        with cause ->
                            Error cause

                    match prepared with
                    | Error cause ->
                        this.RecordTerminalStorageFailure cause
                        return true
                    | Ok request ->
                        let struct (storage, instance) = this.CustomStorage

                        try
                            return!
                                storage.Append instance grainTypeName grainContext.GrainId key request
                        with :? FunctionalJournalPermanentStorageException as cause ->
                            this.RecordTerminalStorageFailure cause
                            return true
                | _ -> return true
            }

        /// <summary>Delete the complete application-owned journal.</summary>
        member this.ClearStoredState() : Task =
            task {
                match Volatile.Read(&terminalStorageFailure) with
                | null ->
                    let struct (storage, instance) = this.CustomStorage

                    try
                        do! storage.Clear instance grainTypeName grainContext.GrainId key
                    with :? FunctionalJournalPermanentStorageException as cause ->
                        this.RecordTerminalStorageFailure cause
                | _ -> ()
            }
            :> Task

    interface IFunctionalJournalAccess with
        /// <summary>
        /// The CONFIRMED view: what a handler is handed. The tentative view is deliberately not
        /// used — with per-turn confirmation there are no unconfirmed entries at the start of a
        /// turn, and a state built from entries that are not durable yet is not a state a handler
        /// should make decisions on.
        /// </summary>
        member this.Current =
            this.RethrowJournalFailure "reading the confirmed state"
            this.ValueOf this.Adaptor.ConfirmedView

        /// <inheritdoc/>
        member this.Tentative =
            this.RethrowJournalFailure "reading the tentative state"
            this.ValueOf this.Adaptor.TentativeView

        /// <inheritdoc/>
        member this.ConfirmedVersion = this.Adaptor.ConfirmedVersion

        /// <inheritdoc/>
        member this.Unconfirmed =
            this.Adaptor.UnconfirmedSuffix
            |> Seq.map (fun entry -> this.DecodeEvent(entry.CodecId, entry.Payload))
            |> Seq.toList

        /// <inheritdoc/>
        member _.StateType = blueprint.StateType

        /// <inheritdoc/>
        member _.EventType = blueprint.EventType

        /// <inheritdoc/>
        member this.Raise(events: obj list) : unit =
            match events with
            | [] -> ()
            | _ ->
                this.EnsureFoldable events

                let entries =
                    events
                    |> List.map (fun event -> this.Entry(event, false))

                this.Adaptor.SubmitRange entries
                this.RethrowFoldFailure "submitting events"

        /// <inheritdoc/>
        member this.RaiseAndConfirm(events: obj list, forceSnapshot: bool) : Task =
            if forceSnapshot && customStorage.IsNone then
                fail
                    JournalStage
                    $"'snapshotNow' was requested by grain type '{grainTypeName}', but its definition declares no 'customStorage'."

            match events, forceSnapshot with
            | [], false ->
                // A handler that raised nothing performs no storage write at all. That is what
                // makes a query-shaped operation on a journaled grain as cheap as one on an
                // ordinary grain.
                Task.CompletedTask
            | [], true ->
                task {
                    // Include any events the callback submitted explicitly through raiseEvent(s)
                    // before materializing the callback-owned snapshot.
                    do! this.Adaptor.ConfirmSubmittedEntries()
                    this.RethrowJournalFailure "confirming events before a manual snapshot"
                    do! this.WriteSnapshotAsync()
                }
                :> Task
            | _, _ ->
                this.EnsureFoldable events

                task {
                    let entries =
                        events
                        |> List.mapi (fun index event ->
                            this.Entry(event, forceSnapshot && index = events.Length - 1))

                    // SubmitRange appends the whole batch atomically: one storage write, and a
                    // later replay can never observe half of a handler's events.
                    this.Adaptor.SubmitRange entries
                    do! this.Adaptor.ConfirmSubmittedEntries()
                    this.RethrowJournalFailure "appending events"
                }
                :> Task

        /// <inheritdoc/>
        member _.RequestSnapshot() =
            fail
                JournalStage
                "snapshotNow must be requested through the callback-scoped functional context."

        /// <inheritdoc/>
        member this.RaiseConditional(events: obj list) : Task<bool> =
            match events with
            | [] -> Task.FromResult true
            | _ ->
                this.EnsureFoldable events

                task {
                    let entries =
                        events
                        |> List.map (fun event -> this.Entry(event, false))

                    let! accepted = this.Adaptor.TryAppendRange entries
                    this.RethrowJournalFailure "appending events conditionally"
                    return accepted
                }

        /// <inheritdoc/>
        member this.Confirm() : Task =
            task {
                do! this.Adaptor.ConfirmSubmittedEntries()
                this.RethrowJournalFailure "confirming events"
            }
            :> Task

        /// <inheritdoc/>
        member this.Refresh() : Task =
            task {
                do! this.Adaptor.Synchronize()
                this.RethrowJournalFailure "refreshing the journal"
            }
            :> Task

        /// <inheritdoc/>
        member this.Retrieve(fromVersion: int, toVersion: int) : Task<obj list> =
            if fromVersion < 0 then
                invalidArg (nameof fromVersion) "invalid range"

            if toVersion < fromVersion then
                invalidArg (nameof toVersion) "invalid range"

            if toVersion > this.Adaptor.ConfirmedVersion then
                invalidArg (nameof toVersion) "invalid range"

            task {
                let! entries = this.Adaptor.RetrieveLogSegment(fromVersion, toVersion)

                return
                    entries
                    |> Seq.map (fun entry -> this.DecodeEvent(entry.CodecId, entry.Payload))
                    |> Seq.toList
            }

        /// <inheritdoc/>
        member this.Clear(cancellationToken: CancellationToken) : Task =
            task {
                do! this.Adaptor.ClearLogAsync cancellationToken
                this.RethrowJournalFailure "clearing the journal"
            }
            :> Task

        /// <inheritdoc/>
        member this.EnableStats() = this.Adaptor.EnableStatsCollection()

        /// <inheritdoc/>
        member this.DisableStats() = this.Adaptor.DisableStatsCollection()

        /// <inheritdoc/>
        member this.GetStats() = this.Adaptor.GetStats()
