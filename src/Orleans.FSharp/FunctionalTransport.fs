namespace Orleans.FSharp

open System
open System.Buffers
open System.Collections.Concurrent
open System.Reflection
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans
open Orleans.Runtime
open Orleans.Serialization
open Orleans.Serialization.Serializers
open Orleans.Serialization.Session
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// The Orleans metadata of one contract's closed target interface. Constructed exactly once
/// per contract: the closed <c>IFunctionalGrainTarget&lt;'Actor&gt;</c> type and its
/// <c>DispatchAsync</c> method, never the open generic definition.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalTargetMetadata =
    {
        /// The closed actor-specific Orleans target interface.
        InterfaceType: Type
        /// The <c>DispatchAsync</c> method of that closed interface.
        DispatchMethod: MethodInfo
        /// The reserved functional interface ID of the contract's grain type.
        InterfaceId: string
        /// The Orleans interface type built from that ID.
        GrainInterfaceType: GrainInterfaceType
    }

/// <summary>Construction of the per-contract closed target metadata.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalTarget =

    /// <summary>
    /// Close <c>IFunctionalGrainTarget&lt;_&gt;</c> over the actor brand and resolve its
    /// dispatch method. Called once per contract, never per call.
    /// </summary>
    let metadataFor (actorBrand: Type) (grainTypeName: string) : FunctionalTargetMetadata =
        if actorBrand.ContainsGenericParameters then
            fail
                ContractStage
                $"the actor brand '{actorBrand.FullName}' is an open generic type; a closed actor brand is required."

        FunctionalInstrumentation.countGenericClosing ()
        let closed = typedefof<IFunctionalGrainTarget<_>>.MakeGenericType [| actorBrand |]

        let dispatch =
            closed.GetMethod FunctionalRequest.DispatchMethodName
            |> function
                | null ->
                    fail
                        ContractStage
                        $"the closed target interface '{closed.FullName}' does not expose '{FunctionalRequest.DispatchMethodName}'."
                | method -> method

        { InterfaceType = closed
          DispatchMethod = dispatch
          InterfaceId = FunctionalIds.interfaceId grainTypeName
          GrainInterfaceType = FunctionalIds.grainInterfaceType grainTypeName }

/// <summary>
/// The transport sender of one bound reference: it takes complete fixed request data and
/// returns the fixed reply.
/// </summary>
/// <remarks>
/// Phase 3 implements this with <c>FunctionalGrainReference</c> over the Orleans send path.
/// Phase 2 exercises the whole binding and call path through an in-memory implementation.
/// </remarks>
type internal IFunctionalRequestSender =

    /// <summary>Send an acknowledged request and await the fixed reply.</summary>
    abstract SendAsync:
        envelope: FunctionalRequestEnvelope * cancellationToken: CancellationToken -> Task<FunctionalReply>

    /// <summary>Send a one-way request; completion means the local send path accepted it.</summary>
    abstract SendOneWay: envelope: FunctionalRequestEnvelope -> unit

/// <summary>
/// The single seam between reference binding and the Orleans transport. Phase 3 replaces the
/// resolution fallback in <see cref="M:Orleans.FSharp.FunctionalTransportSource.resolve"/>
/// with <c>IGrainFactory.GetGrain</c> plus the <c>FunctionalGrainReference</c> type check;
/// everything above this interface stays unchanged.
/// </summary>
type internal IFunctionalTransportSource =

    /// <summary>The services of the client or activation which owns the factory.</summary>
    abstract Services: IServiceProvider

    /// <summary>Create the sender addressing one grain identity through one target interface.</summary>
    abstract CreateSender: grainId: GrainId * metadata: FunctionalTargetMetadata -> IFunctionalRequestSender

/// <summary>Resolution of the transport source from an application-supplied grain factory.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalTransportSource =

    /// <summary>The guidance every "no functional transport in this process" diagnostic ends with.</summary>
    [<Literal>]
    let Guidance =
        "Call AddFunctionalGrainClient() on the client builder (or AddFunctionalGrain() on the silo builder) before binding a functional reference."

    /// <summary>
    /// Resolve an explicitly supplied transport override. Production binding goes through
    /// <c>IGrainFactory.GetGrain</c> and the real <c>FunctionalGrainReference</c>; a factory
    /// which also implements this interface (the in-memory unit-test transport) short-circuits
    /// that path while exercising the same binding and call code.
    /// </summary>
    let tryResolve (factory: IGrainFactory) (grainTypeName: string) : IFunctionalTransportSource option =
        match box factory with
        | null ->
            fail
                BindingStage
                $"binding the contract '{grainTypeName}' requires a grain factory, but none was supplied."
        | :? IFunctionalTransportSource as source -> Some source
        | _ -> None

/// <summary>
/// Exact-type payload serialization. Arguments and replies cross the transport boundary as
/// fresh byte arrays produced by the Orleans serializer for the descriptor's exact CLR type,
/// which gives local and remote calls the same object-graph isolation.
/// </summary>
/// <remarks>
/// Every top-level serialization and deserialization rents a session from the Orleans pool and
/// returns it in <c>finally</c>. Sessions are never shared by concurrent calls.
/// </remarks>
[<Sealed>]
type internal FunctionalPayloadCodec(serializer: Serializer, sessionPool: SerializerSessionPool) =

    /// <summary>Serialize one value as its exact declared type into a fresh byte array.</summary>
    member _.Serialize<'T>(value: 'T) : byte[] =
        let session = sessionPool.GetSession()
        FunctionalInstrumentation.trackSessionRented session

        try
            let buffer = ArrayBufferWriter<byte>()
            serializer.Serialize<'T, ArrayBufferWriter<byte>>(value, buffer, session)
            FunctionalInstrumentation.countPayloadSerialization ()
            buffer.WrittenSpan.ToArray()
        finally
            FunctionalInstrumentation.trackSessionReturned session
            session.Dispose()

    /// <summary>Deserialize one value as its exact declared type.</summary>
    /// <remarks>
    /// When the F# binary codec owns the whole payload — which is when Orleans elides the field
    /// type and the codec has to recover the CLR type from a name in the bytes — the exact type
    /// asked for here is published for the duration of the read, so the wire name can only ever
    /// resolve to something assignable to it. When some other Orleans codec owns the top level,
    /// nothing is published: the F# codec may then be entered for a nested field whose type is
    /// not <c>'T</c>, and an expectation would be wrong rather than protective.
    /// </remarks>
    member _.Deserialize<'T>(payload: byte[]) : 'T =
        let session = sessionPool.GetSession()
        FunctionalInstrumentation.trackSessionRented session

        let expected =
            if FSharpBinaryFormat.isSupportedType typeof<'T> then
                typeof<'T>
            else
                null

        try
            let value =
                FSharpBinaryFormat.ExpectedPayloadType.Scoped(
                    expected,
                    fun () -> serializer.Deserialize<'T>(payload, session))

            FunctionalInstrumentation.countPayloadDeserialization ()
            value
        finally
            FunctionalInstrumentation.trackSessionReturned session
            session.Dispose()

    interface IFunctionalPayloadCodec with
        member this.Serialize<'T>(value: 'T) = this.Serialize<'T> value
        member this.Deserialize<'T>(payload: byte[]) : 'T = this.Deserialize<'T> payload

/// <summary>
/// Serializer preflight: the injected codec provider must resolve an Orleans codec for every
/// exact argument and reply type of a contract before a bound record is returned. Success is
/// cached per contract shape and serializer-service instance.
/// </summary>
[<RequireQualifiedAccess>]
module internal SerializerPreflight =

    let private validated =
        ConditionalWeakTable<obj, ConcurrentDictionary<Type, bool>>()

    /// <summary>Resolve the codec provider of a client or activation.</summary>
    let providerOf (services: IServiceProvider) (grainTypeName: string) : ICodecProvider =
        match services.GetService typeof<ICodecProvider> with
        | :? ICodecProvider as provider -> provider
        | _ ->
            fail
                BindingStage
                $"binding the contract '{grainTypeName}' requires the Orleans serializer, but no ICodecProvider is registered in this process."

    /// <summary>
    /// Resolve the Orleans codec of one declared type and declare it as a top-level payload
    /// type. <paramref name="role"/> and <paramref name="owner"/> only shape the diagnostics.
    /// </summary>
    /// <remarks>
    /// The two failures are kept apart deliberately. A missing codec means the serializer
    /// cannot resolve the type; a rejected declaration means two distinct CLR types share one
    /// <c>FullName</c>, which is a name-collision fault and not a serializer-resolution one.
    /// Reporting the collision as "resolving the Orleans serializer failed" would hide the only
    /// message that says what is actually wrong, so it is rethrown under its own diagnostic
    /// with the original message preserved verbatim.
    /// </remarks>
    let internal checkType (provider: ICodecProvider) (grainTypeName: string) (role: string) (owner: string) (declaredType: Type) =
        let codecs = provider :> IFieldCodecProvider

        try
            codecs.GetCodec declaredType |> ignore
        with
        | :? CodecNotFoundException as cause ->
            failCause
                BindingStage
                $"the {role} type '{declaredType.FullName}' of {owner} on grain type '{grainTypeName}' has no registered Orleans serializer. Register a codec for it (for example through AddFSharpSerialization/the F# binary codec) on every process which sends or hosts this contract."
                cause
        | cause ->
            failCause
                BindingStage
                $"resolving the Orleans serializer for the {role} type '{declaredType.FullName}' of {owner} on grain type '{grainTypeName}' failed."
                cause

        // Exact-type payload serialization makes Orleans elide the field type, so the F# binary
        // codec has to resolve a top-level payload type by name. Declaring it here keeps that
        // resolution working for application assemblies without widening the codec's assembly
        // allow-list.
        try
            FSharpBinaryFormat.declareType declaredType
        with cause ->
            failCause
                BindingStage
                $"the {role} type '{declaredType.FullName}' of {owner} on grain type '{grainTypeName}' cannot be declared as a top-level payload type: {cause.Message}"
                cause

    /// <summary>
    /// Validate that every declared argument and reply type has a codec, caching the outcome
    /// for this API shape and this serializer-service instance.
    /// </summary>
    let ensure
        (provider: ICodecProvider)
        (grainTypeName: string)
        (apiType: Type)
        (declared: (string * Type * Type)[])
        =
        let perProvider =
            validated.GetValue(provider, fun _ -> ConcurrentDictionary<Type, bool>())

        if not (perProvider.ContainsKey apiType) then
            for operationId, argumentType, replyType in declared do
                checkType provider grainTypeName "argument" $"operation '{operationId}'" argumentType
                checkType provider grainTypeName "reply" $"operation '{operationId}'" replyType

            perProvider.[apiType] <- true

    /// <summary>
    /// Validate the durable stored type of every attached persistent state of one hosted
    /// definition. Silo startup runs this so a state type without a serializer fails startup
    /// instead of failing the first storage write.
    /// </summary>
    let ensureStoredTypes (provider: ICodecProvider) (grainTypeName: string) (states: (string * Type)[]) =
        for stateName, storedType in states do
            checkType provider grainTypeName "stored state" $"persistent state '{stateName}'" storedType

/// <summary>
/// The preclosed pair of client closures of one bound operation: the bound API-record field
/// and its cancellable form. Both are boxed values of the field's exact function type.
/// </summary>
[<ReferenceEquality>]
type internal BoundCall =
    {
        /// <c>'Argument -&gt; Task&lt;'Reply&gt;</c>, the value installed in the API record.
        Field: obj
        /// <c>'Argument -&gt; CancellationToken -&gt; Task&lt;'Reply&gt;</c>.
        Cancellable: obj
    }

/// <summary>
/// Everything one bound operation needs at call time: the sender, the payload codec, the
/// immutable envelope metadata, the precomputed protocol tokens, and the local payload limit.
/// One instance is created per bound operation while binding a reference.
/// </summary>
[<Sealed>]
type internal FunctionalCallSite
    (
        sender: IFunctionalRequestSender,
        codec: FunctionalPayloadCodec,
        grainType: string,
        contractVersion: int,
        operationId: string,
        requestToken: byte[],
        replyToken: byte[],
        admissionFlags: byte,
        maxPayloadBytes: int
    ) =

    let isOneWay = admissionFlags &&& AdmissionFlags.OneWay <> AdmissionFlags.None

    /// <summary>
    /// Validate the fixed reply shape, its protocol token, and the local reply-size limit
    /// before any user payload is deserialized.
    /// </summary>
    member private _.ValidateReply(reply: FunctionalReply) =
        if obj.ReferenceEquals(reply, null) then
            fail
                TransportStage
                $"operation '{operationId}' on grain type '{grainType}' returned no reply."

        if isNull reply.ProtocolToken || reply.ProtocolToken.Length <> ProtocolToken.Length then
            fail
                TransportStage
                $"the reply to operation '{operationId}' on grain type '{grainType}' carries a protocol token of {(if isNull reply.ProtocolToken then 0 else reply.ProtocolToken.Length)} bytes; exactly {ProtocolToken.Length} bytes are required."

        if not (ProtocolToken.equal reply.ProtocolToken replyToken) then
            fail
                TransportStage
                $"the reply to operation '{operationId}' on grain type '{grainType}' carries protocol token {ProtocolToken.toHex reply.ProtocolToken}, but {ProtocolToken.toHex replyToken} was expected."

        if isNull reply.Payload then
            fail
                TransportStage
                $"the reply to operation '{operationId}' on grain type '{grainType}' carries no payload."

        PayloadLimit.ensure CallerReplyReceive grainType operationId reply.Payload.Length maxPayloadBytes

    /// <summary>
    /// Serialize the exact argument, construct and send the fixed request, then validate and
    /// deserialize the exact reply.
    /// </summary>
    member this.Invoke<'Argument, 'Reply>(argument: 'Argument, cancellationToken: CancellationToken) : Task<'Reply> =
        if isOneWay && cancellationToken.IsCancellationRequested then
            // A one-way call has no remote cancellation: an already-cancelled token is the only
            // observable effect the caller can have.
            Task.FromCanceled<'Reply> cancellationToken
        else
            task {
                let payload = codec.Serialize<'Argument> argument
                PayloadLimit.ensure CallerRequestSend grainType operationId payload.Length maxPayloadBytes

                let envelope =
                    FunctionalRequestEnvelope(
                        grainType,
                        contractVersion,
                        operationId,
                        requestToken,
                        admissionFlags,
                        payload
                    )

                if isOneWay then
                    sender.SendOneWay envelope
                    return Unchecked.defaultof<'Reply>
                else
                    let! reply = sender.SendAsync(envelope, cancellationToken)
                    this.ValidateReply reply
                    return codec.Deserialize<'Reply> reply.Payload
            }

/// <summary>
/// The typed client-closure factory. Its generic method is closed once per operation
/// descriptor while the contract is sealed, so binding and calling never close a generic.
/// </summary>
[<AbstractClass; Sealed>]
type internal BoundCallFactory =

    /// <summary>Create both closures of one bound operation over its call site.</summary>
    static member Create<'Argument, 'Reply>(site: FunctionalCallSite) : BoundCall =
        { Field = box (fun (argument: 'Argument) -> site.Invoke<'Argument, 'Reply>(argument, CancellationToken.None))
          Cancellable =
            box (fun (argument: 'Argument) (cancellationToken: CancellationToken) ->
                site.Invoke<'Argument, 'Reply>(argument, cancellationToken)) }

/// <summary>Preclosing of the typed client-closure factory.</summary>
[<RequireQualifiedAccess>]
module internal BoundClosure =

    let private createMethod =
        match
            typeof<BoundCallFactory>
                .GetMethod("Create", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        with
        | null -> fail ContractStage "the typed client-closure factory 'BoundCallFactory.Create' was not found."
        | method -> method

    /// <summary>
    /// Close the client-closure factory over one operation's exact argument and reply types.
    /// Called once per descriptor while the contract is sealed.
    /// </summary>
    let precompute (argumentType: Type) (replyType: Type) : Func<FunctionalCallSite, BoundCall> =
        FunctionalInstrumentation.countGenericClosing ()

        let closed = createMethod.MakeGenericMethod [| argumentType; replyType |]

        closed.CreateDelegate typeof<Func<FunctionalCallSite, BoundCall>> :?> Func<FunctionalCallSite, BoundCall>

/// <summary>Client-side transport configuration read while binding a reference.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalTransportConfiguration =

    /// <summary>
    /// The local payload limit of this process. An unconfigured process uses the documented
    /// 16 MiB default; a configured non-positive value is a configuration error.
    /// </summary>
    let maxPayloadBytes (services: IServiceProvider) =
        match services.GetService typeof<IOptions<FunctionalGrainTransportOptions>> with
        | :? IOptions<FunctionalGrainTransportOptions> as options ->
            PayloadLimit.validateLimit options.Value.MaxPayloadBytes
        | _ -> FunctionalGrainTransportOptions.DefaultMaxPayloadBytes

    /// <summary>The exact-type payload codec of this process.</summary>
    let payloadCodec (services: IServiceProvider) (grainTypeName: string) =
        match services.GetService typeof<Serializer> with
        | :? Serializer as serializer -> FunctionalPayloadCodec(serializer, serializer.SessionPool)
        | _ ->
            fail
                BindingStage
                $"binding the contract '{grainTypeName}' requires the Orleans serializer, but no Serializer is registered in this process."
