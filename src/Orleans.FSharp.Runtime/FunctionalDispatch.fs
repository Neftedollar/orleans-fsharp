namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.BroadcastChannel
open Orleans.Runtime
open Orleans.Streams
open Orleans.Streams.Core
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>Everything the dispatch body of one activation needs.</summary>
[<ReferenceEquality>]
type internal FunctionalTargetEnvironment =
    {
        /// The definition this activation hosts.
        Definition: FunctionalHostedDefinition
        /// The Orleans activation context.
        GrainContext: IGrainContext
        /// The activation service provider.
        Services: IServiceProvider
        /// The activation's grain factory.
        GrainFactory: IGrainFactory
        /// A logger scoped to this grain type.
        Logger: ILogger
        /// The registered time provider.
        TimeProvider: TimeProvider
        /// The exact-type payload codec of this silo.
        Codec: FunctionalPayloadCodec
        /// The local payload limit of this silo.
        MaxPayloadBytes: int
        /// The domain key, decoded once from the grain identity.
        Key: obj
        /// The primary state holder and every attached persistent facet of this activation.
        State: FunctionalActivationState
        /// Wrapper for the protected Orleans deactivate-on-idle method.
        DeactivateOnIdle: unit -> unit
        /// Wrapper for the protected Orleans delay-deactivation method.
        DelayDeactivation: TimeSpan -> unit
        /// Register or update one durable reminder on the real Orleans reminder service.
        RegisterReminder: string -> TimeSpan -> TimeSpan -> Task<IGrainReminder>
        /// Create one declared timer, tracked by the target for guaranteed disposal.
        CreateTimer: (CancellationToken -> Task) -> GrainTimerCreationOptions -> unit
    }

/// <summary>
/// Construction of the per-callback invocation context. Every request, activation hook,
/// deactivation hook, timer, and reminder callback receives a fresh context whose
/// persistent-state facades are bound to that callback's scope.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalContextFactory =

    /// <summary>
    /// The invocation context core of one callback. The persistent-state lookup resolves the
    /// facet by its logical <c>(stateName, providerName, storedType)</c> identity and returns a
    /// scope-bound facade; an unattached descriptor resolves to nothing, which the typed
    /// <c>context.persistentState</c> member turns into a deterministic diagnostic.
    /// </summary>
    let core
        (env: FunctionalTargetEnvironment)
        (cancellationToken: CancellationToken)
        (scope: FunctionalStateScope)
        =
        { GrainId = env.GrainContext.GrainId
          GrainFactory = env.GrainFactory
          Services = env.Services
          Logger = env.Logger
          TimeProvider = env.TimeProvider
          UtcNow = env.TimeProvider.GetUtcNow()
          CancellationToken = cancellationToken
          StreamSequenceToken = null
          DeactivateOnIdle = env.DeactivateOnIdle
          DelayDeactivation = env.DelayDeactivation
          ResolvePersistentState =
            fun descriptor ->
                match env.State.TryResolve descriptor with
                | Some facet -> facet.Blueprint.Facade facet.Instance scope
                | None -> null
          ResolveTransactionalState =
            fun descriptor ->
                match env.State.TryResolveTransactional descriptor with
                | Some facet ->
                    facet.Blueprint.Facade
                        facet.Instance
                        facet.Initial
                        env.Definition.GrainTypeName
                        (env.Codec :> IFunctionalPayloadCodec)
                        scope
                | None -> null
          Journal =
            // The activation's journal is bound to THIS callback's scope, so a captured context
            // cannot append after its turn and a state-neutral callback cannot append at all.
            match env.State.Journal with
            | null -> null
            | journal -> FunctionalScopedJournal(journal, scope) :> IFunctionalJournalAccess }

    /// <summary>
    /// The invocation context core of one implicit stream or broadcast delivery: the ordinary
    /// core plus the Orleans cursor of the item being delivered, which
    /// <c>context.streamSequenceToken</c> surfaces. The cursor is <c>null</c> for a
    /// non-rewindable stream provider and always <c>null</c> for a broadcast channel.
    /// </summary>
    let streamCore
        (env: FunctionalTargetEnvironment)
        (cancellationToken: CancellationToken)
        (scope: FunctionalStateScope)
        (sequenceToken: StreamSequenceToken)
        =
        { core env cancellationToken scope with
            StreamSequenceToken = sequenceToken }

/// <summary>
/// Target-side dispatch of one fixed request, in the specification's exact validation order.
/// Protocol validation raises transport-stage diagnostics before any application code runs, so
/// a protocol failure is always distinguishable from an application handler exception, which
/// travels the ordinary Orleans response-exception path.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalDispatch =

    /// <summary>
    /// Steps 1 through 3 of the normative validation order, shared by the unary and the streaming
    /// entry points: fixed envelope shape, admitted version, payload size, token length, reserved
    /// flags, descriptor resolution, <c>sinceVersion</c>, reply shape, protocol token, and
    /// admission flags. It returns the resolved descriptor, the admitted request version, and that
    /// version's token pair.
    /// </summary>
    /// <param name="expectStreaming">
    /// Which entry point is admitting the request. A descriptor of the other kind is rejected here,
    /// by name, before the token comparison -- the token comparison would also reject it (the two
    /// kinds hash different direction literals) but would report a digest mismatch rather than the
    /// shape mismatch that actually happened.
    /// </param>
    let internal admit
        (env: FunctionalTargetEnvironment)
        (envelope: FunctionalRequestEnvelope)
        (expectStreaming: bool)
        : struct (FunctionalHostedOperation * int * byte[] * byte[]) =
        let definition = env.Definition
        let grainTypeName = definition.GrainTypeName

        // 1. Fixed envelope shape, grain type, contract version, payload size, token length,
        //    and reserved flags.
        if obj.ReferenceEquals(envelope, null) then
            fail TransportStage $"grain type '{grainTypeName}' received a request without an envelope."

        if not (String.Equals(envelope.GrainType, grainTypeName, StringComparison.Ordinal)) then
            fail
                TransportStage
                $"this activation hosts grain type '{grainTypeName}' but received a request addressed to '{envelope.GrainType}'."

        // Spec 004 item 7: admission is the ONLY thing the version policy changes. Under the
        // default Exact policy this is the spec-003 equality test and the spec-003 sentence,
        // unchanged; under BackwardCompatible it is a range test. Everything downstream --
        // descriptor resolution, admission flags, the payload codec, storage identity -- reads
        // the same values either way.
        if not (definition.AcceptsVersion envelope.ContractVersion) then
            fail TransportStage (definition.VersionRejection envelope.ContractVersion)

        // The version the rest of dispatch answers in. It is the CALLER's version, not the
        // hosted one: protocol tokens are version-derived, so an admitted older caller must be
        // met with its own version's tokens in both directions.
        let requestVersion = envelope.ContractVersion

        if isNull envelope.Payload then
            fail
                TransportStage
                $"the request for operation '{envelope.OperationId}' on grain type '{grainTypeName}' carries no payload."

        PayloadLimit.ensure
            SiloRequestReceive
            grainTypeName
            envelope.OperationId
            envelope.Payload.Length
            env.MaxPayloadBytes

        if isNull envelope.ProtocolToken || envelope.ProtocolToken.Length <> ProtocolToken.Length then
            let actual =
                if isNull envelope.ProtocolToken then 0 else envelope.ProtocolToken.Length

            fail
                TransportStage
                $"the request for operation '{envelope.OperationId}' on grain type '{grainTypeName}' carries a protocol token of {actual} bytes; exactly {ProtocolToken.Length} bytes are required."

        if AdmissionFlags.hasReserved envelope.AdmissionFlags then
            fail
                TransportStage
                $"the request for operation '{envelope.OperationId}' on grain type '{grainTypeName}' sets a reserved admission-flag bit (mask 0x{AdmissionFlags.Reserved:x2})."

        // 2. Resolve the immutable descriptor by ordinal operation ID.
        let operation =
            match definition.TryFindOperation envelope.OperationId with
            | Some operation -> operation
            | None ->
                fail
                    TransportStage
                    $"grain type '{grainTypeName}' hosts no operation '{envelope.OperationId}' at contract version {definition.Version}."

        // 2b. Spec 004 item 7: the operation must exist at the ADMITTED version. Checked before
        //     the token comparison because it is the more specific fault and the token check
        //     cannot catch it -- an older caller's token is computed from its own version, which
        //     is exactly the token this host now expects for that version, so a v(n-1) call to an
        //     operation introduced at v(n) would otherwise be admitted and its argument
        //     deserialized as the newer declared type.
        if operation.SinceVersion > requestVersion then
            fail
                TransportStage
                $"operation '{operation.OperationId}' on grain type '{grainTypeName}' was introduced at contract version {operation.SinceVersion}, but the request declares version {requestVersion}."

        // 2c. Spec 004 item 6: the descriptor's reply shape must be the one this entry point
        //     serves. A unary caller reaching a streaming descriptor (or the reverse) is a shape
        //     mismatch, and saying so is more useful than the digest mismatch the token comparison
        //     below would report for the same call.
        if expectStreaming <> operation.IsStreaming then
            if expectStreaming then
                fail
                    TransportStage
                    $"operation '{operation.OperationId}' on grain type '{grainTypeName}' returns Task<_> and was opened as a stream. Only an operation whose API field returns IAsyncEnumerable<_> can be enumerated."
            else
                fail
                    TransportStage
                    $"operation '{operation.OperationId}' on grain type '{grainTypeName}' returns IAsyncEnumerable<_> and was called as an ordinary operation. A streaming operation can only be enumerated."

        // 3. Compare the exact protocol token and the admission flags with the descriptor, at the
        //    admitted request version.
        let struct (expectedRequestToken, expectedReplyToken) = operation.TokensFor requestVersion

        if not (ProtocolToken.equal envelope.ProtocolToken expectedRequestToken) then
            fail
                TransportStage
                $"the request for operation '{operation.OperationId}' on grain type '{grainTypeName}' carries protocol token {ProtocolToken.toHex envelope.ProtocolToken}, but {ProtocolToken.toHex expectedRequestToken} was expected."

        if envelope.AdmissionFlags <> operation.AdmissionFlags then
            fail
                TransportStage
                $"the request for operation '{operation.OperationId}' on grain type '{grainTypeName}' carries admission flags 0x{envelope.AdmissionFlags:x2}, but the hosted descriptor declares 0x{operation.AdmissionFlags:x2}."

        struct (operation, requestVersion, expectedRequestToken, expectedReplyToken)

    /// <summary>Dispatch one request on this activation.</summary>
    let dispatch
        (env: FunctionalTargetEnvironment)
        (envelope: FunctionalRequestEnvelope)
        (cancellationToken: CancellationToken)
        : ValueTask<FunctionalReply> =
        let definition = env.Definition
        let grainTypeName = definition.GrainTypeName

        let struct (operation, requestVersion, _, expectedReplyToken) = admit env envelope false

        // 4-6. Typed payload deserialization with a fresh session, the per-invocation context,
        //      the preclosed typed handler adapter, and the state-publication rule.

        // The delivered one-way context uses CancellationToken.None. A one-way caller completed
        // at the local acknowledgement and can never signal cancellation, so the target-local
        // token would be a token that cannot be cancelled but CAN be disposed underneath a
        // handler which registered on it — the request disposes its own CancellationTokenSource.
        let invocationToken =
            if operation.IsOneWay then
                CancellationToken.None
            else
                cancellationToken

        // A read-only or state-neutral interleaved callback discards its replacement state, so
        // its persistent-state facades permit getters and reject the setter and every storage
        // call. The scope expires as soon as the callback's task completes.
        //
        // Spec 004 item 2 adds the third case, for the same reason and with the same mechanism: a
        // transaction-scoped operation (Create, CreateOrJoin, Join) can have every durable effect
        // it made undone by an abort, and neither an in-memory state publication nor a persistent
        // storage write has any participant that could undo it. Rather than let one aborted
        // transaction leave an activation half-updated, such an operation is state-neutral for
        // everything except its transactional facets: the transactional facade is enabled by the
        // separate TransactionalAccess axis below, so the two rules do not fight.
        let stateNeutral =
            operation.IsReadOnly || operation.IsAlwaysInterleave || operation.IsTransactionScoped

        // Which transactional facet operations this callback may perform. 'Supported' is included
        // because Orleans forwards a caller's ambient context to it; Suppress and NotAllowed never
        // have one, so they get the same rejection an ordinary operation gets.
        let transactionalAccess =
            match operation.Transaction with
            | Some Orleans.TransactionOption.Create
            | Some Orleans.TransactionOption.CreateOrJoin
            | Some Orleans.TransactionOption.Join
            | Some Orleans.TransactionOption.Supported ->
                if operation.IsReadOnly then
                    ReadOnlyTransaction
                else
                    ReadWriteTransaction
            | _ -> Unavailable

        let scope =
            FunctionalStateScope(grainTypeName, operation.OperationId, not stateNeutral, transactionalAccess)

        let invocation =
            { Key = env.Key
              Core = FunctionalContextFactory.core env invocationToken scope
              State = env.State.Current
              Payload = envelope.Payload
              Codec = env.Codec }

        let work =
            task {
                // "Logs and activities contain grain type, operation ID, version, grain ID, and
                // outcome; payload bytes and deserialized application values are excluded by
                // default." One trace line covers every dispatched request — acknowledged and
                // one-way, success and failure alike — with exactly those fields and nothing
                // application-shaped. It is logged in the outermost `finally` so it fires on
                // every exit path, success or failure.
                let mutable outcome = "success"

                try
                    try
                        try
                            let! nextState, payload =
                                task {
                                    try
                                        return! operation.Adapter.Invoke invocation
                                    finally
                                        scope.Expire()
                                }

                            // Read-only, state-neutral interleaved, and transaction-scoped
                            // operations discard their replacement.
                            //
                            // Spec 004 item 3: for a journaled definition the adapter's first
                            // result is not a replacement state but the boxed list of events the
                            // handler raised, so the same rule appends and confirms them instead
                            // of publishing. Confirmation happens HERE -- after the handler
                            // returned and before the reply is built -- which is what makes
                            // "confirmed before the caller sees the reply" a property of the
                            // runtime rather than of every handler.
                            match env.State.Journal with
                            | null ->
                                if not stateNeutral then
                                    env.State.Publish nextState
                            | journal ->
                                let raised = unbox<obj list> nextState

                                if stateNeutral then
                                    // A readOnly or alwaysInterleave operation may run while
                                    // another turn is in flight, so an append from it would
                                    // interleave with that turn's own appends and be ordered by
                                    // nothing. Dropping the events silently would be worse: the
                                    // handler believed it had changed the grain.
                                    if not (List.isEmpty raised) then
                                        fail
                                            JournalStage
                                            $"operation '{operation.OperationId}' of grain type '{grainTypeName}' raised {List.length raised} event(s), but it is declared 'readOnly' or 'alwaysInterleave'. Such an operation may run while another turn of this activation is in flight, so its appends could not be ordered against that turn's. Declare the operation without 'readOnly'/'alwaysInterleave', or return no events."
                                else
                                    do! journal.RaiseAndConfirm raised

                            // 7. The silo reply limit, then the descriptor's reply token and
                            //    fresh payload.
                            PayloadLimit.ensure
                                SiloReplySend
                                grainTypeName
                                operation.OperationId
                                payload.Length
                                env.MaxPayloadBytes

                            return FunctionalReply(expectedReplyToken, payload)
                        with error when operation.IsOneWay ->
                            // A one-way target failure is never returned to that caller: the
                            // caller's send completed at the local acknowledgement. It is
                            // recorded here so the failure is not silent, then rethrown onto the
                            // ordinary Orleans path so the runtime's own one-way logging still
                            // observes it.
                            env.Logger.LogError(
                                error,
                                "Functional one-way operation {OperationId} on grain type {GrainType} failed on target {GrainId}: {Message}",
                                operation.OperationId,
                                grainTypeName,
                                env.GrainContext.GrainId,
                                error.Message
                            )

                            ExceptionDispatchInfo.Capture(error).Throw()
                            return Unchecked.defaultof<FunctionalReply>
                    with error ->
                        // Covers the acknowledged-call arm of the same rule directly, and the
                        // rethrown one-way exception a second time — either way the application
                        // exception still follows Orleans' ordinary response-exception path
                        // unaltered; only the traced outcome changes here. `reraise()` cannot be
                        // used here: F#'s task computation expression desugars `with` into a
                        // continuation call rather than a literal try-with, which is the one
                        // context `reraise()` requires.
                        outcome <- "failed"
                        ExceptionDispatchInfo.Capture(error).Throw()
                        return Unchecked.defaultof<FunctionalReply>
                finally
                    // "Logs and activities contain grain type, operation ID, version, grain ID,
                    // and outcome; payload bytes and application values are excluded." The
                    // ambient Activity (if any diagnostic listener started one for this request)
                    // is tagged with exactly those five fields and nothing application-shaped.
                    // Tagging is unconditional — SetTag is a cheap dictionary write and only runs
                    // at all when an Activity is actually current.
                    match Activity.Current with
                    | null -> ()
                    | activity ->
                        activity
                            .SetTag("grainType", grainTypeName)
                            .SetTag("operationId", operation.OperationId)
                            .SetTag("version", requestVersion)
                            .SetTag("grainId", string env.GrainContext.GrainId)
                            .SetTag("outcome", outcome)
                        |> ignore

                    // The per-call trace line is Debug-level and fires on every dispatched
                    // request; guard it so a production silo running at Information (or above)
                    // never boxes the five arguments into an object[] just to discard them.
                    if env.Logger.IsEnabled LogLevel.Debug then
                        env.Logger.LogDebug(
                            "Functional dispatch grainType={GrainType} operationId={OperationId} version={Version} grainId={GrainId} outcome={Outcome}",
                            grainTypeName,
                            operation.OperationId,
                            requestVersion,
                            env.GrainContext.GrainId,
                            outcome
                        )
            }

        ValueTask<FunctionalReply> work

/// <summary>
/// The target-side enumerator of one server-streaming operation. Spec 004 item 6.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy on purpose.</b> Every protocol check runs inside the first <c>MoveNextAsync</c>, not in
/// the constructor: <c>AsyncEnumerableGrainExtension.StartEnumeration</c> calls
/// <c>InvokeImplementation</c> and <c>GetAsyncEnumerator</c> outside its <c>try</c> and
/// <c>MoveNextAsync</c> inside it, so a rejection raised from the first pull becomes a clean
/// <c>EnumerationResult.Error</c> — the extension removes and disposes the enumerator, and the
/// caller's <c>MoveNextAsync</c> rethrows the original exception — whereas one raised earlier would
/// escape the grain call with a half-built entry left in the extension's table.
/// </para>
/// <para>
/// <b>State-neutral by construction.</b> The scope is created with <c>allowsMutation = false</c>
/// and no transactional access, and no replacement state is ever published: a stream produces
/// across many turns of the activation (Orleans pulls it with a separate, always-interleaving
/// <c>MoveNext</c> per batch), so a whole-state replacement published at the end would overwrite
/// every other turn that ran while it was open. The state the handler sees is the snapshot taken
/// at the first pull.
/// </para>
/// </remarks>
[<Sealed>]
type internal FunctionalStreamEnumerator
    (env: FunctionalTargetEnvironment, envelope: FunctionalRequestEnvelope, cancellationToken: CancellationToken) =

    let mutable started = false
    let mutable source: IAsyncEnumerator<byte[]> = null
    let mutable scope: FunctionalStateScope option = None
    let mutable operationId = envelope.OperationId
    let mutable requestVersion = 0
    let mutable itemToken: byte[] = null
    let mutable current = Unchecked.defaultof<FunctionalReply>
    let mutable outcome = "success"
    let mutable disposed = false

    /// Steps 1 through 6 of the dispatch order, run once, on the first pull.
    member private _.Start() =
        let grainTypeName = env.Definition.GrainTypeName
        let struct (operation, version, _, expectedItemToken) = FunctionalDispatch.admit env envelope true

        operationId <- operation.OperationId
        requestVersion <- version
        itemToken <- expectedItemToken

        // A streaming operation is state-neutral: no replacement state, no persistent-state
        // mutation, no transactional facet. Sealing already rejects 'readOnly', 'oneWay',
        // 'alwaysInterleave' and 'transactional' on a streaming field, so this is the only
        // configuration such an operation can have.
        let callScope =
            FunctionalStateScope(grainTypeName, operation.OperationId, false, Unavailable)

        scope <- Some callScope

        let invocation =
            { Key = env.Key
              Core = FunctionalContextFactory.core env cancellationToken callScope
              State = env.State.Current
              Payload = envelope.Payload
              Codec = env.Codec }

        source <- operation.StreamAdapter.Invoke(invocation, cancellationToken)

    interface IAsyncEnumerator<FunctionalReply> with
        member _.Current = current

        member this.MoveNextAsync() =
            ValueTask<bool>(
                task {
                    try
                        if not started then
                            started <- true
                            this.Start()

                        match! source.MoveNextAsync() with
                        | false -> return false
                        | true ->
                            let payload = source.Current

                            PayloadLimit.ensure
                                SiloStreamItemSend
                                env.Definition.GrainTypeName
                                operationId
                                payload.Length
                                env.MaxPayloadBytes

                            current <- FunctionalReply(itemToken, payload)
                            return true
                    with error ->
                        outcome <- "failed"
                        ExceptionDispatchInfo.Capture(error).Throw()
                        return false
                }
            )

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            if disposed then
                ValueTask()
            else
                disposed <- true

                ValueTask(
                    task {
                        try
                            // Disposing the handler's own enumerator is what runs its finally
                            // blocks. Orleans cancels the enumeration's token first (in
                            // AsyncEnumerableGrainExtension.DisposeEnumeratorAsync), so a handler
                            // that awaits on the token is already unblocked by the time this runs.
                            match source with
                            | null -> ()
                            | enumerator -> do! enumerator.DisposeAsync()
                        finally
                            match scope with
                            | None -> ()
                            | Some callScope -> callScope.Expire()

                            match Activity.Current with
                            | null -> ()
                            | activity ->
                                activity
                                    .SetTag("grainType", env.Definition.GrainTypeName)
                                    .SetTag("operationId", operationId)
                                    .SetTag("version", requestVersion)
                                    .SetTag("grainId", string env.GrainContext.GrainId)
                                    .SetTag("outcome", outcome)
                                |> ignore

                            if env.Logger.IsEnabled LogLevel.Debug then
                                env.Logger.LogDebug(
                                    "Functional stream grainType={GrainType} operationId={OperationId} version={Version} grainId={GrainId} outcome={Outcome}",
                                    env.Definition.GrainTypeName,
                                    operationId,
                                    requestVersion,
                                    env.GrainContext.GrainId,
                                    outcome
                                )
                    }
                )

/// <summary>
/// The functional half of the Orleans activation lifecycle. It runs between the stock
/// <c>SetupState</c> load and the completion of activation, and again during deactivation,
/// and it never issues a storage call of its own.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalLifecycle =

    /// <summary>
    /// Step 5 of the activation order: reconcile every declared reminder, in declaration order,
    /// through <c>RegisterOrUpdateReminder</c>. Each call is awaited before the next one starts,
    /// so "declaration order" is a real sequential guarantee and not just registration-call order.
    /// </summary>
    let private reconcileReminders (env: FunctionalTargetEnvironment) : Task =
        task {
            for reminder in env.Definition.Reminders do
                let! _ = env.RegisterReminder reminder.Name reminder.DueTime reminder.Period
                ()
        }
        :> Task

    /// <summary>
    /// Step 6 of the activation order: create every declared timer from the
    /// <c>GrainTimerCreationOptions</c> copied at sealing. Each callback builds a fresh
    /// invocation context per tick — token from the Orleans timer callback, whole-state
    /// replacement under <c>Interleave = false</c> — and publishes its returned state exactly
    /// like a handler return.
    /// </summary>
    let private createTimers (env: FunctionalTargetEnvironment) =
        for timer in env.Definition.Timers do
            let options =
                GrainTimerCreationOptions(
                    DueTime = timer.DueTime,
                    Period = timer.Period,
                    Interleave = timer.Interleave,
                    KeepAlive = timer.KeepAlive
                )

            let callback (token: CancellationToken) : Task =
                task {
                    let scope =
                        FunctionalStateScope(env.Definition.GrainTypeName, $"onTimer:{timer.Name}", true)

                    try
                        let core = FunctionalContextFactory.core env token scope
                        let! next = timer.Adapter.Invoke(env.Key, core, env.State.Current)
                        env.State.Publish next
                    finally
                        scope.Expire()
                }
                :> Task

            env.CreateTimer callback options

    /// <summary>
    /// Steps 3 through 6 of the activation order: initialize the ephemeral primary state and
    /// every attached holder which reports no durable record; run the functional
    /// <c>onActivate</c> hook, whose replacement is published in memory only; reconcile declared
    /// reminders; then create declared timers. A storage read, an initializer, or the hook
    /// failing all fail the activation before reminders or timers are ever touched.
    /// </summary>
    let activate (env: FunctionalTargetEnvironment) (cancellationToken: CancellationToken) : Task =
        task {
            env.State.Initialize env.Key

            match env.Definition.OnActivate, env.Definition.Journal with
            | _, Some journal ->
                // A journaled definition's activation hook returns no replacement state: the
                // journal has already been replayed by the time this runs, and the only way to
                // change the state is to raise an event.
                match journal.OnActivate with
                | None -> ()
                | Some hook ->
                    // allowsMutation = true: a journaled definition has no persistent facet for
                    // the flag to govern, and this hook runs as an ordinary turn of the activation
                    // with nothing else in flight, so it MAY append through raiseConditional.
                    let scope =
                        FunctionalStateScope(env.Definition.GrainTypeName, "onActivate", true)

                    try
                        let core = FunctionalContextFactory.core env cancellationToken scope
                        do! hook.Invoke(env.Key, core, env.State.Current)
                    finally
                        scope.Expire()
            | None, None -> ()
            | Some hook, None ->
                let scope =
                    FunctionalStateScope(env.Definition.GrainTypeName, "onActivate", true)

                try
                    let core = FunctionalContextFactory.core env cancellationToken scope
                    let! next = hook.Invoke(env.Key, core, env.State.Current)
                    env.State.Publish next
                finally
                    scope.Expire()

            do! reconcileReminders env
            createTimers env
        }
        :> Task

    /// <summary>
    /// The functional <c>onDeactivate</c> hook, run before the lifecycle <c>OnStop</c> stages.
    /// The hook may write explicitly and receives no library retry; a hook or storage failure is
    /// logged here and then travels the ordinary Orleans stop path, which observes it while the
    /// remaining stop stages still run. Activation-local cleanup happens in <c>finally</c>.
    /// </summary>
    let deactivate
        (env: FunctionalTargetEnvironment)
        (reason: DeactivationReason)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            match env.Definition.Journal with
            | Some journal ->
                match journal.OnDeactivate with
                | None -> ()
                | Some hook ->
                    let scope =
                        FunctionalStateScope(env.Definition.GrainTypeName, "onDeactivate", true)

                    try
                        try
                            let core = FunctionalContextFactory.core env cancellationToken scope
                            do! hook.Invoke(env.Key, core, reason, env.State.Current)
                        with error ->
                            env.Logger.LogError(
                                error,
                                "Functional onDeactivate hook of grain type {GrainType} failed on {GrainId} (reason {Reason}): {Message}. The runtime performs no retry.",
                                env.Definition.GrainTypeName,
                                env.GrainContext.GrainId,
                                reason.ReasonCode,
                                error.Message
                            )

                            ExceptionDispatchInfo.Capture(error).Throw()
                    finally
                        scope.Expire()
            | None ->

            match env.Definition.OnDeactivate with
            | None -> ()
            | Some _ when not env.State.IsInitialized ->
                // The activation never reached state initialization — its storage read,
                // initializer, or activation hook failed — so there is no primary state value to
                // hand the hook. Running it with an absent state would turn a clear activation
                // failure into an unrelated one inside application code.
                //
                // Orleans 10.1.0 and 10.2.2 do not invoke OnDeactivateAsync for an activation
                // whose OnActivateAsync failed (proven by the integration test "a failing
                // activation hook fails the call and skips the deactivation hook"), so this is
                // defence in depth against a version which does, not a live path.
                env.Logger.LogWarning(
                    "Skipping the functional onDeactivate hook of grain type {GrainType} on {GrainId} (reason {Reason}): the activation failed before its state was initialized.",
                    env.Definition.GrainTypeName,
                    env.GrainContext.GrainId,
                    reason.ReasonCode
                )
            | Some hook ->
                let scope =
                    FunctionalStateScope(env.Definition.GrainTypeName, "onDeactivate", true)

                try
                    try
                        let core = FunctionalContextFactory.core env cancellationToken scope
                        do! hook.Invoke(env.Key, core, reason, env.State.Current)
                    with error ->
                        env.Logger.LogError(
                            error,
                            "Functional onDeactivate hook of grain type {GrainType} failed on {GrainId} (reason {Reason}): {Message}. The runtime performs no retry.",
                            env.Definition.GrainTypeName,
                            env.GrainContext.GrainId,
                            reason.ReasonCode,
                            error.Message
                        )

                        ExceptionDispatchInfo.Capture(error).Throw()
                finally
                    scope.Expire()
        }
        :> Task

/// <summary>
/// Implicit stream and broadcast-channel delivery into a functional activation.
/// </summary>
/// <remarks>
/// <para>
/// The seam is Orleans' own, and it is reached without any code generation. Orleans installs its
/// <c>StreamConsumerExtension</c> on an activation whose <b>grain instance</b> implements
/// <c>IStreamSubscriptionObserver</c> (<c>StreamConsumerGrainContextAction.Configure</c>, and the
/// keyed <c>IGrainExtension</c> factory in <c>StreamingServiceCollectionExtensions</c>, both do
/// exactly <c>GrainContext.GrainInstance as IStreamSubscriptionObserver</c>). When an item
/// arrives for a subscription the extension has no observer for — which is always the case for an
/// implicit subscription on a fresh activation — it calls <c>OnSubscribed</c> with a handle
/// factory, then re-checks its observer table and delivers the pending item. Attaching an
/// observer from inside <c>OnSubscribed</c> is therefore what turns "activated, then dropped on
/// the floor" into a delivery. Broadcast channels use the identical shape through
/// <c>IOnBroadcastChannelSubscribed</c> and <c>BroadcastChannelConsumerExtension</c>.
/// </para>
/// <para>
/// A delivery runs under the timer-hook rules: whole-state replacement published only on a
/// successful return, no storage call of the runtime's own, and <c>CancellationToken.None</c>
/// (neither <c>IAsyncObserver.OnNextAsync</c> nor <c>IBroadcastChannelSubscription.Attach</c>
/// supplies a token). Non-reentrancy is Orleans' doing rather than a setting of ours:
/// <c>IStreamConsumerExtension</c>'s delivery methods carry no <c>[AlwaysInterleave]</c>, so a
/// delivery takes an ordinary turn on the activation.
/// </para>
/// </remarks>
[<RequireQualifiedAccess>]
module internal FunctionalStreams =

    /// <summary>The state-scope operation name of one declared subscription.</summary>
    let private scopeName (binding: FunctionalStreamDeclaration) =
        $"{binding.OperationName}:{binding.ProviderName}/{binding.Namespace}"

    /// <summary>
    /// Run one delivered item through its declared hook. The replacement state is published
    /// exactly like a handler return, so a throwing hook leaves the activation's state untouched
    /// and the exception travels back to Orleans unaltered.
    /// </summary>
    let private deliver
        (env: FunctionalTargetEnvironment)
        (binding: FunctionalStreamDeclaration)
        (item: obj)
        (sequenceToken: StreamSequenceToken)
        : Task =
        task {
            let scope =
                FunctionalStateScope(env.Definition.GrainTypeName, scopeName binding, true)

            try
                let core =
                    FunctionalContextFactory.streamCore env CancellationToken.None scope sequenceToken

                let! next = binding.Adapter.Invoke(env.Key, core, env.State.Current, item)
                env.State.Publish next
            finally
                scope.Expire()
        }
        :> Task

    /// <summary>The delivery callback handed to a declaration's preclosed typed attach.</summary>
    let private deliveryOf (env: FunctionalTargetEnvironment) (binding: FunctionalStreamDeclaration) =
        FunctionalStreamDelivery(fun item sequenceToken -> deliver env binding item sequenceToken)

    /// <summary>
    /// One unmatched delivery. Orleans' implicit-subscription binding names a namespace but not a
    /// provider, so a namespace this definition declares for one provider is also routed here
    /// from any other provider running on the silo. Returning without attaching leaves Orleans to
    /// drop the item ("I don't have any subscriber for that stream"), which is the least
    /// destructive answer: throwing would poison a pulling agent over an item that a different,
    /// legitimately configured provider delivered.
    /// </summary>
    let private unmatched
        (env: FunctionalTargetEnvironment)
        (transport: string)
        (providerName: string)
        (itemNamespace: string)
        =
        env.Logger.LogWarning(
            "Grain {GrainId} of functional grain type {GrainType} received a {Transport} delivery on provider {ProviderName} and namespace {Namespace}, which it declares no hook for. The item is left undelivered.",
            env.GrainContext.GrainId,
            env.Definition.GrainTypeName,
            transport,
            providerName,
            itemNamespace
        )

        Task.CompletedTask

    /// <summary>
    /// <c>IStreamSubscriptionObserver.OnSubscribed</c>: attach the declared hook's typed observer
    /// to the subscription Orleans is delivering to.
    /// </summary>
    let onStreamSubscribed (env: FunctionalTargetEnvironment) (factory: IStreamSubscriptionHandleFactory) : Task =
        let itemNamespace = factory.StreamId.GetNamespace()

        match env.Definition.TryFindStreamBinding(true, factory.ProviderName, itemNamespace) with
        | Some binding ->
            match binding.Attachment with
            | StreamAttachment attach -> attach.Invoke(factory, deliveryOf env binding)
            | ChannelAttachment _ ->
                // Unreachable: TryFindStreamBinding already filtered on the stream transport.
                unmatched env "stream" factory.ProviderName itemNamespace
        | None -> unmatched env "stream" factory.ProviderName itemNamespace

    /// <summary>
    /// <c>IOnBroadcastChannelSubscribed.OnSubscribed</c>: attach the declared hook at the exact
    /// item type for the channel Orleans is publishing to.
    /// </summary>
    let onChannelSubscribed (env: FunctionalTargetEnvironment) (subscription: IBroadcastChannelSubscription) : Task =
        let itemNamespace = subscription.ChannelId.GetNamespace()

        match env.Definition.TryFindStreamBinding(false, subscription.ProviderName, itemNamespace) with
        | Some binding ->
            match binding.Attachment with
            | ChannelAttachment attach -> attach.Invoke(subscription, deliveryOf env binding)
            | StreamAttachment _ ->
                // Unreachable: TryFindStreamBinding already filtered on the channel transport.
                unmatched env "broadcast" subscription.ProviderName itemNamespace
        | None -> unmatched env "broadcast" subscription.ProviderName itemNamespace
