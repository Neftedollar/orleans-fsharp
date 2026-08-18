namespace Orleans.FSharp

open System
open System.Buffers
open System.Collections.Concurrent
open System.Collections.Generic
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
    /// <param name="actorBrand">The closed actor brand type to close the target interface over.</param>
    /// <param name="grainTypeName">The contract's grain type name.</param>
    /// <exception cref="System.InvalidOperationException">
    /// <paramref name="actorBrand"/> is an open generic type, or the closed target interface does
    /// not expose the dispatch method.
    /// </exception>
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
    /// <param name="envelope">The fixed request envelope to send.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    abstract SendAsync:
        envelope: FunctionalRequestEnvelope * cancellationToken: CancellationToken -> Task<FunctionalReply>

    /// <summary>
    /// Send an acknowledged request under Orleans' transactional invokable base and await the
    /// fixed reply. Separate from <see cref="SendAsync"/> because the transaction machinery lives
    /// in the invokable's base class, so which base class the request uses is decided here, at
    /// send time, from the operation's declared transaction option.
    /// </summary>
    /// <param name="envelope">The fixed request envelope to send.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    abstract SendTransactionalAsync:
        envelope: FunctionalRequestEnvelope * cancellationToken: CancellationToken -> Task<FunctionalReply>

    /// <summary>Send a one-way request; completion means the local send path accepted it.</summary>
    /// <param name="envelope">The fixed request envelope to send.</param>
    abstract SendOneWay: envelope: FunctionalRequestEnvelope -> unit

    /// <summary>
    /// Open a server-streaming operation and return the sequence of fixed item replies. Spec 004
    /// item 6.
    /// </summary>
    /// <remarks>
    /// Nothing is sent yet: the returned value is Orleans' own <c>AsyncEnumerableRequest</c>, and
    /// the first message leaves only when a consumer calls <c>GetAsyncEnumerator</c> and pulls.
    /// Enumerating it twice runs two independent remote enumerations, which is Orleans' semantics
    /// and is preserved unchanged.
    /// </remarks>
    /// <param name="envelope">The fixed request envelope to send.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    abstract OpenStream:
        envelope: FunctionalRequestEnvelope * cancellationToken: CancellationToken ->
            IAsyncEnumerable<FunctionalReply>

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
    /// <param name="grainId">The grain identity to address.</param>
    /// <param name="metadata">The contract's closed target interface metadata.</param>
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
    /// <param name="factory">The application-supplied grain factory to inspect.</param>
    /// <param name="grainTypeName">The contract's grain type name.</param>
    /// <exception cref="System.InvalidOperationException"><paramref name="factory"/> is null.</exception>
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
type internal FunctionalPayloadCodec
    (serializer: Serializer, sessionPool: SerializerSessionPool, ?maxPayloadBytes: int) =

    let maxPayloadBytes =
        defaultArg maxPayloadBytes FunctionalGrainTransportOptions.DefaultMaxPayloadBytes

    /// <summary>The local payload-size limit this codec instance was built with.</summary>
    member _.MaxPayloadBytes = maxPayloadBytes

    /// <summary>Serialize one value as its exact declared type into a fresh byte array.</summary>
    /// <param name="value">The value to serialize.</param>
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
    /// <param name="payload">The serialized payload bytes.</param>
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
        member this.MaxPayloadBytes = maxPayloadBytes

/// <summary>
/// Serializer preflight: the injected codec provider must resolve an Orleans codec for every
/// exact argument and reply type of a contract before a bound record is returned. Success is
/// cached per contract shape and serializer-service instance.
/// </summary>
[<RequireQualifiedAccess>]
module internal SerializerPreflight =

    /// <summary>Per codec-provider cache of API shapes already validated.</summary>
    let private validated =
        ConditionalWeakTable<obj, ConcurrentDictionary<Type, bool>>()

    /// <summary>Resolve the codec provider of a client or activation.</summary>
    /// <param name="services">The client or activation's service provider.</param>
    /// <param name="grainTypeName">The contract's grain type name.</param>
    /// <exception cref="System.InvalidOperationException">No <see cref="ICodecProvider"/> is registered.</exception>
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
    /// <param name="provider">The resolved Orleans codec provider.</param>
    /// <param name="grainTypeName">The contract's grain type name, for diagnostics.</param>
    /// <param name="role">What kind of type this is (for example "argument" or "reply"), for diagnostics.</param>
    /// <param name="owner">The declaring operation or facet, for diagnostics.</param>
    /// <param name="declaredType">The declared CLR type to resolve a codec for and declare as top-level.</param>
    /// <exception cref="System.InvalidOperationException">
    /// No Orleans codec is registered for <paramref name="declaredType"/>, resolving its codec
    /// otherwise fails, or it cannot be declared as a top-level payload type.
    /// </exception>
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
    /// <param name="provider">The resolved Orleans codec provider.</param>
    /// <param name="grainTypeName">The contract's grain type name, for diagnostics.</param>
    /// <param name="apiType">The API record type this shape was reflected from; the cache key.</param>
    /// <param name="declared">Every declared operation's ID, argument type, and reply type.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Any declared argument or reply type has no registered Orleans codec, or cannot be declared
    /// as a top-level payload type. See <see cref="checkType"/>.
    /// </exception>
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
    /// <param name="provider">The resolved Orleans codec provider.</param>
    /// <param name="grainTypeName">The contract's grain type name, for diagnostics.</param>
    /// <param name="states">Every attached persistent state's name and stored CLR type.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Any stored type has no registered Orleans codec, or cannot be declared as a top-level
    /// payload type. See <see cref="checkType"/>.
    /// </exception>
    let ensureStoredTypes (provider: ICodecProvider) (grainTypeName: string) (states: (string * Type)[]) =
        for stateName, storedType in states do
            checkType provider grainTypeName "stored state" $"persistent state '{stateName}'" storedType

    /// <summary>
    /// Validate the stored type of every attached transactional state of one hosted definition.
    /// </summary>
    /// <remarks>
    /// Same reason as the persistent variant, plus one that is specific to transactions: the
    /// runtime snapshots a transactional value through the exact-type payload codec before the
    /// first write of every transaction, and exact-type serialization makes Orleans elide the
    /// field type — so the stored type has to be resolvable by name, which is precisely what
    /// declaring it here arranges. Without the declaration a state type from an application
    /// assembly serializes and then fails to deserialize on the way back out of the snapshot.
    /// </remarks>
    /// <param name="provider">The resolved Orleans codec provider.</param>
    /// <param name="grainTypeName">The contract's grain type name, for diagnostics.</param>
    /// <param name="states">Every attached transactional state's name and stored CLR type.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Any stored type has no registered Orleans codec, or cannot be declared as a top-level
    /// payload type. See <see cref="checkType"/>.
    /// </exception>
    let ensureTransactionalStoredTypes
        (provider: ICodecProvider)
        (grainTypeName: string)
        (states: (string * Type)[])
        =
        for stateName, storedType in states do
            checkType provider grainTypeName "stored state" $"transactional state '{stateName}'" storedType

    /// <summary>
    /// Validate the state and event types of a journaled definition. Spec 004 item 3.
    /// </summary>
    /// <remarks>
    /// Declaring both is not optional bookkeeping, it is what makes a journal readable again.
    /// Every stored view and every stored entry is a payload produced by the exact-type codec,
    /// which makes Orleans elide the field type, so the F# binary codec has to resolve the type
    /// from the name embedded in the bytes — and its fallback, <c>Type.GetType</c>, searches only
    /// <c>Orleans.FSharp</c> and the core library. Without the declaration a journal written by an
    /// application assembly serializes and then cannot be replayed.
    /// </remarks>
    /// <param name="provider">The resolved Orleans codec provider.</param>
    /// <param name="grainTypeName">The contract's grain type name, for diagnostics.</param>
    /// <param name="stateType">The journaled definition's view/state CLR type.</param>
    /// <param name="eventType">The journaled definition's event CLR type.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Either type has no registered Orleans codec, or cannot be declared as a top-level payload
    /// type. See <see cref="checkType"/>.
    /// </exception>
    let ensureJournalTypes (provider: ICodecProvider) (grainTypeName: string) (stateType: Type) (eventType: Type) =
        checkType provider grainTypeName "journal state" "the journal" stateType
        checkType provider grainTypeName "journal event" "the journal" eventType

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
/// The caller-side view of one opened functional stream, used by
/// <see cref="M:Orleans.FSharp.FunctionalStream.withBatchSize"/> to reach the batch-size knob of
/// Orleans' underlying request through our typed wrapper.
/// </summary>
type internal IFunctionalCallerStream =

    /// <summary>Ask Orleans to drain at most this many elements into one reply message.</summary>
    /// <param name="maxBatchSize">The maximum number of elements to batch per reply message.</param>
    abstract SetMaxBatchSize: maxBatchSize: int -> unit

/// <summary>
/// The caller-side enumerator of one server-streaming operation: it pulls fixed item replies from
/// Orleans' own enumerator proxy and turns each one into the operation's exact item type, applying
/// the same reply validation a unary call applies -- token length, token identity, non-null
/// payload, local payload limit -- before any user payload is deserialized.
/// </summary>
[<Sealed>]
type internal FunctionalStreamCallEnumerator<'Item>
    (
        source: IAsyncEnumerator<FunctionalReply>,
        codec: FunctionalPayloadCodec,
        grainType: string,
        operationId: string,
        itemToken: byte[],
        maxPayloadBytes: int
    ) =

    let mutable current = Unchecked.defaultof<'Item>

    /// <summary>
    /// Validate one item reply's shape, protocol token, and the local payload limit before it is
    /// deserialized, mirroring the checks a unary call's reply gets.
    /// </summary>
    /// <param name="reply">The item reply pulled from the underlying Orleans enumerator.</param>
    /// <exception cref="System.InvalidOperationException">
    /// The reply is null; its protocol token is the wrong length or does not match the expected
    /// item token; its payload is null; or its payload exceeds the local payload-size limit.
    /// </exception>
    member private _.Validate(reply: FunctionalReply) =
        if obj.ReferenceEquals(reply, null) then
            fail
                TransportStage
                $"streaming operation '{operationId}' on grain type '{grainType}' yielded an item with no reply."

        if isNull reply.ProtocolToken || reply.ProtocolToken.Length <> ProtocolToken.Length then
            fail
                TransportStage
                $"an item of streaming operation '{operationId}' on grain type '{grainType}' carries a protocol token of {(if isNull reply.ProtocolToken then 0 else reply.ProtocolToken.Length)} bytes; exactly {ProtocolToken.Length} bytes are required."

        if not (ProtocolToken.equal reply.ProtocolToken itemToken) then
            fail
                TransportStage
                $"an item of streaming operation '{operationId}' on grain type '{grainType}' carries protocol token {ProtocolToken.toHex reply.ProtocolToken}, but {ProtocolToken.toHex itemToken} was expected."

        if isNull reply.Payload then
            fail
                TransportStage
                $"an item of streaming operation '{operationId}' on grain type '{grainType}' carries no payload."

        PayloadLimit.ensure CallerStreamItemReceive grainType operationId reply.Payload.Length maxPayloadBytes

    interface IAsyncEnumerator<'Item> with
        /// <summary>The most recently produced item.</summary>
        member _.Current = current

        /// <summary>Pull, validate, and deserialize the next item; false when the stream is exhausted.</summary>
        /// <exception cref="System.InvalidOperationException">The pulled item reply fails validation. See <see cref="Validate"/>.</exception>
        member this.MoveNextAsync() =
            ValueTask<bool>(
                task {
                    match! source.MoveNextAsync() with
                    | false -> return false
                    | true ->
                        let reply = source.Current
                        this.Validate reply
                        current <- codec.Deserialize<'Item> reply.Payload
                        return true
                }
            )

    interface IAsyncDisposable with
        /// <summary>Dispose the underlying Orleans enumerator.</summary>
        member _.DisposeAsync() = source.DisposeAsync()

/// <summary>
/// The caller-side <c>IAsyncEnumerable</c> one bound streaming operation returns. It is the value
/// an F# caller pipes into <c>TaskSeq</c> and a C# caller writes <c>await foreach</c> over.
/// </summary>
[<Sealed>]
type internal FunctionalStreamCall<'Item>
    (
        source: IAsyncEnumerable<FunctionalReply>,
        codec: FunctionalPayloadCodec,
        grainType: string,
        operationId: string,
        itemToken: byte[],
        maxPayloadBytes: int
    ) =

    interface IAsyncEnumerable<'Item> with
        /// <summary>Create the enumerator for one independent remote enumeration of this stream.</summary>
        /// <param name="cancellationToken">Cancels the enumeration.</param>
        member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
            new FunctionalStreamCallEnumerator<'Item>(
                source.GetAsyncEnumerator cancellationToken,
                codec,
                grainType,
                operationId,
                itemToken,
                maxPayloadBytes
            )
            :> IAsyncEnumerator<'Item>

    interface IFunctionalCallerStream with
        /// <summary>
        /// Set Orleans' per-reply batch size on the underlying request; a no-op on the in-memory
        /// unit-test transport, which has no batching to configure.
        /// </summary>
        /// <param name="maxBatchSize">The maximum number of elements to batch per reply message.</param>
        member _.SetMaxBatchSize(maxBatchSize: int) =
            match box source with
            | :? Orleans.Runtime.AsyncEnumerableRequest<FunctionalReply> as request ->
                request.MaxBatchSize <- maxBatchSize
            | _ ->
                // The in-memory unit-test transport yields items one at a time and has no batching
                // to configure; the knob is Orleans' and only exists on the Orleans path.
                ()

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

    // Which Orleans invokable base this operation's requests use. Read from the same admission
    // byte dispatch compares against the hosted descriptor, so caller and host cannot disagree
    // about whether a call is transactional without the call being rejected.
    let isTransactional = AdmissionFlags.isTransactional admissionFlags

    /// <summary>
    /// Validate the fixed reply shape, its protocol token, and the local reply-size limit
    /// before any user payload is deserialized.
    /// </summary>
    /// <param name="reply">The reply received from the sender.</param>
    /// <exception cref="System.InvalidOperationException">
    /// The reply is null; its protocol token is the wrong length or does not match the expected
    /// reply token; its payload is null; or its payload exceeds the local payload-size limit.
    /// </exception>
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
    /// <param name="argument">The exact operation argument.</param>
    /// <param name="cancellationToken">Cancels waiting for the reply.</param>
    /// <exception cref="System.InvalidOperationException">
    /// The serialized argument or the received reply exceeds the local payload-size limit, or the
    /// reply fails validation. See <see cref="ValidateReply"/>.
    /// </exception>
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
                    let! reply =
                        if isTransactional then
                            sender.SendTransactionalAsync(envelope, cancellationToken)
                        else
                            sender.SendAsync(envelope, cancellationToken)

                    this.ValidateReply reply
                    return codec.Deserialize<'Reply> reply.Payload
            }

    /// <summary>
    /// Serialize the exact argument, construct the fixed streaming request, and return the lazy
    /// sequence of exact items. Spec 004 item 6.
    /// </summary>
    /// <remarks>
    /// Argument serialization and the caller-side payload check happen <b>now</b>, when the API
    /// field is applied, not on first pull: an argument that cannot be sent is a fault of the call,
    /// and deferring it to the first <c>MoveNextAsync</c> would report it from a place the caller
    /// did not write. Nothing is sent yet — the first message leaves on the first pull.
    /// </remarks>
    /// <param name="argument">The exact operation argument.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <exception cref="System.InvalidOperationException">
    /// The serialized argument exceeds the local payload-size limit.
    /// </exception>
    member _.InvokeStream<'Argument, 'Item>
        (argument: 'Argument, cancellationToken: CancellationToken)
        : IAsyncEnumerable<'Item> =
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

        // The reply token of a streaming descriptor is its stream-item token, so the same field
        // carries the per-item expectation the unary path carries the per-reply one.
        FunctionalStreamCall<'Item>(
            sender.OpenStream(envelope, cancellationToken),
            codec,
            grainType,
            operationId,
            replyToken,
            maxPayloadBytes
        )
        :> IAsyncEnumerable<'Item>

/// <summary>
/// The typed client-closure factory. Its generic method is closed once per operation
/// descriptor while the contract is sealed, so binding and calling never close a generic.
/// </summary>
[<AbstractClass; Sealed>]
type internal BoundCallFactory =

    /// <summary>Create both closures of one bound operation over its call site.</summary>
    /// <param name="site">The bound operation's call site to close the client closures over.</param>
    static member Create<'Argument, 'Reply>(site: FunctionalCallSite) : BoundCall =
        { Field = box (fun (argument: 'Argument) -> site.Invoke<'Argument, 'Reply>(argument, CancellationToken.None))
          Cancellable =
            box (fun (argument: 'Argument) (cancellationToken: CancellationToken) ->
                site.Invoke<'Argument, 'Reply>(argument, cancellationToken)) }

    /// <summary>Create both closures of one bound <b>streaming</b> operation over its call site.</summary>
    /// <remarks>
    /// The caller's token is carried by the request rather than applied to the enumerator, because
    /// that is where Orleans reads it: <c>AsyncEnumeratorProxy</c> links
    /// <c>request.GetCancellationToken()</c> with whatever token <c>GetAsyncEnumerator</c> is given
    /// and owns the linked source's disposal, so both tokens end up cancelling the enumeration and
    /// neither of them is ours to manage.
    /// </remarks>
    /// <param name="site">The bound streaming operation's call site to close the client closures over.</param>
    static member CreateStream<'Argument, 'Item>(site: FunctionalCallSite) : BoundCall =
        { Field =
            box (fun (argument: 'Argument) -> site.InvokeStream<'Argument, 'Item>(argument, CancellationToken.None))
          Cancellable =
            box (fun (argument: 'Argument) (cancellationToken: CancellationToken) ->
                site.InvokeStream<'Argument, 'Item>(argument, cancellationToken)) }

/// <summary>Preclosing of the typed client-closure factory.</summary>
[<RequireQualifiedAccess>]
module internal BoundClosure =

    /// <summary>The reflected open generic method behind <see cref="BoundCallFactory.Create"/>.</summary>
    /// <exception cref="System.InvalidOperationException">
    /// The <c>Create</c> method could not be found by reflection, meaning the factory shape and this
    /// lookup have drifted apart.
    /// </exception>
    let private createMethod =
        match
            typeof<BoundCallFactory>
                .GetMethod("Create", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        with
        | null -> fail ContractStage "the typed client-closure factory 'BoundCallFactory.Create' was not found."
        | method -> method

    /// <summary>The reflected open generic method behind <see cref="BoundCallFactory.CreateStream"/>.</summary>
    /// <exception cref="System.InvalidOperationException">
    /// The <c>CreateStream</c> method could not be found by reflection, meaning the factory shape
    /// and this lookup have drifted apart.
    /// </exception>
    let private createStreamMethod =
        match
            typeof<BoundCallFactory>
                .GetMethod("CreateStream", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        with
        | null -> fail ContractStage "the typed client-closure factory 'BoundCallFactory.CreateStream' was not found."
        | method -> method

    /// <summary>
    /// Close the client-closure factory over one operation's exact argument and reply types.
    /// Called once per descriptor while the contract is sealed.
    /// </summary>
    /// <param name="argumentType">The operation's exact argument type.</param>
    /// <param name="replyType">The operation's exact reply type.</param>
    let precompute (argumentType: Type) (replyType: Type) : Func<FunctionalCallSite, BoundCall> =
        FunctionalInstrumentation.countGenericClosing ()

        let closed = createMethod.MakeGenericMethod [| argumentType; replyType |]

        closed.CreateDelegate typeof<Func<FunctionalCallSite, BoundCall>> :?> Func<FunctionalCallSite, BoundCall>

    /// <summary>
    /// Close the streaming client-closure factory over one operation's exact argument and item
    /// types. Called once per streaming descriptor while the contract is sealed.
    /// </summary>
    /// <param name="argumentType">The operation's exact argument type.</param>
    /// <param name="itemType">The operation's exact stream item type.</param>
    let precomputeStream (argumentType: Type) (itemType: Type) : Func<FunctionalCallSite, BoundCall> =
        FunctionalInstrumentation.countGenericClosing ()

        let closed = createStreamMethod.MakeGenericMethod [| argumentType; itemType |]

        closed.CreateDelegate typeof<Func<FunctionalCallSite, BoundCall>> :?> Func<FunctionalCallSite, BoundCall>

/// <summary>Client-side transport configuration read while binding a reference.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalTransportConfiguration =

    /// <summary>
    /// The local payload limit of this process. An unconfigured process uses the documented
    /// 16 MiB default; a configured non-positive value is a configuration error.
    /// </summary>
    /// <param name="services">The process's service provider.</param>
    /// <exception cref="System.InvalidOperationException">A configured limit is not positive.</exception>
    let maxPayloadBytes (services: IServiceProvider) =
        match services.GetService typeof<IOptions<FunctionalGrainTransportOptions>> with
        | :? IOptions<FunctionalGrainTransportOptions> as options ->
            PayloadLimit.validateLimit options.Value.MaxPayloadBytes
        | _ -> FunctionalGrainTransportOptions.DefaultMaxPayloadBytes

    /// <summary>The exact-type payload codec of this process, carrying this process's own limit.</summary>
    /// <param name="services">The process's service provider.</param>
    /// <param name="grainTypeName">The contract's grain type name, for diagnostics.</param>
    /// <exception cref="System.InvalidOperationException">No <see cref="Serializer"/> is registered.</exception>
    let payloadCodec (services: IServiceProvider) (grainTypeName: string) =
        match services.GetService typeof<Serializer> with
        | :? Serializer as serializer ->
            FunctionalPayloadCodec(serializer, serializer.SessionPool, maxPayloadBytes services)
        | _ ->
            fail
                BindingStage
                $"binding the contract '{grainTypeName}' requires the Orleans serializer, but no Serializer is registered in this process."
