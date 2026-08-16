namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// The activation-local holder of the primary state. Phase 3 hosts ephemeral definitions only:
/// the state factory runs once per activation and a successful sequential handler return
/// replaces the value here. Phase 4 replaces this holder with the selected
/// <c>IPersistentState&lt;'State&gt;</c> when <c>stateFrom</c> is configured.
/// </summary>
[<Sealed>]
type internal FunctionalActivationState(initial: obj) =

    let mutable current = initial

    /// <summary>The current primary state, boxed.</summary>
    member _.Current = current

    /// <summary>Publish a replacement primary state. Never writes storage.</summary>
    member _.Publish(value: obj) = current <- value

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
        /// The activation-local primary state holder.
        State: FunctionalActivationState
        /// Wrapper for the protected Orleans deactivate-on-idle method.
        DeactivateOnIdle: unit -> unit
        /// Wrapper for the protected Orleans delay-deactivation method.
        DelayDeactivation: TimeSpan -> unit
    }

/// <summary>
/// Target-side dispatch of one fixed request, in the specification's exact validation order.
/// Protocol validation raises transport-stage diagnostics before any application code runs, so
/// a protocol failure is always distinguishable from an application handler exception, which
/// travels the ordinary Orleans response-exception path.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalDispatch =

    /// <summary>The per-invocation context of one acknowledged or delivered one-way request.</summary>
    let private contextCore (env: FunctionalTargetEnvironment) (cancellationToken: CancellationToken) =
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
                notAvailable
                    "Phase 4"
                    $"the persistent state '{descriptor.StateName}' of grain type '{env.Definition.GrainTypeName}'" }

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
        let invocation =
            { Key = env.Key
              Core = contextCore env cancellationToken
              State = env.State.Current
              Payload = envelope.Payload
              Codec = env.Codec }

        let work =
            task {
                let! nextState, payload = operation.Adapter.Invoke invocation

                // Read-only and state-neutral interleaved operations discard their replacement.
                if not (operation.IsReadOnly || operation.IsAlwaysInterleave) then
                    env.State.Publish nextState

                // 7. The silo reply limit, then the descriptor's reply token and fresh payload.
                PayloadLimit.ensure
                    SiloReplySend
                    grainTypeName
                    operation.OperationId
                    payload.Length
                    env.MaxPayloadBytes

                return FunctionalReply(operation.ReplyToken, payload)
            }

        ValueTask<FunctionalReply> work
