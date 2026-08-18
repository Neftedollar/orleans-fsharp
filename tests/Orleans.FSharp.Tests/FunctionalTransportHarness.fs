/// <summary>
/// In-memory transport harness for spec 003 Phase 2. It exercises the production binding and
/// call path — key encoding, serializer preflight, preclosed closures, exact-type payload
/// bytes, protocol tokens, and payload limits — without a live Orleans cluster, by
/// implementing the internal transport seam that Phase 3 replaces with the real
/// <c>FunctionalGrainReference</c>.
/// </summary>
module internal Orleans.FSharp.Tests.FunctionalTransportHarness

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans
open Orleans.Runtime
open Orleans.Serialization
open Orleans.FSharp

// ──────────────────────────────────────────────────────────────────────────────
// Serializer wiring
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Build a service provider carrying the Orleans serializer with the explicit fixed-transport
/// codecs, optionally the F# binary codec (so ordinary F# argument and reply types resolve),
/// and optionally a non-default transport option value.
/// </summary>
let buildServices (withFSharpCodec: bool) (maxPayloadBytes: int option) : ServiceProvider =
    let services = ServiceCollection()

    ServiceCollectionExtensions.AddSerializer(
        services,
        Action<ISerializerBuilder>(fun builder ->
            FunctionalTransportSerialization.AddFunctionalTransport builder |> ignore

            if withFSharpCodec then
                FSharpBinaryCodecRegistration.addToSerializerBuilder builder |> ignore)
    )
    |> ignore

    match maxPayloadBytes with
    | Some limit ->
        services.Configure<FunctionalGrainTransportOptions>(fun (options: FunctionalGrainTransportOptions) ->
            options.MaxPayloadBytes <- limit)
        |> ignore
    | None -> ()

    services.BuildServiceProvider()

/// <summary>
/// The exact-type payload codec of a harness service provider, carrying whatever
/// <c>FunctionalGrainTransportOptions.MaxPayloadBytes</c> <c>buildServices</c> configured (or the
/// 16 MiB default when it did not) — exactly the production wiring, so a codec built here can
/// exercise the caller-side notify payload boundary the way the real registered singleton does.
/// </summary>
/// <remarks>
/// Deliberately reads the RAW configured value rather than going through
/// <c>FunctionalTransportConfiguration.maxPayloadBytes</c>, which validates and throws on a
/// non-positive value: that validation belongs to the one place production performs it (binding
/// a reference / creating an observer), and <c>``a non-positive configured payload limit fails
/// binding``</c> depends on nothing upstream of that call raising it first.
/// </remarks>
let payloadCodec (services: IServiceProvider) =
    let serializer = services.GetRequiredService<Serializer>()

    let maxPayloadBytes =
        match services.GetService typeof<IOptions<FunctionalGrainTransportOptions>> with
        | :? IOptions<FunctionalGrainTransportOptions> as options -> options.Value.MaxPayloadBytes
        | _ -> FunctionalGrainTransportOptions.DefaultMaxPayloadBytes

    FunctionalPayloadCodec(serializer, serializer.SessionPool, maxPayloadBytes)

// ──────────────────────────────────────────────────────────────────────────────
// In-memory transport
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Every non-functional grain-factory member of the harness factories fails loudly.</summary>
let private notSupported<'T> () : 'T =
    raise (NotSupportedException "The in-memory functional transport supports functional binding only.")

/// <summary>One request observed by the in-memory transport.</summary>
type RecordedCall =
    { GrainId: GrainId
      Metadata: FunctionalTargetMetadata
      Envelope: FunctionalRequestEnvelope
      IsOneWay: bool }

/// <summary>
/// A grain factory whose functional transport is in-memory. Every request is recorded and then
/// answered by the supplied dispatch function.
/// </summary>
type InMemoryTransport
    (
        services: IServiceProvider,
        dispatch: GrainId -> FunctionalRequestEnvelope -> Task<FunctionalReply>,
        streamDispatch: (GrainId -> FunctionalRequestEnvelope -> IAsyncEnumerable<FunctionalReply>) option
    ) =

    let recorded = ConcurrentQueue<RecordedCall>()

    /// <summary>The unary-only harness, for the tests that never open a stream.</summary>
    new(services: IServiceProvider, dispatch: GrainId -> FunctionalRequestEnvelope -> Task<FunctionalReply>) =
        InMemoryTransport(services, dispatch, None)

    /// <summary>Every request observed so far, in order.</summary>
    member _.Calls = recorded.ToArray()

    /// <summary>The most recent request, or a failure when nothing was sent.</summary>
    member this.LastCall =
        match this.Calls with
        | [||] -> failwith "no functional request was sent through the in-memory transport."
        | calls -> calls.[calls.Length - 1]

    /// <summary>Forget every recorded request.</summary>
    member _.Clear() = recorded.Clear()

    interface IFunctionalTransportSource with
        member _.Services = services

        member _.CreateSender(grainId, metadata) =
            { new IFunctionalRequestSender with
                member _.SendAsync(envelope, cancellationToken) =
                    recorded.Enqueue
                        { GrainId = grainId
                          Metadata = metadata
                          Envelope = envelope
                          IsOneWay = false }

                    cancellationToken.ThrowIfCancellationRequested()
                    dispatch grainId envelope

                // The in-memory transport has no Orleans transaction machinery behind it, so a
                // transactional send is recorded and dispatched exactly like an ordinary one. The
                // unit tests that use this harness assert on the recorded envelope (the admission
                // byte carries the transaction option), never on commit behaviour, which only the
                // integration cluster can prove.
                member _.SendTransactionalAsync(envelope, cancellationToken) =
                    recorded.Enqueue
                        { GrainId = grainId
                          Metadata = metadata
                          Envelope = envelope
                          IsOneWay = false }

                    cancellationToken.ThrowIfCancellationRequested()
                    dispatch grainId envelope

                member _.SendOneWay envelope =
                    recorded.Enqueue
                        { GrainId = grainId
                          Metadata = metadata
                          Envelope = envelope
                          IsOneWay = true }

                    dispatch grainId envelope |> ignore

                // Spec 004 item 6. The harness has no Orleans enumerator extension behind it, so a
                // stream is whatever IAsyncEnumerable the test supplied; what it does exercise is
                // the whole caller-side path — argument serialization, the payload limit at send,
                // the envelope with the stream-request token, and the per-item validation and
                // exact-type deserialization on the way back.
                member _.OpenStream(envelope, _cancellationToken) =
                    recorded.Enqueue
                        { GrainId = grainId
                          Metadata = metadata
                          Envelope = envelope
                          IsOneWay = false }

                    match streamDispatch with
                    | Some open' -> open' grainId envelope
                    | None ->
                        raise (
                            NotSupportedException
                                "this in-memory functional transport was built without a streaming dispatch."
                        ) }

    interface IGrainFactory with
        member _.GetGrain<'T when 'T :> IGrainWithGuidKey>(_primaryKey: Guid, _prefix: string) : 'T = notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithIntegerKey>(_primaryKey: int64, _prefix: string) : 'T =
            notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithStringKey>(_primaryKey: string, _prefix: string) : 'T =
            notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithGuidCompoundKey>
            (_primaryKey: Guid, _keyExtension: string, _prefix: string)
            : 'T =
            notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithIntegerCompoundKey>
            (_primaryKey: int64, _keyExtension: string, _prefix: string)
            : 'T =
            notSupported ()

        member _.CreateObjectReference<'T when 'T :> IGrainObserver>(_obj: IGrainObserver) : 'T = notSupported ()
        member _.DeleteObjectReference<'T when 'T :> IGrainObserver>(_obj: IGrainObserver) : unit = notSupported ()
        member _.GetGrain(_interfaceType: Type, _primaryKey: Guid) : IGrain = notSupported ()
        member _.GetGrain(_interfaceType: Type, _primaryKey: int64) : IGrain = notSupported ()
        member _.GetGrain(_interfaceType: Type, _primaryKey: string) : IGrain = notSupported ()

        member _.GetGrain(_interfaceType: Type, _primaryKey: Guid, _keyExtension: string) : IGrain = notSupported ()

        member _.GetGrain(_interfaceType: Type, _primaryKey: int64, _keyExtension: string) : IGrain = notSupported ()

        member _.GetGrain<'T when 'T :> IAddressable>(_grainId: GrainId) : 'T = notSupported ()
        member _.GetGrain(_grainId: GrainId) : IAddressable = notSupported ()

        member _.GetGrain(_grainId: GrainId, _interfaceType: GrainInterfaceType) : IAddressable = notSupported ()

        member _.GetGrain(_interfaceType: Type, _grainKey: IdSpan, _grainClassNamePrefix: string) : IAddressable =
            notSupported ()

        member _.GetGrain(_interfaceType: Type, _grainKey: IdSpan) : IAddressable = notSupported ()

/// <summary>
/// A grain factory with no functional transport at all: binding through it must fail with the
/// configuration diagnostic.
/// </summary>
type UnconfiguredFactory() =

    interface IGrainFactory with
        member _.GetGrain<'T when 'T :> IGrainWithGuidKey>(_primaryKey: Guid, _prefix: string) : 'T = notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithIntegerKey>(_primaryKey: int64, _prefix: string) : 'T =
            notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithStringKey>(_primaryKey: string, _prefix: string) : 'T =
            notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithGuidCompoundKey>
            (_primaryKey: Guid, _keyExtension: string, _prefix: string)
            : 'T =
            notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithIntegerCompoundKey>
            (_primaryKey: int64, _keyExtension: string, _prefix: string)
            : 'T =
            notSupported ()

        member _.CreateObjectReference<'T when 'T :> IGrainObserver>(_obj: IGrainObserver) : 'T = notSupported ()
        member _.DeleteObjectReference<'T when 'T :> IGrainObserver>(_obj: IGrainObserver) : unit = notSupported ()
        member _.GetGrain(_interfaceType: Type, _primaryKey: Guid) : IGrain = notSupported ()
        member _.GetGrain(_interfaceType: Type, _primaryKey: int64) : IGrain = notSupported ()
        member _.GetGrain(_interfaceType: Type, _primaryKey: string) : IGrain = notSupported ()

        member _.GetGrain(_interfaceType: Type, _primaryKey: Guid, _keyExtension: string) : IGrain = notSupported ()

        member _.GetGrain(_interfaceType: Type, _primaryKey: int64, _keyExtension: string) : IGrain = notSupported ()

        member _.GetGrain<'T when 'T :> IAddressable>(_grainId: GrainId) : 'T = notSupported ()
        member _.GetGrain(_grainId: GrainId) : IAddressable = notSupported ()

        member _.GetGrain(_grainId: GrainId, _interfaceType: GrainInterfaceType) : IAddressable = notSupported ()

        member _.GetGrain(_interfaceType: Type, _grainKey: IdSpan, _grainClassNamePrefix: string) : IAddressable =
            notSupported ()

        member _.GetGrain(_interfaceType: Type, _grainKey: IdSpan) : IAddressable = notSupported ()

/// <summary>
/// A grain factory that counts <c>CreateObjectReference</c> calls instead of performing them, so
/// a test can prove a code path did or did not reach it at all -- the shape task 17 uses to show
/// that a rejected observer type creates no Orleans object reference: register this as the
/// process's <c>IGrainFactory</c>, drive the code path under test, then assert
/// <c>CreatedCount = 0</c>. Every other member still fails loudly, exactly like
/// <c>UnconfiguredFactory</c>, so a path that reaches anything else this factory does not model
/// fails the test just as clearly as a leaked reference would.
/// </summary>
type CountingObserverFactory() =
    let mutable created = 0

    /// <summary>How many times <c>CreateObjectReference</c> has been called.</summary>
    member _.CreatedCount = created

    interface IGrainFactory with
        member _.GetGrain<'T when 'T :> IGrainWithGuidKey>(_primaryKey: Guid, _prefix: string) : 'T = notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithIntegerKey>(_primaryKey: int64, _prefix: string) : 'T =
            notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithStringKey>(_primaryKey: string, _prefix: string) : 'T =
            notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithGuidCompoundKey>
            (_primaryKey: Guid, _keyExtension: string, _prefix: string)
            : 'T =
            notSupported ()

        member _.GetGrain<'T when 'T :> IGrainWithIntegerCompoundKey>
            (_primaryKey: int64, _keyExtension: string, _prefix: string)
            : 'T =
            notSupported ()

        member _.CreateObjectReference<'T when 'T :> IGrainObserver>(_obj: IGrainObserver) : 'T =
            created <- created + 1
            notSupported ()

        member _.DeleteObjectReference<'T when 'T :> IGrainObserver>(_obj: IGrainObserver) : unit = notSupported ()
        member _.GetGrain(_interfaceType: Type, _primaryKey: Guid) : IGrain = notSupported ()
        member _.GetGrain(_interfaceType: Type, _primaryKey: int64) : IGrain = notSupported ()
        member _.GetGrain(_interfaceType: Type, _primaryKey: string) : IGrain = notSupported ()

        member _.GetGrain(_interfaceType: Type, _primaryKey: Guid, _keyExtension: string) : IGrain = notSupported ()

        member _.GetGrain(_interfaceType: Type, _primaryKey: int64, _keyExtension: string) : IGrain = notSupported ()

        member _.GetGrain<'T when 'T :> IAddressable>(_grainId: GrainId) : 'T = notSupported ()
        member _.GetGrain(_grainId: GrainId) : IAddressable = notSupported ()

        member _.GetGrain(_grainId: GrainId, _interfaceType: GrainInterfaceType) : IAddressable = notSupported ()

        member _.GetGrain(_interfaceType: Type, _grainKey: IdSpan, _grainClassNamePrefix: string) : IAddressable =
            notSupported ()

        member _.GetGrain(_interfaceType: Type, _grainKey: IdSpan) : IAddressable = notSupported ()

// ──────────────────────────────────────────────────────────────────────────────
// In-memory target
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The in-memory stand-in for the silo dispatch path: it revalidates the fixed envelope the
/// way the target will, runs a typed handler over the exact argument type, and returns the
/// descriptor's reply token with a fresh payload.
/// </summary>
type InMemoryTarget(services: IServiceProvider, grainType: string, contractVersion: int) =

    let codec = payloadCodec services
    let handlers = ConcurrentDictionary<string, FunctionalRequestEnvelope -> byte[]>(StringComparer.Ordinal)
    let mutable replyLimit = FunctionalGrainTransportOptions.DefaultMaxPayloadBytes
    let mutable requestLimit = FunctionalGrainTransportOptions.DefaultMaxPayloadBytes

    /// <summary>The payload codec this target serializes replies with.</summary>
    member _.Codec = codec

    /// <summary>The silo-side request limit this target enforces.</summary>
    member _.RequestLimit
        with get () = requestLimit
        and set value = requestLimit <- value

    /// <summary>The silo-side reply limit this target enforces.</summary>
    member _.ReplyLimit
        with get () = replyLimit
        and set value = replyLimit <- value

    /// <summary>Install a typed handler for one operation.</summary>
    member _.Handle<'Argument, 'Reply>(operationId: string, handler: 'Argument -> 'Reply) =
        handlers.[operationId] <-
            fun envelope ->
                let argument = codec.Deserialize<'Argument> envelope.Payload
                codec.Serialize<'Reply>(handler argument)

    /// <summary>Install a raw byte handler for one operation.</summary>
    member _.HandleRaw(operationId: string, handler: FunctionalRequestEnvelope -> byte[]) =
        handlers.[operationId] <- handler

    /// <summary>Dispatch one request through the fixed validation order.</summary>
    member _.Dispatch (_grainId: GrainId) (envelope: FunctionalRequestEnvelope) : Task<FunctionalReply> =
        task {
            // 1. Fixed envelope shape, grain type, version, payload size, token, flags.
            if envelope.GrainType <> grainType then
                failwith $"the in-memory target hosts '{grainType}' but received '{envelope.GrainType}'."

            if envelope.ContractVersion <> contractVersion then
                failwith
                    $"the in-memory target hosts version {contractVersion} but received {envelope.ContractVersion}."

            PayloadLimit.ensure
                PayloadBoundary.SiloRequestReceive
                grainType
                envelope.OperationId
                envelope.Payload.Length
                requestLimit

            if AdmissionFlags.hasReserved envelope.AdmissionFlags then
                failwith "the in-memory target received reserved admission flags."

            // 2-3. Resolve the descriptor and compare the request token.
            let expected = ProtocolToken.request grainType contractVersion envelope.OperationId

            if not (ProtocolToken.equal envelope.ProtocolToken expected) then
                failwith $"the in-memory target received an unexpected request token for '{envelope.OperationId}'."

            match handlers.TryGetValue envelope.OperationId with
            | false, _ -> return failwith $"the in-memory target hosts no operation '{envelope.OperationId}'."
            | true, handler ->
                // 4-7. Typed handler, exact reply serialization, silo reply limit.
                let payload = handler envelope

                PayloadLimit.ensure
                    PayloadBoundary.SiloReplySend
                    grainType
                    envelope.OperationId
                    payload.Length
                    replyLimit

                return FunctionalReply(ProtocolToken.reply grainType contractVersion envelope.OperationId, payload)
        }
