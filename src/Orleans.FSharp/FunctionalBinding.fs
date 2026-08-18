namespace Orleans.FSharp

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// A bound functional reference: the domain key, the cached API record instance, and
/// selector-based calls for advanced scenarios.
/// </summary>
[<Sealed>]
type FunctionalGrainRef<'Actor, 'Key, 'Api>
    internal
    (
        key: 'Key,
        api: 'Api,
        contract: GrainContract<'Actor, 'Key, 'Api>,
        grainId: GrainId,
        bound: BoundCall[]
    ) =

    /// <summary>Resolve one explicit selector against the cached shape for a raw call.</summary>
    /// <param name="entry">The calling member's own name, used to phrase the diagnostic.</param>
    /// <param name="selector">The caller-supplied field projection to resolve.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; or when the resolved operation's argument or
    /// reply type does not match <paramref name="selector"/>'s own type parameters.
    /// </exception>
    member private _.Resolve<'Argument, 'Reply>
        (entry: string, selector: OperationSelector<'Api, 'Argument, 'Reply>)
        : BoundCall =
        let operation = contract.Resolve(entry, selector)

        // Defensive: the selector's inferred types always match the descriptor it resolved to,
        // so a mismatch here means the API record was reflected against a different shape.
        if operation.ArgumentType <> typeof<'Argument> || operation.ReplyType <> typeof<'Reply> then
            fail
                BindingStage
                $"the '{entry}' selector of grain type '{contract.GrainTypeName}' resolved to operation '{operation.OperationId}', whose argument and reply types are '{operation.ArgumentType.FullName}' and '{operation.ReplyType.FullName}', but the call site supplied '{typeof<'Argument>.FullName}' and '{typeof<'Reply>.FullName}'."

        bound.[operation.Index]

    /// <summary>The domain key this reference addresses.</summary>
    member _.key = key

    /// <summary>The bound API record instance; the same instance on every access.</summary>
    member _.api = api

    /// <summary>The contract this reference was bound from.</summary>
    member internal _.Contract = contract

    /// <summary>The preclosed closure pair of one operation, by descriptor index.</summary>
    /// <param name="index">The operation descriptor's zero-based index.</param>
    member internal _.BoundCall(index: int) = bound.[index]

    /// <summary>Every preclosed closure pair, in descriptor order.</summary>
    member internal _.BoundCalls = bound

    /// <summary>The exact Orleans identity this reference addresses.</summary>
    member internal _.GrainId = grainId

    /// <summary>Call one operation identified by an explicit selector.</summary>
    /// <param name="selector">The API field to call.</param>
    /// <param name="argument">The call argument.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields.
    /// </exception>
    member this.call (selector: OperationSelector<'Api, 'Argument, 'Reply>) (argument: 'Argument) : Task<'Reply> =
        let call = this.Resolve("call", selector)
        (unbox<'Argument -> Task<'Reply>> call.Field) argument

    /// <summary>Call one operation with cooperative remote cancellation.</summary>
    /// <param name="selector">The API field to call.</param>
    /// <param name="argument">The call argument.</param>
    /// <param name="cancellationToken">The token Orleans links into the remote call for cooperative cancellation.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields.
    /// </exception>
    member this.callCancellable
        (selector: OperationSelector<'Api, 'Argument, 'Reply>)
        (argument: 'Argument)
        (cancellationToken: CancellationToken)
        : Task<'Reply> =
        let call = this.Resolve("callCancellable", selector)
        (unbox<'Argument -> CancellationToken -> Task<'Reply>> call.Cancellable) argument cancellationToken

    /// <summary>Resolve one explicit streaming selector against the cached shape. Spec 004 item 6.</summary>
    /// <param name="entry">The calling member's own name, used to phrase the diagnostic.</param>
    /// <param name="selector">The caller-supplied streaming field projection to resolve.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; or when the resolved operation's argument or item
    /// type does not match <paramref name="selector"/>'s own type parameters.
    /// </exception>
    member private _.ResolveStream<'Argument, 'Item>
        (entry: string, selector: StreamSelector<'Api, 'Argument, 'Item>)
        : BoundCall =
        let operation = contract.ResolveStream(entry, selector)

        if operation.ArgumentType <> typeof<'Argument> || operation.ReplyType <> typeof<'Item> then
            fail
                BindingStage
                $"the '{entry}' selector of grain type '{contract.GrainTypeName}' resolved to operation '{operation.OperationId}', whose argument and item types are '{operation.ArgumentType.FullName}' and '{operation.ReplyType.FullName}', but the call site supplied '{typeof<'Argument>.FullName}' and '{typeof<'Item>.FullName}'."

        bound.[operation.Index]

    /// <summary>Open one streaming operation identified by an explicit selector.</summary>
    /// <param name="selector">The streaming API field to open.</param>
    /// <param name="argument">The call argument.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields.
    /// </exception>
    member this.stream
        (selector: StreamSelector<'Api, 'Argument, 'Item>)
        (argument: 'Argument)
        : IAsyncEnumerable<'Item> =
        let call = this.ResolveStream("stream", selector)
        (unbox<'Argument -> IAsyncEnumerable<'Item>> call.Field) argument

    /// <summary>
    /// Open one streaming operation with cooperative cancellation: the token is carried by the
    /// request, so Orleans links it into every enumeration started from the returned sequence.
    /// </summary>
    /// <param name="selector">The streaming API field to open.</param>
    /// <param name="argument">The call argument.</param>
    /// <param name="cancellationToken">The token Orleans links into every enumeration started from the returned sequence.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields.
    /// </exception>
    member this.streamCancellable
        (selector: StreamSelector<'Api, 'Argument, 'Item>)
        (argument: 'Argument)
        (cancellationToken: CancellationToken)
        : IAsyncEnumerable<'Item> =
        let call = this.ResolveStream("streamCancellable", selector)
        (unbox<'Argument -> CancellationToken -> IAsyncEnumerable<'Item>> call.Cancellable) argument cancellationToken

/// <summary>
/// Reference binding: encode the key, resolve the transport, validate serializers, and create
/// one preclosed typed closure per API-record field.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalBinding =

    /// <summary>
    /// Bind one contract to one domain key. Every reflective step (API shape, selectors,
    /// generic closing) has already happened while the contract was sealed; binding only
    /// encodes the key, resolves services, validates serializers, and instantiates the
    /// preclosed closures.
    /// </summary>
    /// <param name="contract">The sealed contract to bind.</param>
    /// <param name="factory">The grain factory of the calling client or activation.</param>
    /// <param name="key">The domain key of the target grain.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="factory"/> cannot create a reference for this grain type
    /// through the functional interface, or resolves to something other than a functional grain
    /// reference; also thrown, indirectly, by serializer-preflight validation when an operation's
    /// argument or reply type has no registered codec.
    /// </exception>
    let bind
        (contract: GrainContract<'Actor, 'Key, 'Api>)
        (factory: IGrainFactory)
        (key: 'Key)
        : FunctionalGrainRef<'Actor, 'Key, 'Api> =
        let grainTypeName = contract.GrainTypeName

        // 1. Encode the domain key and construct the exact grain identity.
        let grainId = contract.GrainIdOf key

        // 2. The stable actor-specific GrainInterfaceType, built once per contract.
        let metadata = contract.TargetMetadata

        // 3-4. Obtain the exact custom reference and verify its type. A grain factory which
        //      also implements the internal transport seam (the in-memory unit-test transport)
        //      short-circuits the Orleans send path while running the same binding code.
        let services, codec, sender =
            match FunctionalTransportSource.tryResolve factory grainTypeName with
            | Some source ->
                let services = source.Services

                services,
                FunctionalTransportConfiguration.payloadCodec services grainTypeName,
                source.CreateSender(grainId, metadata)
            | None ->
                let addressable =
                    try
                        factory.GetGrain(grainId, metadata.GrainInterfaceType)
                    with cause ->
                        failCause
                            BindingStage
                            $"the grain factory '{factory.GetType().FullName}' could not create a reference for grain type '{grainTypeName}' through the functional interface ID '{metadata.InterfaceId}'. {FunctionalTransportSource.Guidance}"
                            cause

                match box addressable with
                | :? FunctionalGrainReference as reference ->
                    let codec =
                        match reference.PayloadCodec with
                        | :? FunctionalPayloadCodec as payloadCodec -> payloadCodec
                        | _ -> FunctionalTransportConfiguration.payloadCodec reference.Services grainTypeName

                    reference.Services,
                    codec,
                    (FunctionalReferenceSender(reference, metadata) :> IFunctionalRequestSender)
                | other ->
                    let actual =
                        if isNull other then
                            "<null>"
                        else
                            other.GetType().FullName

                    fail
                        BindingStage
                        $"binding grain type '{grainTypeName}' returned '{actual}' instead of the functional grain reference. {FunctionalTransportSource.Guidance}"

        // 5. Validate that every exact argument and reply type has a registered codec.
        let provider = SerializerPreflight.providerOf services grainTypeName
        SerializerPreflight.ensure provider grainTypeName contract.ApiType contract.DeclaredTypes

        let maxPayloadBytes = FunctionalTransportConfiguration.maxPayloadBytes services

        // 6. One call site and one preclosed closure pair per descriptor.
        let bound =
            contract.Operations
            |> Array.map (fun operation ->
                let site =
                    FunctionalCallSite(
                        sender,
                        codec,
                        grainTypeName,
                        contract.Version,
                        operation.OperationId,
                        operation.RequestToken,
                        operation.ReplyToken,
                        operation.AdmissionFlags,
                        maxPayloadBytes
                    )

                operation.ClosureFactory.Invoke site)

        // 7. Build the API record with the cached record constructor and retain that instance.
        let api =
            unbox<'Api> (contract.Shape.Constructor(bound |> Array.map (fun call -> call.Field)))

        FunctionalGrainRef<'Actor, 'Key, 'Api>(key, api, contract, grainId, bound)

/// <summary>Binding of a contract to an Orleans grain reference.</summary>
/// <remarks>
/// <para>
/// Call sites are ordinary curried applications —
/// <c>FunctionalGrain.ref contract factory key</c> — and the point-free binding
/// <c>let ref = FunctionalGrain.ref contract</c> infers the complete concrete type
/// <c>IGrainFactory -&gt; 'Key -&gt; 'Api</c> with no annotation and no later use site.
/// </para>
/// <para>
/// The binding takes <c>contract</c> as its single declared parameter and returns the
/// remaining curried function on purpose. F# inserts flexibility for non-sealed parameter
/// types at every use of a function or member, so declaring <c>factory: IGrainFactory</c> as
/// a second curried parameter would make every partial application generic in a flexible
/// <c>'_a :&gt; IGrainFactory</c> and hit the value restriction (FS0030). Flexibility is
/// inserted only for declared parameters of a member, so with the factory in the result type
/// the partial application stays concrete, while argument subsumption still lets any
/// <c>IGrainFactory</c> implementation (for example <c>IClusterClient</c>) be applied
/// directly.
/// </para>
/// <para>
/// One consequence is worth knowing at call sites: because the factory is applied to the
/// returned function rather than to a declared parameter, F# does not insert subtype
/// flexibility for it. Annotate a caller's factory parameter as <c>IGrainFactory</c> — any
/// implementation, <c>IClusterClient</c> included, is accepted by ordinary subsumption, so a
/// flexible <c>#IGrainFactory</c> annotation buys nothing here and is reported as
/// <c>FS0064</c> ("less generic than indicated by its type annotations"), which is an error
/// under <c>TreatWarningsAsErrors</c>. Code that must stay generic in the factory type — a
/// <c>'F when 'F :&gt; IGrainFactory</c> type parameter on a class, which would otherwise fail
/// with <c>FS0660</c>/<c>FS0663</c> — has two diagnostic-free forms: call through the
/// application-owned binding (<c>let ref = FunctionalGrain.ref contract</c>, then
/// <c>ref factory key</c>), because flexibility is inserted at every use of a named binding
/// even when the compiler has to look through its function type, or upcast once at the call
/// (<c>FunctionalGrain.ref contract (factory :&gt; IGrainFactory) key</c>).
/// The same two forms are also the ones that stay silent for projects that opt the
/// implicit-conversion informationals in with <c>--warnon:3388</c>: applying a derived interface
/// value such as <c>IClusterClient</c> straight to the returned function is an implicit upcast
/// and is reported under that flag, which is off by default.
/// </para>
/// </remarks>
[<AbstractClass; Sealed>]
type FunctionalGrain =

    /// <summary>
    /// Bind the contract to the grain addressed by the domain key and return the bound API
    /// record. The returned function takes the grain factory of the calling client or
    /// activation and then the domain key of the target grain.
    /// </summary>
    /// <param name="contract">The sealed contract.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the returned function is applied, if the grain factory cannot create a
    /// reference for this contract's grain type through the functional interface, or resolves to
    /// something other than a functional grain reference; see <c>FunctionalBinding.bind</c>.
    /// </exception>
    static member ref(contract: GrainContract<'Actor, 'Key, 'Api>) : IGrainFactory -> 'Key -> 'Api =
        fun factory key -> (FunctionalBinding.bind contract factory key).api

    /// <summary>
    /// Bind the contract to the grain addressed by the domain key and return the typed wrapper
    /// exposing the key, the cached API record, and selector-based calls. The returned function
    /// takes the grain factory of the calling client or activation and then the domain key of
    /// the target grain.
    /// </summary>
    /// <param name="contract">The sealed contract.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the returned function is applied, if the grain factory cannot create a
    /// reference for this contract's grain type through the functional interface, or resolves to
    /// something other than a functional grain reference; see <c>FunctionalBinding.bind</c>.
    /// </exception>
    static member rawRef
        (contract: GrainContract<'Actor, 'Key, 'Api>)
        : IGrainFactory -> 'Key -> FunctionalGrainRef<'Actor, 'Key, 'Api> =
        fun factory key -> FunctionalBinding.bind contract factory key

    /// <summary>
    /// The <c>StreamId</c> whose implicit delivery reaches the grain this contract addresses by
    /// <paramref name="key"/>. Use it for every publish aimed at an <c>onStream</c> declaration.
    /// </summary>
    /// <param name="contract">The sealed contract of the subscribing definition.</param>
    /// <remarks>
    /// <para>
    /// <b>Why this exists rather than <c>StreamId.Create(ns, key)</c>.</b> Orleans routes an
    /// implicit delivery to <c>GrainId.Create(grainType, streamId.Key)</c> — the stream key bytes
    /// verbatim (<c>DefaultStreamIdMapper</c>, for any grain class that implements no legacy grain-key
    /// interface, which the functional marker does not). So the stream key must be the grain key
    /// <b>in this contract's own Orleans encoding</b>, and for two of the six key codecs
    /// <c>StreamId.Create</c>'s own overloads do not produce it:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>stringKey</c> and <c>guidKey</c> agree —
    /// <c>StreamId.Create(ns, "k")</c> and <c>StreamId.Create(ns, guid)</c> produce exactly the
    /// UTF-8 string and the 32-char "N"-format Guid the codecs encode.</description></item>
    /// <item><description><c>integerKey</c> does <b>not</b>:
    /// <c>StreamId.Create(ns, 42L)</c> writes decimal <c>"42"</c>, while Orleans'
    /// <c>GrainIdKeyExtensions.CreateIntegerKey</c> — which the codec uses, because that is what
    /// <c>IGrainWithIntegerKey</c> identities really are — writes hexadecimal <c>"2A"</c>. A
    /// publish built the naive way silently addresses a different grain (and one whose key decodes
    /// as 0x42 = 66).</description></item>
    /// <item><description>the compound codecs have no <c>StreamId.Create</c> overload at
    /// all.</description></item>
    /// </list>
    /// <para>
    /// This member always agrees with the contract, because it asks the contract.
    /// </para>
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the returned function is applied: if <paramref name="contract"/> is null, or
    /// the stream namespace supplied to it is blank or white-space.
    /// </exception>
    static member streamId(contract: GrainContract<'Actor, 'Key, 'Api>) : string -> 'Key -> StreamId =
        fun streamNamespace key ->
            if obj.ReferenceEquals(contract, null) then
                fail BindingStage "building a StreamId requires a sealed contract."

            if isBlank streamNamespace then
                fail
                    BindingStage
                    $"the stream namespace for grain type '{contract.GrainTypeName}' must not be empty or white-space: Orleans only resolves implicit subscribers for a stream that has one."

            StreamId.Create(
                System.Text.Encoding.UTF8.GetBytes streamNamespace,
                contract.GrainIdOf(key).Key.AsSpan()
            )

    /// <summary>
    /// The <c>ChannelId</c> whose implicit publish reaches the grain this contract addresses by
    /// <paramref name="key"/>. The broadcast-channel counterpart of
    /// <see cref="M:Orleans.FSharp.FunctionalGrain.streamId``3(Orleans.FSharp.GrainContract{``0,``1,``2})"/>,
    /// with the same reason for existing.
    /// </summary>
    /// <param name="contract">The sealed contract of the subscribing definition.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the returned function is applied: if <paramref name="contract"/> is null, or
    /// the channel namespace supplied to it is blank or white-space.
    /// </exception>
    static member channelId
        (contract: GrainContract<'Actor, 'Key, 'Api>)
        : string -> 'Key -> Orleans.BroadcastChannel.ChannelId =
        fun channelNamespace key ->
            if obj.ReferenceEquals(contract, null) then
                fail BindingStage "building a ChannelId requires a sealed contract."

            if isBlank channelNamespace then
                fail
                    BindingStage
                    $"the channel namespace for grain type '{contract.GrainTypeName}' must not be empty or white-space: Orleans only resolves implicit subscribers for a channel that has one."

            Orleans.BroadcastChannel.ChannelId.Create(
                System.Text.Encoding.UTF8.GetBytes channelNamespace,
                contract.GrainIdOf(key).Key.AsSpan()
            )

/// <summary>
/// Tuning of one opened functional stream. Spec 004 item 6.
/// </summary>
[<AbstractClass; Sealed>]
type FunctionalStream =

    /// <summary>
    /// Ask the target to drain at most <paramref name="maxBatchSize"/> synchronously-available
    /// items into each reply message, and return the same sequence.
    /// </summary>
    /// <param name="maxBatchSize">The maximum items per reply; must be positive. Orleans' own default is 100.</param>
    /// <param name="stream">A sequence returned by a streaming API-record field of this runtime.</param>
    /// <remarks>
    /// <para>
    /// This is Orleans' own <c>AsyncEnumerableRequest.MaxBatchSize</c> knob, reached through the
    /// runtime's typed wrapper. Orleans' <c>AsyncEnumerableExtensions.WithBatchSize</c> cannot be
    /// used directly here: it tests <c>self is AsyncEnumerableRequest&lt;T&gt;</c> at the element
    /// type, and a functional stream's element type is the application's item type while the
    /// underlying request's is the fixed transport reply — so that method would silently do
    /// nothing. This one fails loudly instead when applied to something that is not a functional
    /// stream.
    /// </para>
    /// <para>
    /// Apply it to the value the API field returned, before enumerating: the batch size is read
    /// when an enumeration starts, so setting it afterwards affects only later enumerations.
    /// </para>
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="maxBatchSize"/> is not positive, or when
    /// <paramref name="stream"/> is null or was not returned by a functional streaming operation.
    /// </exception>
    static member withBatchSize (maxBatchSize: int) (stream: IAsyncEnumerable<'Item>) : IAsyncEnumerable<'Item> =
        if maxBatchSize <= 0 then
            fail
                BindingStage
                $"'withBatchSize' requires a positive maximum batch size, but {maxBatchSize} was supplied."

        match box stream with
        | :? IFunctionalCallerStream as callerStream ->
            callerStream.SetMaxBatchSize maxBatchSize
            stream
        | null -> fail BindingStage "'withBatchSize' requires a stream returned by a functional streaming operation, but null was supplied."
        | other ->
            fail
                BindingStage
                $"'withBatchSize' requires a stream returned by a functional streaming operation, but '{other.GetType().FullName}' was supplied."
