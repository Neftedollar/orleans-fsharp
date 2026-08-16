namespace Orleans.FSharp

open System
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
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
          CancellationToken = cancellationToken
          DeactivateOnIdle = env.DeactivateOnIdle
          DelayDeactivation = env.DelayDeactivation
          ResolvePersistentState =
            fun descriptor ->
                match env.State.TryResolve descriptor with
                | Some facet -> facet.Blueprint.Facade facet.Instance scope
                | None -> null }

/// <summary>
/// Target-side dispatch of one fixed request, in the specification's exact validation order.
/// Protocol validation raises transport-stage diagnostics before any application code runs, so
/// a protocol failure is always distinguishable from an application handler exception, which
/// travels the ordinary Orleans response-exception path.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalDispatch =

    /// <summary>Dispatch one request on this activation.</summary>
    let dispatch
        (env: FunctionalTargetEnvironment)
        (envelope: FunctionalRequestEnvelope)
        (cancellationToken: CancellationToken)
        : ValueTask<FunctionalReply> =
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

        if envelope.ContractVersion <> definition.Version then
            fail
                TransportStage
                $"grain type '{grainTypeName}' hosts contract version {definition.Version} but received version {envelope.ContractVersion}."

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

        // 3. Compare the exact protocol token and the admission flags with the descriptor.
        if not (ProtocolToken.equal envelope.ProtocolToken operation.RequestToken) then
            fail
                TransportStage
                $"the request for operation '{operation.OperationId}' on grain type '{grainTypeName}' carries protocol token {ProtocolToken.toHex envelope.ProtocolToken}, but {ProtocolToken.toHex operation.RequestToken} was expected."

        if envelope.AdmissionFlags <> operation.AdmissionFlags then
            fail
                TransportStage
                $"the request for operation '{operation.OperationId}' on grain type '{grainTypeName}' carries admission flags 0x{envelope.AdmissionFlags:x2}, but the hosted descriptor declares 0x{operation.AdmissionFlags:x2}."

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
        let stateNeutral = operation.IsReadOnly || operation.IsAlwaysInterleave

        let scope =
            FunctionalStateScope(grainTypeName, operation.OperationId, not stateNeutral)

        let invocation =
            { Key = env.Key
              Core = FunctionalContextFactory.core env invocationToken scope
              State = env.State.Current
              Payload = envelope.Payload
              Codec = env.Codec }

        let work =
            task {
                try
                    let! nextState, payload =
                        task {
                            try
                                return! operation.Adapter.Invoke invocation
                            finally
                                scope.Expire()
                        }

                    // Read-only and state-neutral interleaved operations discard their replacement.
                    if not stateNeutral then
                        env.State.Publish nextState

                    // 7. The silo reply limit, then the descriptor's reply token and fresh payload.
                    PayloadLimit.ensure
                        SiloReplySend
                        grainTypeName
                        operation.OperationId
                        payload.Length
                        env.MaxPayloadBytes

                    return FunctionalReply(operation.ReplyToken, payload)
                with error when operation.IsOneWay ->
                    // A one-way target failure is never returned to that caller: the caller's
                    // send completed at the local acknowledgement. It is recorded here so the
                    // failure is not silent, then rethrown onto the ordinary Orleans path so
                    // tracing and the runtime's own one-way logging still observe it.
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
            }

        ValueTask<FunctionalReply> work

/// <summary>
/// The functional half of the Orleans activation lifecycle. It runs between the stock
/// <c>SetupState</c> load and the completion of activation, and again during deactivation,
/// and it never issues a storage call of its own.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalLifecycle =

    /// <summary>
    /// Steps 3 and 4 of the activation order: initialize the ephemeral primary state and every
    /// attached holder which reports no durable record, then run the functional
    /// <c>onActivate</c> hook, whose replacement is published in memory only. A storage read,
    /// an initializer, or the hook failing all fail the activation.
    /// </summary>
    let activate (env: FunctionalTargetEnvironment) (cancellationToken: CancellationToken) : Task =
        task {
            env.State.Initialize env.Key

            match env.Definition.OnActivate with
            | None -> ()
            | Some hook ->
                let scope =
                    FunctionalStateScope(env.Definition.GrainTypeName, "onActivate", true)

                try
                    let core = FunctionalContextFactory.core env cancellationToken scope
                    let! next = hook.Invoke(env.Key, core, env.State.Current)
                    env.State.Publish next
                finally
                    scope.Expire()
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
            match env.Definition.OnDeactivate with
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
        }
        :> Task
