namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.EventSourcing
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
        logger: ILogger,
        key: obj
    ) =

    let mutable adaptor: ILogViewAdaptor<FunctionalJournalView, FunctionalJournalEntry> =
        Unchecked.defaultof<_>

    /// The first exception an <c>apply</c> fold threw, which Orleans would otherwise swallow.
    let mutable foldFailure: exn = null

    /// <summary>The declared initial state of this grain, boxed. Re-derived, never stored.</summary>
    member private _.InitialState = blueprint.Initial key

    /// <summary>
    /// The state a view cell holds. A cell that was never written — a fresh <c>new()</c> instance
    /// Orleans materialized on a read that found no record — reports the declared initial state
    /// instead of a null payload.
    /// </summary>
    /// <param name="view">The log-view cell to read, or <c>null</c> for a never-materialized view.</param>
    member private this.ValueOf(view: FunctionalJournalView) : obj =
        if isNull (box view) then this.InitialState
        elif view.HasValue && not (isNull view.Payload) then blueprint.DecodeState codec view.Payload
        else this.InitialState

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
    /// <param name="events">The boxed events about to be submitted, folded in order over the current confirmed state.</param>
    /// <exception cref="System.InvalidOperationException">The <c>apply</c> fold threw for one of <paramref name="events"/>.</exception>
    member private this.EnsureFoldable(events: obj list) =
        let mutable state = (this :> IFunctionalJournalAccess).Current

        for event in events do
            try
                state <- blueprint.Apply state event
            with cause ->
                failCause
                    JournalStage
                    $"the 'apply' fold of grain type '{grainTypeName}' failed for an event raised by grain '{grainContext.GrainId}'. Nothing was appended: the fold is run over the confirmed state before the events are submitted, because Orleans' adaptors fold an entry only after the storage write that made it durable — an event whose fold throws would otherwise stay in the journal and fail every later replay."
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

    /// <summary>The adaptor, once installed.</summary>
    /// <exception cref="System.InvalidOperationException">The journal is read before <see cref="Install"/> has run.</exception>
    member private _.Adaptor =
        match box adaptor with
        | null ->
            fail
                JournalStage
                $"the journal of grain type '{grainTypeName}' was used before its log-view adaptor was installed."
        | _ -> adaptor

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
        let seed =
            FunctionalJournalView(Payload = blueprint.EncodeState codec this.InitialState, HasValue = true)

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
            this.RethrowFoldFailure "replaying the journal"
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
        member _.OnConnectionIssue(issue: ConnectionIssue) =
            logger.LogWarning(
                "The journal of grain type {GrainType} on {GrainId} hit a storage issue and will retry: {Issue}",
                grainTypeName,
                grainContext.GrainId,
                issue
            )

        /// <inheritdoc/>
        member _.OnConnectionIssueResolved(issue: ConnectionIssue) =
            logger.LogInformation(
                "The journal of grain type {GrainType} on {GrainId} recovered from a storage issue: {Issue}",
                grainTypeName,
                grainContext.GrainId,
                issue
            )

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
                let event = blueprint.DecodeEvent codec entry.Payload
                let next = blueprint.Apply current event
                view.Payload <- blueprint.EncodeState codec next
                view.HasValue <- true
            with cause ->
                // Orleans swallows this; remember it so the runtime can fail the turn.
                if isNull foldFailure then
                    foldFailure <- cause

                reraise ()

        /// <inheritdoc/>
        member _.OnViewChanged(_tentative: bool, _confirmed: bool) = ()

    interface IFunctionalJournalAccess with
        /// <summary>
        /// The CONFIRMED view: what a handler is handed. The tentative view is deliberately not
        /// used — with per-turn confirmation there are no unconfirmed entries at the start of a
        /// turn, and a state built from entries that are not durable yet is not a state a handler
        /// should make decisions on.
        /// </summary>
        member this.Current =
            this.RethrowFoldFailure "reading the confirmed state"
            this.ValueOf this.Adaptor.ConfirmedView

        /// <inheritdoc/>
        member this.ConfirmedVersion = this.Adaptor.ConfirmedVersion

        /// <inheritdoc/>
        member _.EventType = blueprint.EventType

        /// <inheritdoc/>
        member this.RaiseAndConfirm(events: obj list) : Task =
            match events with
            | [] ->
                // A handler that raised nothing performs no storage write at all. That is what
                // makes a query-shaped operation on a journaled grain as cheap as one on an
                // ordinary grain.
                Task.CompletedTask
            | _ ->
                this.EnsureFoldable events

                task {
                    let entries =
                        events
                        |> List.map (fun event -> FunctionalJournalEntry(Payload = blueprint.EncodeEvent codec event))

                    // SubmitRange appends the whole batch atomically: one storage write, and a
                    // later replay can never observe half of a handler's events.
                    this.Adaptor.SubmitRange entries
                    do! this.Adaptor.ConfirmSubmittedEntries()
                    this.RethrowFoldFailure "appending events"
                }
                :> Task

        /// <inheritdoc/>
        member this.RaiseConditional(events: obj list) : Task<bool> =
            match events with
            | [] -> Task.FromResult true
            | _ ->
                this.EnsureFoldable events

                task {
                    let entries =
                        events
                        |> List.map (fun event -> FunctionalJournalEntry(Payload = blueprint.EncodeEvent codec event))

                    let! accepted = this.Adaptor.TryAppendRange entries
                    this.RethrowFoldFailure "appending events conditionally"
                    return accepted
                }
