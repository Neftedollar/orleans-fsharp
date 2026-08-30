namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.EventSourcing
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// Everything one target-side invocation supplies to a preclosed typed handler adapter: the
/// boxed domain key, the invocation context core, the boxed current primary state, the exact
/// argument bytes, and the payload codec of the hosting silo.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalInvocation =
    {
        /// The domain key, decoded once from the grain identity and boxed.
        Key: obj
        /// The per-invocation context services.
        Core: FunctionalContextCore
        /// The current primary state, boxed.
        State: obj
        /// The exact argument payload received from the caller.
        Payload: byte[]
        /// The exact-type payload codec of the hosting silo.
        Codec: FunctionalPayloadCodec
    }

/// <summary>
/// The preclosed typed server adapter of one operation. It deserializes the exact argument
/// type, builds the typed invocation context, calls the application handler, and serializes the
/// exact reply type, returning the replacement state boxed alongside the reply payload.
/// </summary>
type internal FunctionalServerAdapter = delegate of FunctionalInvocation -> Task<obj * byte[]>

/// <summary>The typed server-adapter factory, closed once per hosted operation.</summary>
[<AbstractClass; Sealed>]
type internal ServerAdapterFactory =

    /// <summary>Close one handler over its exact actor, key, state, argument, and reply types.</summary>
    /// <param name="handler">The boxed application handler, of type <c>Handler&lt;'Actor,'Key,'State,'Argument,'Reply&gt;</c>.</param>
    static member Create<'Actor, 'Key, 'State, 'Argument, 'Reply>(handler: obj) : FunctionalServerAdapter =
        let typed = unbox<Handler<'Actor, 'Key, 'State, 'Argument, 'Reply>> handler

        FunctionalServerAdapter(fun invocation ->
            // Step 4 of the dispatch order: typed payload deserialization with a fresh session,
            // before the per-invocation context exists and before any application code runs.
            let argument = invocation.Codec.Deserialize<'Argument> invocation.Payload

            task {
                // Step 5: the per-invocation context and the preclosed typed handler.
                let context =
                    FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> invocation.Key, invocation.Core)

                let! nextState, reply = typed context (unbox<'State> invocation.State) argument

                // Step 7 (serialization half): the exact reply type.
                let payload = invocation.Codec.Serialize<'Reply> reply
                return box nextState, payload
            })

/// <summary>Preclosing of the typed server-adapter factory.</summary>
[<RequireQualifiedAccess>]
module internal ServerAdapter =

    let private createMethod =
        match
            typeof<ServerAdapterFactory>
                .GetMethod("Create", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        with
        | null -> fail DefinitionStage "the typed server-adapter factory 'ServerAdapterFactory.Create' was not found."
        | method -> method

    /// <summary>
    /// Close the server-adapter factory over one operation's exact types. Called once per hosted
    /// operation while the definition is registered on a silo, never per call.
    /// </summary>
    /// <param name="actorType">The actor brand CLR type.</param>
    /// <param name="keyType">The domain key CLR type.</param>
    /// <param name="stateType">The definition's declared primary state CLR type.</param>
    /// <param name="argumentType">The operation's exact argument type.</param>
    /// <param name="replyType">The operation's exact reply type.</param>
    /// <param name="handler">The boxed application handler.</param>
    let precompute
        (actorType: Type)
        (keyType: Type)
        (stateType: Type)
        (argumentType: Type)
        (replyType: Type)
        (handler: obj)
        : FunctionalServerAdapter =
        FunctionalInstrumentation.countGenericClosing ()

        let closed =
            createMethod.MakeGenericMethod [| actorType; keyType; stateType; argumentType; replyType |]

        (closed.CreateDelegate typeof<Func<obj, FunctionalServerAdapter>> :?> Func<obj, FunctionalServerAdapter>).Invoke
            handler

/// <summary>
/// The preclosed typed server adapter of one <b>streaming</b> operation. Spec 004 item 6. It
/// deserializes the exact argument type, builds the typed invocation context, calls the
/// application handler, and returns an enumerator over the exact item type already serialized to
/// bytes — so the layer above it never sees an application value and never closes a generic.
/// </summary>
type internal FunctionalStreamServerAdapter =
    delegate of FunctionalInvocation * CancellationToken -> IAsyncEnumerator<byte[]>

/// <summary>
/// Serializes each element of one streaming handler's sequence as its exact declared item type.
/// One instance per enumeration; disposal is forwarded so the handler's own <c>finally</c> blocks
/// run when the caller disposes.
/// </summary>
[<Sealed>]
type internal SerializingStreamEnumerator<'Item>(source: IAsyncEnumerator<'Item>, codec: FunctionalPayloadCodec) =

    let mutable current: byte[] = Array.empty

    interface IAsyncEnumerator<byte[]> with
        member _.Current = current

        member _.MoveNextAsync() =
            ValueTask<bool>(
                task {
                    match! source.MoveNextAsync() with
                    | false -> return false
                    | true ->
                        current <- codec.Serialize<'Item> source.Current
                        return true
                }
            )

    interface IAsyncDisposable with
        member _.DisposeAsync() = source.DisposeAsync()

/// <summary>The typed streaming server-adapter factory, closed once per hosted operation.</summary>
[<AbstractClass; Sealed>]
type internal StreamServerAdapterFactory =

    /// <summary>Close one streaming handler over its exact actor, key, state, argument, and item types.</summary>
    /// <param name="handler">The boxed application handler, of type <c>StreamHandler&lt;'Actor,'Key,'State,'Argument,'Item&gt;</c>.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown, from the returned adapter's own invocation, when the handler returns a null
    /// <c>IAsyncEnumerable</c>.
    /// </exception>
    static member Create<'Actor, 'Key, 'State, 'Argument, 'Item>(handler: obj) : FunctionalStreamServerAdapter =
        let typed = unbox<StreamHandler<'Actor, 'Key, 'State, 'Argument, 'Item>> handler

        FunctionalStreamServerAdapter(fun invocation cancellationToken ->
            // Same order as the unary adapter: the exact argument is deserialized before the
            // context exists and before any application code runs.
            let argument = invocation.Codec.Deserialize<'Argument> invocation.Payload

            let context =
                FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> invocation.Key, invocation.Core)

            let items = typed context (unbox<'State> invocation.State) argument

            if obj.ReferenceEquals(items, null) then
                fail
                    DefinitionStage
                    "a streaming handler returned a null IAsyncEnumerable. Return an empty sequence instead."

            // The enumeration's own token, which Orleans cancels when the caller disposes, is what
            // the handler's sequence is enumerated with -- so a handler that honours its token sees
            // the caller's disposal, and one that does not still has its finally blocks run by the
            // DisposeAsync forwarded below.
            new SerializingStreamEnumerator<'Item>(items.GetAsyncEnumerator cancellationToken, invocation.Codec)
            :> IAsyncEnumerator<byte[]>)

/// <summary>Preclosing of the typed streaming server-adapter factory.</summary>
[<RequireQualifiedAccess>]
module internal StreamServerAdapter =

    let private createMethod =
        match
            typeof<StreamServerAdapterFactory>
                .GetMethod("Create", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        with
        | null ->
            fail
                DefinitionStage
                "the typed streaming server-adapter factory 'StreamServerAdapterFactory.Create' was not found."
        | method -> method

    /// <summary>Close the streaming server-adapter factory over one operation's exact types.</summary>
    /// <param name="actorType">The actor brand CLR type.</param>
    /// <param name="keyType">The domain key CLR type.</param>
    /// <param name="stateType">The definition's declared primary state CLR type.</param>
    /// <param name="argumentType">The operation's exact argument type.</param>
    /// <param name="itemType">The operation's exact streamed item type.</param>
    /// <param name="handler">The boxed application handler.</param>
    let precompute
        (actorType: Type)
        (keyType: Type)
        (stateType: Type)
        (argumentType: Type)
        (itemType: Type)
        (handler: obj)
        : FunctionalStreamServerAdapter =
        FunctionalInstrumentation.countGenericClosing ()

        let closed =
            createMethod.MakeGenericMethod [| actorType; keyType; stateType; argumentType; itemType |]

        (closed.CreateDelegate typeof<Func<obj, FunctionalStreamServerAdapter>>
        :?> Func<obj, FunctionalStreamServerAdapter>)
            .Invoke
            handler

/// <summary>
/// The preclosed typed server adapter of one operation of a JOURNALED definition. It has exactly
/// the <see cref="T:Orleans.FSharp.FunctionalServerAdapter"/> shape, and the <c>obj</c> it returns
/// is the boxed <c>obj list</c> of events the handler raised rather than a replacement state: a
/// journaled definition's state is the fold of its journal, so nothing else can replace it.
/// </summary>
[<AbstractClass; Sealed>]
type internal JournaledServerAdapterFactory =

    /// <summary>Close one journaled handler over its exact types.</summary>
    /// <param name="handler">The boxed application handler, of type <c>JournaledHandler&lt;'Actor,'Key,'State,'Event,'Argument,'Reply&gt;</c>.</param>
    static member Create<'Actor, 'Key, 'State, 'Event, 'Argument, 'Reply>(handler: obj) : FunctionalServerAdapter =
        let typed =
            unbox<JournaledHandler<'Actor, 'Key, 'State, 'Event, 'Argument, 'Reply>> handler

        FunctionalServerAdapter(fun invocation ->
            let argument = invocation.Codec.Deserialize<'Argument> invocation.Payload

            task {
                let context =
                    FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> invocation.Key, invocation.Core)

                let! events, reply = typed context (unbox<'State> invocation.State) argument
                let payload = invocation.Codec.Serialize<'Reply> reply
                return box (events |> List.map box), payload
            })

/// <summary>Preclosing of the journaled server-adapter factory.</summary>
[<RequireQualifiedAccess>]
module internal JournaledServerAdapter =

    let private createMethod =
        match
            typeof<JournaledServerAdapterFactory>
                .GetMethod("Create", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        with
        | null ->
            fail
                DefinitionStage
                "the journaled server-adapter factory 'JournaledServerAdapterFactory.Create' was not found."
        | method -> method

    /// <summary>Close the journaled server-adapter factory over one operation's exact types.</summary>
    /// <param name="actorType">The actor brand CLR type.</param>
    /// <param name="keyType">The domain key CLR type.</param>
    /// <param name="stateType">The definition's declared confirmed-state CLR type.</param>
    /// <param name="eventType">The definition's declared event CLR type.</param>
    /// <param name="argumentType">The operation's exact argument type.</param>
    /// <param name="replyType">The operation's exact reply type.</param>
    /// <param name="handler">The boxed application handler.</param>
    let precompute
        (actorType: Type)
        (keyType: Type)
        (stateType: Type)
        (eventType: Type)
        (argumentType: Type)
        (replyType: Type)
        (handler: obj)
        : FunctionalServerAdapter =
        FunctionalInstrumentation.countGenericClosing ()

        let closed =
            createMethod.MakeGenericMethod [| actorType; keyType; stateType; eventType; argumentType; replyType |]

        (closed.CreateDelegate typeof<Func<obj, FunctionalServerAdapter>> :?> Func<obj, FunctionalServerAdapter>).Invoke
            handler

/// <summary>
/// The preclosed typed adapter of a journaled definition's <c>onActivate</c> hook: boxed key,
/// context core, boxed replayed state. It returns no replacement state.
/// </summary>
type internal FunctionalJournaledHookAdapter = delegate of obj * FunctionalContextCore * obj -> Task

/// <summary>The preclosed typed adapter of a journaled definition's <c>onDeactivate</c> hook.</summary>
type internal FunctionalJournaledDeactivateAdapter =
    delegate of obj * FunctionalContextCore * DeactivationReason * obj -> Task

/// <summary>A preclosed synchronous tentative/confirmed-state notification.</summary>
type internal FunctionalJournalStateChangedAdapter = delegate of obj * FunctionalContextCore * obj -> unit

/// <summary>A preclosed synchronous log-consistency connection notification.</summary>
type internal FunctionalJournalConnectionIssueAdapter =
    delegate of obj * FunctionalContextCore * obj * ConnectionIssue -> unit

/// <summary>A boxed custom-storage read, after the definition's generic types were preclosed.</summary>
[<ReferenceEquality>]
type internal FunctionalJournalStorageReadData =
    { Snapshot: struct (int * obj) option
      Events: obj list }

/// <summary>A boxed custom-storage write, after the definition's generic types were preclosed.</summary>
[<ReferenceEquality>]
type internal FunctionalJournalStorageWriteData =
    { ExpectedVersion: int
      Events: obj list
      Snapshot: struct (int * obj) option }

/// <summary>The preclosed typed seam to one application custom-storage implementation.</summary>
[<ReferenceEquality>]
type internal FunctionalJournalStorageBlueprint =
    {
        Resolve: IServiceProvider -> obj
        Read: obj -> string -> GrainId -> obj -> Task<FunctionalJournalStorageReadData>
        Append: obj -> string -> GrainId -> obj -> FunctionalJournalStorageWriteData -> Task<bool>
        Clear: obj -> string -> GrainId -> obj -> Task
    }

/// <summary>The boxed per-definition snapshot rule consumed by the activation runtime.</summary>
[<NoEquality; NoComparison>]
type internal FunctionalJournalSnapshotRule =
    | InheritSnapshotRule
    | DisableSnapshotRule
    | EverySnapshotRule of int
    | ConditionalSnapshotRule of (int -> obj -> bool)

/// <summary>
/// Everything the silo side needs to host one definition's journal, with every generic already
/// closed at definition time.
/// </summary>
/// <remarks>
/// The four codec closures are what keeps the durable form of a journal free of CLR type names:
/// both the view and every entry are carried as bytes produced by the definition's <b>exact</b>
/// declared state and event types, which is the same byte boundary the transport puts between a
/// caller and a handler.
/// </remarks>
[<ReferenceEquality>]
type internal FunctionalJournalBlueprint =
    {
        /// The name of the registered <c>ILogViewAdaptorFactory</c> this journal lives in.
        ProviderName: string
        /// The named <c>IGrainStorage</c> the provider writes through, or the silo default.
        StorageName: string option
        /// The typed storage bridge used by Orleans' CustomStorage provider, when declared.
        CustomStorage: FunctionalJournalStorageBlueprint option
        /// Definition-level payload codec override. Absence inherits the silo journal default.
        CodecOverride: FunctionalPersistenceCodec option
        /// The per-definition snapshot rule, already closed over the exact state type.
        SnapshotRule: FunctionalJournalSnapshotRule
        /// The definition's declared state type.
        StateType: Type
        /// The definition's declared event type.
        EventType: Type
        /// The schema version written for materialized state and snapshot views.
        StateSchemaVersion: int
        /// The schema version written for journal entries.
        EventSchemaVersion: int
        /// The declared initial state for one boxed domain key.
        Initial: obj -> obj
        /// The replay fold over boxed state and boxed event.
        Apply: obj -> obj -> obj
        /// Serialize the boxed state as its exact declared type.
        EncodeState: FunctionalPersistenceCodec -> IFunctionalPayloadCodec -> obj -> byte[]
        /// Deserialize the boxed state as its exact declared type.
        DecodeState: FunctionalPersistenceCodec -> IFunctionalPayloadCodec -> int -> string -> byte[] -> obj
        /// Serialize a boxed event as its exact declared type.
        EncodeEvent: FunctionalPersistenceCodec -> IFunctionalPayloadCodec -> obj -> byte[]
        /// Deserialize a boxed event as its exact declared type.
        DecodeEvent: FunctionalPersistenceCodec -> IFunctionalPayloadCodec -> int -> string -> byte[] -> obj
        /// The preclosed activation hook, when the definition declares one.
        OnActivate: FunctionalJournaledHookAdapter option
        /// The preclosed deactivation hook, when the definition declares one.
        OnDeactivate: FunctionalJournaledDeactivateAdapter option
        /// The preclosed tentative-state notification hook, when declared.
        OnTentativeStateChanged: FunctionalJournalStateChangedAdapter option
        /// The preclosed confirmed-state notification hook, when declared.
        OnStateChanged: FunctionalJournalStateChangedAdapter option
        /// The preclosed connection-issue notification hook, when declared.
        OnConnectionIssue: FunctionalJournalConnectionIssueAdapter option
        /// The preclosed resolved-connection notification hook, when declared.
        OnConnectionIssueResolved: FunctionalJournalConnectionIssueAdapter option
    }

/// <summary>
/// The preclosed typed adapter of the functional <c>onActivate</c> hook. It receives the boxed
/// domain key, the invocation context core, and the boxed current primary state, and returns the
/// boxed replacement state, which is published in memory only.
/// </summary>
type internal FunctionalActivateAdapter = delegate of obj * FunctionalContextCore * obj -> Task<obj>

/// <summary>
/// The preclosed typed adapter of the functional <c>onDeactivate</c> hook. It returns no
/// replacement state.
/// </summary>
type internal FunctionalDeactivateAdapter = delegate of obj * FunctionalContextCore * DeactivationReason * obj -> Task

/// <summary>
/// The preclosed typed adapter of one declared reminder hook. Its boxed result is a replacement
/// state for an ordinary definition and an event list for a journaled definition; the reminder
/// context token is always <c>CancellationToken.None</c>.
/// </summary>
type internal FunctionalReminderAdapter = delegate of obj * FunctionalContextCore * obj * TickStatus -> Task<obj>

/// <summary>
/// The preclosed typed adapter of one declared timer hook. Its boxed result is a replacement state
/// for an ordinary definition and an event list for a journaled definition; the context token is
/// the one supplied by the Orleans timer callback.
/// </summary>
type internal FunctionalTimerAdapter = delegate of obj * FunctionalContextCore * obj -> Task<obj>

/// <summary>
/// The preclosed typed adapter of one declared <c>onLifecycle</c> hook. State-free by design (see
/// the <c>onLifecycle</c> custom operation's remarks); it neither receives nor returns state.
/// </summary>
type internal FunctionalLifecycleAdapter = delegate of obj * FunctionalContextCore -> Task

/// <summary>
/// The key-type-closed factory for one activation-scoped Orleans migration participant.
/// </summary>
type internal FunctionalMigrationParticipantFactoryAdapter =
    delegate of obj * GrainId * IServiceProvider * ILogger -> IGrainMigrationParticipant

/// <summary>One declared reminder frozen into the hosted view: identity plus its preclosed adapter.</summary>
[<ReferenceEquality>]
type internal FunctionalHostedReminder =
    {
        /// The durable reminder name.
        Name: string
        /// Explicit due time, validated non-negative at definition sealing.
        DueTime: TimeSpan
        /// Explicit period, validated strictly positive at definition sealing.
        Period: TimeSpan
        /// The preclosed typed reminder-hook adapter.
        Adapter: FunctionalReminderAdapter
    }

/// <summary>One declared timer frozen into the hosted view: identity plus its preclosed adapter.</summary>
[<ReferenceEquality>]
type internal FunctionalHostedTimer =
    {
        /// The timer name, unique within the definition.
        Name: string
        /// <c>GrainTimerCreationOptions.DueTime</c>, copied at sealing.
        DueTime: TimeSpan
        /// <c>GrainTimerCreationOptions.Period</c>, copied at sealing.
        Period: TimeSpan
        /// <c>GrainTimerCreationOptions.Interleave</c>; always <c>false</c> for a whole-state timer,
        /// and allowed for a journaled event-producing timer.
        Interleave: bool
        /// <c>GrainTimerCreationOptions.KeepAlive</c>, copied at sealing.
        KeepAlive: bool
        /// The preclosed typed timer-hook adapter.
        Adapter: FunctionalTimerAdapter
    }

/// <summary>One hosted operation: the immutable descriptor plus its preclosed server adapter.</summary>
[<ReferenceEquality>]
type internal FunctionalHostedOperation =
    {
        /// The stable ordinal wire operation ID.
        OperationId: string
        /// The source API-record field name.
        FieldName: string
        /// <summary>
        /// True when the API field returns <c>IAsyncEnumerable&lt;'Item&gt;</c>. Spec 004 item 6.
        /// Such an operation is reached only through <c>DispatchStream</c>, is state-neutral, and
        /// carries the two streaming protocol-token directions.
        /// </summary>
        IsStreaming: bool
        /// The precomputed admission-flag byte of this operation.
        AdmissionFlags: byte
        /// Orleans read-only scheduling.
        IsReadOnly: bool
        /// One-way delivery.
        IsOneWay: bool
        /// Always-interleave admission.
        IsAlwaysInterleave: bool
        /// The declared Orleans transaction option, when the operation is declared
        /// <c>transactional</c>.
        Transaction: Orleans.TransactionOption option
        /// True when Orleans creates or joins a transaction before this operation's handler runs
        /// -- the three options for which <c>TransactionRequestBase.IsTransactionRequired</c> is
        /// true. Such an operation is state-neutral: its replacement primary state is discarded
        /// and its persistent-state facades reject every mutation, because neither could be
        /// rolled back if the transaction aborts.
        IsTransactionScoped: bool
        /// The contract version this operation was introduced at.
        SinceVersion: int
        /// The lowest request version this definition admits; the index origin of
        /// <see cref="P:Orleans.FSharp.FunctionalHostedOperation.VersionTokens"/>.
        MinAcceptedVersion: int
        /// <summary>
        /// The request-direction and reply-direction protocol tokens of this operation at every
        /// admitted contract version, indexed by <c>version - MinAcceptedVersion</c>.
        /// </summary>
        /// <remarks>
        /// A protocol token is the digest of grain type, <b>version</b>, operation ID, and
        /// direction, so a caller at an older admitted version sends a different request token and
        /// checks the reply against a different reply token than a current-version caller. A
        /// version-tolerant host therefore cannot compare against one fixed pair: it has to answer
        /// in the caller's own version. Every admitted version's pair is precomputed here, once,
        /// while the definition is sealed -- which is only possible because the accepted set is a
        /// closed range and not a predicate.
        /// </remarks>
        VersionTokens: struct (byte[] * byte[])[]
        /// The operation's exact argument type.
        ArgumentType: Type
        /// The operation's exact reply type; the exact item type for a streaming operation.
        ReplyType: Type
        /// <summary>
        /// The preclosed typed handler adapter of a unary operation, and <c>null</c> for a
        /// streaming one.
        /// </summary>
        Adapter: FunctionalServerAdapter
        /// <summary>
        /// The preclosed typed handler adapter of a streaming operation, and <c>null</c> for a
        /// unary one. Exactly one of the two is non-null, decided by
        /// <see cref="P:Orleans.FSharp.FunctionalHostedOperation.IsStreaming"/> while the
        /// definition was sealed.
        /// </summary>
        StreamAdapter: FunctionalStreamServerAdapter
    }

    /// <summary>The protocol-token pair this operation uses for one admitted request version.</summary>
    /// <param name="version">The admitted request version to look up.</param>
    member this.TokensFor(version: int) =
        this.VersionTokens.[version - this.MinAcceptedVersion]

    /// <summary>The request-direction protocol token at the hosted contract version.</summary>
    member this.RequestToken =
        let struct (request, _) = this.VersionTokens.[this.VersionTokens.Length - 1]
        request

    /// <summary>The reply-direction protocol token at the hosted contract version.</summary>
    member this.ReplyToken =
        let struct (_, reply) = this.VersionTokens.[this.VersionTokens.Length - 1]
        reply

/// <summary>
/// The non-generic view of one hosted functional definition. The silo registry, the manifest
/// providers, the activator, and target dispatch all work against this shape, so no silo-side
/// code has to close a generic per call.
/// </summary>
[<Sealed>]
type internal FunctionalHostedDefinition
    (
        source: obj,
        grainTypeName: string,
        version: int,
        acceptedVersions: VersionPolicy,
        isReentrant: bool,
        mayInterleave: (IFunctionalRequestMetadata -> bool) option,
        actorType: Type,
        interfaceType: Type,
        interfaceId: string,
        apiType: Type,
        stateType: Type,
        operations: FunctionalHostedOperation[],
        decodeKey: GrainId -> obj,
        createState: obj -> obj,
        declaredTypes: (string * Type * Type)[],
        primaryFacet: FunctionalFacetBlueprint option,
        additionalFacets: FunctionalFacetBlueprint[],
        transactionalFacets: FunctionalTransactionalBlueprint[],
        onActivate: FunctionalActivateAdapter option,
        onDeactivate: FunctionalDeactivateAdapter option,
        collectionAge: TimeSpan option,
        reminders: FunctionalHostedReminder[],
        timers: FunctionalHostedTimer[],
        streamBindings: FunctionalStreamDeclaration[],
        placement: PlacementConfiguration option,
        migrationParticipants: FunctionalMigrationParticipantFactoryAdapter[],
        lifecycleHooks: (LifecycleStage * FunctionalLifecycleAdapter)[],
        journal: FunctionalJournalBlueprint option
    ) =

    let facets =
        [| match primaryFacet with
           | Some primary -> yield primary
           | None -> ()
           yield! additionalFacets |]

    let byId =
        let map = Dictionary<string, FunctionalHostedOperation>(StringComparer.Ordinal)

        for operation in operations do
            map.[operation.OperationId] <- operation

        map

    /// <summary>The definition value this hosted view was built from; registration identity.</summary>
    member _.Source = source

    /// <summary>The explicit Orleans grain type name.</summary>
    member _.GrainTypeName = grainTypeName

    /// <summary>The application contract version this silo hosts.</summary>
    member _.Version = version

    /// <summary>Which request versions this definition admits.</summary>
    member _.AcceptedVersions = acceptedVersions

    /// <summary>The lowest request version this definition admits.</summary>
    member _.MinAcceptedVersion =
        match acceptedVersions with
        | Exact -> version
        | BackwardCompatible minVersion -> minVersion

    /// <summary>True when the whole grain is reentrant.</summary>
    member _.IsReentrant = isReentrant

    /// <summary>The declared per-request interleave predicate, when the contract declares one.</summary>
    member _.MayInterleave = mayInterleave

    /// <summary>True when this definition admits a request carrying that contract version.</summary>
    /// <param name="requestVersion">The contract version the incoming request carries.</param>
    member this.AcceptsVersion(requestVersion: int) =
        requestVersion >= this.MinAcceptedVersion && requestVersion <= version

    /// <summary>
    /// The rejection text for a request version this definition does not admit. Under the default
    /// <c>Exact</c> policy it is byte-for-byte the spec-003 sentence, so nothing that reads the
    /// diagnostic changes when version tolerance ships unused.
    /// </summary>
    /// <param name="requestVersion">The contract version the incoming request carries.</param>
    member this.VersionRejection(requestVersion: int) =
        match acceptedVersions with
        | Exact ->
            $"grain type '{grainTypeName}' hosts contract version {version} but received version {requestVersion}."
        | BackwardCompatible minVersion ->
            $"grain type '{grainTypeName}' hosts contract version {version} and accepts versions {minVersion} through {version}, but received version {requestVersion}."

    /// <summary>The actor-brand CLR type.</summary>
    member _.ActorType = actorType

    /// <summary>The closed actor-specific Orleans target interface.</summary>
    member _.InterfaceType = interfaceType

    /// <summary>The reserved functional interface ID of this grain type.</summary>
    member _.InterfaceId = interfaceId

    /// <summary>The API record CLR type.</summary>
    member _.ApiType = apiType

    /// <summary>The definition's primary state CLR type.</summary>
    member _.StateType = stateType

    /// <summary>Hosted operations in API-record declaration order.</summary>
    member _.Operations = operations

    /// <summary>Operation ID, argument type, and reply type of every hosted operation.</summary>
    member _.DeclaredTypes = declaredTypes

    /// <summary>Look up a hosted operation by its ordinal wire ID.</summary>
    /// <param name="operationId">The stable wire operation ID to find.</param>
    member _.TryFindOperation(operationId: string) =
        match byId.TryGetValue operationId with
        | true, operation -> Some operation
        | _ -> None

    /// <summary>Decode the boxed domain key from an Orleans grain identity.</summary>
    /// <param name="grainId">The Orleans grain identity to decode.</param>
    member _.DecodeKey(grainId: GrainId) = decodeKey grainId

    /// <summary>Create the initial primary state of one activation from its boxed domain key.</summary>
    /// <param name="key">The boxed domain key.</param>
    member _.CreateState(key: obj) = createState key

    /// <summary>
    /// The primary persistent holder when <c>stateFrom</c> is configured. Its
    /// <c>IPersistentState.State</c> is then the authoritative in-memory primary state.
    /// </summary>
    member _.PrimaryFacet = primaryFacet

    /// <summary>Every attached facet: the primary one first, then the additional ones.</summary>
    member _.Facets = facets

    /// <summary>Attached transactional facets in declaration order.</summary>
    member _.TransactionalFacets = transactionalFacets

    /// <summary>The preclosed activation-hook adapter, when the definition declares one.</summary>
    member _.OnActivate = onActivate

    /// <summary>The preclosed deactivation-hook adapter, when the definition declares one.</summary>
    member _.OnDeactivate = onDeactivate

    /// <summary>The configured idle collection age, frozen into manifest properties when present.</summary>
    member _.CollectionAge = collectionAge

    /// <summary>Declared reminders in declaration order.</summary>
    member _.Reminders = reminders

    /// <summary>Declared timers in declaration order.</summary>
    member _.Timers = timers

    /// <summary>
    /// Declared implicit stream and broadcast subscriptions in declaration order. Already
    /// preclosed over their item types at definition time, so the silo side never closes a
    /// generic for one.
    /// </summary>
    member _.StreamBindings = streamBindings

    /// <summary>
    /// The declared subscription for one delivery, matched on transport, provider name, and
    /// namespace, all by ordinal equality. Orleans' implicit-subscription binding names a
    /// namespace but not a provider, so a namespace declared for one provider can still be
    /// routed here from another; such a delivery matches nothing and returns <c>None</c>.
    /// </summary>
    /// <param name="isStream">True to match an Orleans-streams (<c>onStream</c>) binding; false for a broadcast-channel (<c>onBroadcast</c>) one.</param>
    /// <param name="providerName">The named provider the delivery arrived on.</param>
    /// <param name="streamNamespace">The stream or channel namespace the delivery arrived on.</param>
    member _.TryFindStreamBinding(isStream: bool, providerName: string, streamNamespace: string) =
        streamBindings
        |> Array.tryFind (fun binding ->
            binding.IsStream = isStream
            && String.Equals(binding.ProviderName, providerName, StringComparison.Ordinal)
            && String.Equals(binding.Namespace, streamNamespace, StringComparison.Ordinal))

    /// <summary>The configured placement, when <c>statelessWorker</c> or <c>placement</c> was
    /// declared.</summary>
    member _.Placement = placement

    /// <summary>Activation-scoped migration-participant factories in declaration order.</summary>
    member _.MigrationParticipants = migrationParticipants

    /// <summary>Declared <c>onLifecycle</c> hooks with their preclosed adapters.</summary>
    member _.LifecycleHooks = lifecycleHooks

    /// <summary>
    /// The journal of a <c>journaledGrainFor</c> definition, and <c>None</c> for every ordinary
    /// <c>grainFor</c> one. Its presence is what makes an activation install a log-view adaptor,
    /// read its state from the journal instead of from an in-memory cell, and treat a handler's
    /// returned value as events rather than as a replacement state.
    /// </summary>
    member _.Journal = journal

    /// <summary>Look up a declared reminder by its exact ordinal name.</summary>
    /// <param name="reminderName">The durable reminder name to find.</param>
    member _.TryFindReminder(reminderName: string) =
        reminders
        |> Array.tryFind (fun reminder -> String.Equals(reminder.Name, reminderName, StringComparison.Ordinal))

    override _.ToString() =
        $"FunctionalHostedDefinition(grainType = '{grainTypeName}', version = {version}, operations = {operations.Length})"

/// <summary>Per-version protocol tokens of one hosted operation. Shared by both hosted builders.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalHostedTokens =

    /// <summary>
    /// One token pair per admitted version, in ascending version order, so the last entry is always
    /// the hosted contract version's own pair. A streaming operation gets the
    /// <c>stream-request</c>/<c>stream-item</c> directions; every other operation gets
    /// <c>request</c>/<c>reply</c>, byte-for-byte what spec 003 precomputed.
    /// </summary>
    /// <param name="grainTypeName">The hosted grain type name.</param>
    /// <param name="operation">The operation to precompute tokens for.</param>
    /// <param name="minAcceptedVersion">The lowest request version the hosting definition admits.</param>
    /// <param name="hostedVersion">The hosted contract version.</param>
    let forOperation
        (grainTypeName: string)
        (operation: FunctionalOperation)
        (minAcceptedVersion: int)
        (hostedVersion: int)
        : struct (byte[] * byte[])[] =
        let requestOf, replyOf =
            if operation.IsStreaming then
                ProtocolToken.streamRequest, ProtocolToken.streamItem
            else
                ProtocolToken.request, ProtocolToken.reply

        [| for candidate in minAcceptedVersion..hostedVersion ->
               struct (requestOf grainTypeName candidate operation.OperationId,
                       replyOf grainTypeName candidate operation.OperationId) |]

/// <summary>Construction of the non-generic hosted view of a sealed definition.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalHosted =

    /// <summary>Build the silo-side view of one sealed definition, preclosing every adapter.</summary>
    /// <param name="definition">The sealed definition to build a hosted view of.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when <paramref name="definition"/> is null.</exception>
    let create (definition: FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>) : FunctionalHostedDefinition =
        if obj.ReferenceEquals(definition, null) then
            fail DefinitionStage "AddFunctionalGrain requires a sealed functional grain definition."

        let contract = definition.Contract
        let metadata = contract.TargetMetadata
        let minAcceptedVersion = contract.MinAcceptedVersion

        let operations =
            contract.Operations
            |> Array.map (fun operation ->
                let adapter =
                    if operation.IsStreaming then
                        Unchecked.defaultof<FunctionalServerAdapter>
                    else
                        ServerAdapter.precompute
                            typeof<'Actor>
                            typeof<'Key>
                            typeof<'State>
                            operation.ArgumentType
                            operation.ReplyType
                            (definition.HandlerFor operation)

                let streamAdapter =
                    if operation.IsStreaming then
                        StreamServerAdapter.precompute
                            typeof<'Actor>
                            typeof<'Key>
                            typeof<'State>
                            operation.ArgumentType
                            operation.ReplyType
                            (definition.HandlerFor operation)
                    else
                        Unchecked.defaultof<FunctionalStreamServerAdapter>

                // One token pair per admitted version, in ascending version order, so the last
                // entry is always the hosted contract version's own pair. Under the default
                // Exact policy this is a single-element array holding exactly the tokens spec
                // 003 precomputed. A streaming operation uses the two streaming directions in
                // exactly the same shape.
                let versionTokens =
                    FunctionalHostedTokens.forOperation
                        contract.GrainTypeName
                        operation
                        minAcceptedVersion
                        contract.Version

                { OperationId = operation.OperationId
                  FieldName = operation.FieldName
                  IsStreaming = operation.IsStreaming
                  AdmissionFlags = operation.AdmissionFlags
                  IsReadOnly = operation.IsReadOnly
                  IsOneWay = operation.IsOneWay
                  IsAlwaysInterleave = operation.IsAlwaysInterleave
                  Transaction = operation.Transaction
                  IsTransactionScoped = operation.IsTransactionScoped
                  SinceVersion = operation.SinceVersion
                  MinAcceptedVersion = minAcceptedVersion
                  VersionTokens = versionTokens
                  ArgumentType = operation.ArgumentType
                  ReplyType = operation.ReplyType
                  Adapter = adapter
                  StreamAdapter = streamAdapter })

        // The primary facet blueprint closes over 'State here; the additional ones were closed
        // over their own stored types by 'usePersistentState'.
        let primaryFacet =
            definition.Primary
            |> Option.map (fun reference ->
                FunctionalFacet.blueprint reference (fun key -> box (definition.Initializer(unbox<'Key> key))))
            |> Option.map (FunctionalFacet.withDefaultCodec definition.PersistenceCodec)

        let additionalFacets =
            definition.Additional
            |> List.map (FunctionalFacet.withDefaultCodec definition.PersistenceCodec)
            |> List.toArray

        let onActivate =
            definition.OnActivate
            |> Option.map (fun hook ->
                FunctionalActivateAdapter(fun key core state ->
                    task {
                        let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                        let! next = hook context (unbox<'State> state)
                        return box next
                    }))

        let onDeactivate =
            definition.OnDeactivate
            |> Option.map (fun hook ->
                FunctionalDeactivateAdapter(fun key core reason state ->
                    let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                    hook context reason (unbox<'State> state) :> Task))

        let reminders =
            definition.Reminders
            |> List.map (fun declaration ->
                let adapter =
                    FunctionalReminderAdapter(fun key core state tickStatus ->
                        task {
                            let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                            let! next = declaration.Hook context (unbox<'State> state) tickStatus
                            return box next
                        })

                { Name = declaration.Name
                  DueTime = declaration.DueTime
                  Period = declaration.Period
                  Adapter = adapter })
            |> List.toArray

        let timers =
            definition.Timers
            |> List.map (fun declaration ->
                let adapter =
                    FunctionalTimerAdapter(fun key core state ->
                        task {
                            let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                            let! next = declaration.Hook context (unbox<'State> state)
                            return box next
                        })

                { Name = declaration.Name
                  DueTime = declaration.DueTime
                  Period = declaration.Period
                  Interleave = declaration.Interleave
                  KeepAlive = declaration.KeepAlive
                  Adapter = adapter })
            |> List.toArray

        let lifecycleHooks =
            definition.LifecycleHooks
            |> Map.toArray
            |> Array.map (fun (stage, hook) ->
                let adapter =
                    FunctionalLifecycleAdapter(fun key core ->
                        let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                        hook context :> Task)

                stage, adapter)

        let migrationParticipants =
            definition.MigrationParticipants
            |> List.map (fun factory ->
                FunctionalMigrationParticipantFactoryAdapter(fun key grainId services logger ->
                    let context =
                        FunctionalActivationMigrationContext<'Actor, 'Key>(
                            unbox<'Key> key,
                            grainId,
                            services,
                            logger
                        )

                    factory context))
            |> List.toArray

        FunctionalHostedDefinition(
            box definition,
            contract.GrainTypeName,
            contract.Version,
            contract.AcceptedVersions,
            contract.IsReentrant,
            contract.MayInterleave,
            typeof<'Actor>,
            metadata.InterfaceType,
            metadata.InterfaceId,
            contract.ApiType,
            typeof<'State>,
            operations,
            (fun grainId -> box (contract.KeyOf grainId)),
            (fun key -> box (definition.Initializer(unbox<'Key> key))),
            contract.DeclaredTypes,
            primaryFacet,
            additionalFacets,
            List.toArray definition.TransactionalFacets,
            onActivate,
            onDeactivate,
            definition.CollectionAge,
            reminders,
            timers,
            List.toArray definition.StreamBindings,
            definition.Placement,
            migrationParticipants,
            lifecycleHooks,
            None
        )

/// <summary>Construction of the non-generic hosted view of a sealed journaled definition.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalJournaledHosted =

    /// <summary>Build the silo-side view of one sealed journaled definition.</summary>
    /// <param name="definition">The sealed journaled definition to build a hosted view of.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="definition"/> is null; also thrown, defensively, if the
    /// definition somehow has no 'logProvider', which sealing already rejects and this repeats as
    /// an unreachable guard.
    /// </exception>
    let create
        (definition: FunctionalJournaledGrainDefinition<'Actor, 'Key, 'Api, 'State, 'Event>)
        : FunctionalHostedDefinition =
        if obj.ReferenceEquals(definition, null) then
            fail DefinitionStage "AddFunctionalJournaledGrain requires a sealed functional journaled grain definition."

        let contract = definition.Contract
        let metadata = contract.TargetMetadata
        let minAcceptedVersion = contract.MinAcceptedVersion

        let operations =
            contract.Operations
            |> Array.map (fun operation ->
                let adapter =
                    if operation.IsStreaming then
                        Unchecked.defaultof<FunctionalServerAdapter>
                    else
                        JournaledServerAdapter.precompute
                            typeof<'Actor>
                            typeof<'Key>
                            typeof<'State>
                            typeof<'Event>
                            operation.ArgumentType
                            operation.ReplyType
                            (definition.HandlerFor operation)

                // A journaled definition's streaming handler has exactly the ordinary streaming
                // shape -- it raises no events, for the same reason it publishes no replacement
                // state -- so it uses the ordinary streaming adapter.
                let streamAdapter =
                    if operation.IsStreaming then
                        StreamServerAdapter.precompute
                            typeof<'Actor>
                            typeof<'Key>
                            typeof<'State>
                            operation.ArgumentType
                            operation.ReplyType
                            (definition.HandlerFor operation)
                    else
                        Unchecked.defaultof<FunctionalStreamServerAdapter>

                let versionTokens =
                    FunctionalHostedTokens.forOperation
                        contract.GrainTypeName
                        operation
                        minAcceptedVersion
                        contract.Version

                { OperationId = operation.OperationId
                  FieldName = operation.FieldName
                  IsStreaming = operation.IsStreaming
                  AdmissionFlags = operation.AdmissionFlags
                  IsReadOnly = operation.IsReadOnly
                  IsOneWay = operation.IsOneWay
                  IsAlwaysInterleave = operation.IsAlwaysInterleave
                  Transaction = operation.Transaction
                  IsTransactionScoped = operation.IsTransactionScoped
                  SinceVersion = operation.SinceVersion
                  MinAcceptedVersion = minAcceptedVersion
                  VersionTokens = versionTokens
                  ArgumentType = operation.ArgumentType
                  ReplyType = operation.ReplyType
                  Adapter = adapter
                  StreamAdapter = streamAdapter })

        let configuration =
            match definition.Journal with
            | Some journal -> journal
            | None ->
                // Unreachable: sealing rejects a journaled definition without 'logProvider'.
                fail
                    DefinitionStage
                    $"the journaled definition of grain type '{contract.GrainTypeName}' has no log-consistency provider."

        let onActivate =
            definition.OnActivate
            |> Option.map (fun hook ->
                FunctionalJournaledHookAdapter(fun key core state ->
                    let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                    hook context (unbox<'State> state) :> Task))

        let onDeactivate =
            definition.OnDeactivate
            |> Option.map (fun hook ->
                FunctionalJournaledDeactivateAdapter(fun key core reason state ->
                    let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                    hook context reason (unbox<'State> state) :> Task))

        let reminders =
            definition.Reminders
            |> List.map (fun declaration ->
                let adapter =
                    FunctionalReminderAdapter(fun key core state tickStatus ->
                        task {
                            let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                            let! events = declaration.Hook context (unbox<'State> state) tickStatus
                            return box (events |> List.map box)
                        })

                { Name = declaration.Name
                  DueTime = declaration.DueTime
                  Period = declaration.Period
                  Adapter = adapter })
            |> List.toArray

        let timers =
            definition.Timers
            |> List.map (fun declaration ->
                let adapter =
                    FunctionalTimerAdapter(fun key core state ->
                        task {
                            let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                            let! events = declaration.Hook context (unbox<'State> state)
                            return box (events |> List.map box)
                        })

                { Name = declaration.Name
                  DueTime = declaration.DueTime
                  Period = declaration.Period
                  Interleave = declaration.Interleave
                  KeepAlive = declaration.KeepAlive
                  Adapter = adapter })
            |> List.toArray

        let stateChanged hook =
            hook
            |> Option.map (fun callback ->
                FunctionalJournalStateChangedAdapter(fun key core state ->
                    let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                    callback context (unbox<'State> state)))

        let connectionChanged hook =
            hook
            |> Option.map (fun callback ->
                FunctionalJournalConnectionIssueAdapter(fun key core state issue ->
                    let context = FunctionalGrainContext<'Actor, 'Key>(unbox<'Key> key, core)
                    callback context (unbox<'State> state) issue))

        let customStorage =
            definition.CustomStorage
            |> Option.map (fun resolve ->
                let identity grainTypeName grainId key =
                    FunctionalJournalStorageIdentity<'Key>(grainTypeName, grainId, unbox<'Key> key)

                { Resolve =
                    fun services ->
                        let storage = resolve services

                        if isNull (box storage) then
                            fail
                                JournalStage
                                $"the 'customStorage' resolver of grain type '{contract.GrainTypeName}' returned null."

                        box storage
                  Read =
                    fun instance grainTypeName grainId key ->
                        task {
                            let storage = unbox<IFunctionalJournalStorage<'Key, 'State, 'Event>> instance
                            let! read = storage.Read(identity grainTypeName grainId key)

                            if obj.ReferenceEquals(read, null) then
                                fail
                                    JournalStage
                                    $"custom storage of grain type '{contract.GrainTypeName}' returned null from Read."

                            if isNull (box read.Events) then
                                fail
                                    JournalStage
                                    $"custom storage of grain type '{contract.GrainTypeName}' returned a null event tail from Read. Return an empty IReadOnlyList instead."

                            return
                                { Snapshot = read.Snapshot |> Option.map (fun value -> struct (value.Version, box value.State))
                                  Events = read.Events |> Seq.map box |> Seq.toList }
                        }
                  Append =
                    fun instance grainTypeName grainId key write ->
                        let storage = unbox<IFunctionalJournalStorage<'Key, 'State, 'Event>> instance

                        let events =
                            write.Events
                            |> List.map unbox<'Event>
                            |> List.toArray
                            :> IReadOnlyList<'Event>

                        let snapshot =
                            write.Snapshot
                            |> Option.map (fun struct (version, state) ->
                                { Version = version
                                  State = unbox<'State> state })

                        storage.Append(
                            identity grainTypeName grainId key,
                            { ExpectedVersion = write.ExpectedVersion
                              Events = events
                              Snapshot = snapshot }
                        )
                  Clear =
                    fun instance grainTypeName grainId key ->
                        let storage = unbox<IFunctionalJournalStorage<'Key, 'State, 'Event>> instance
                        storage.Clear(identity grainTypeName grainId key) })

        let snapshotRule =
            match definition.SnapshotPolicy with
            | None
            | Some FunctionalJournalSnapshotPolicy.Inherit -> InheritSnapshotRule
            | Some FunctionalJournalSnapshotPolicy.Disabled -> DisableSnapshotRule
            | Some(FunctionalJournalSnapshotPolicy.Every eventCount) -> EverySnapshotRule eventCount
            | Some(FunctionalJournalSnapshotPolicy.When predicate) ->
                ConditionalSnapshotRule(fun version state -> predicate version (unbox<'State> state))

        let migrationParticipants =
            definition.MigrationParticipants
            |> List.map (fun factory ->
                FunctionalMigrationParticipantFactoryAdapter(fun key grainId services logger ->
                    let context =
                        FunctionalActivationMigrationContext<'Actor, 'Key>(
                            unbox<'Key> key,
                            grainId,
                            services,
                            logger
                        )

                    factory context))
            |> List.toArray

        let blueprint =
            { ProviderName = configuration.ProviderName
              StorageName = configuration.StorageName
              CustomStorage = customStorage
              CodecOverride = definition.JournalCodec
              SnapshotRule = snapshotRule
              StateType = typeof<'State>
              EventType = typeof<'Event>
              StateSchemaVersion =
                definition.StateSchema |> Option.map _.CurrentVersion |> Option.defaultValue 0
              EventSchemaVersion =
                definition.EventSchema |> Option.map _.CurrentVersion |> Option.defaultValue 0
              Initial = fun key -> box (definition.Initial(unbox<'Key> key))
              Apply = fun state event -> box (definition.Apply (unbox<'State> state) (unbox<'Event> event))
              EncodeState =
                fun selected codec value ->
                    FunctionalPersistenceEncoding.encode<'State> selected codec (unbox<'State> value)
              DecodeState =
                fun selected codec storedSchemaVersion storedCodecId payload ->
                    match definition.StateSchema with
                    | Some schema ->
                        box (schema.Decode(selected, codec, storedSchemaVersion, storedCodecId, payload))
                    | None when storedSchemaVersion = 0 ->
                        box (FunctionalPersistenceEncoding.decode<'State> selected codec storedCodecId payload)
                    | None ->
                        raise (
                            InvalidOperationException(
                                $"Functional journal state has schema version {storedSchemaVersion}, but grain type '{contract.GrainTypeName}' declares no 'stateSchema'. Restore that schema pipeline before reading the durable view."
                            )
                        )
              EncodeEvent =
                fun selected codec value ->
                    FunctionalPersistenceEncoding.encode<'Event> selected codec (unbox<'Event> value)
              DecodeEvent =
                fun selected codec storedSchemaVersion storedCodecId payload ->
                    match definition.EventSchema with
                    | Some schema ->
                        box (schema.Decode(selected, codec, storedSchemaVersion, storedCodecId, payload))
                    | None when storedSchemaVersion = 0 ->
                        box (FunctionalPersistenceEncoding.decode<'Event> selected codec storedCodecId payload)
                    | None ->
                        raise (
                            InvalidOperationException(
                                $"Functional journal event has schema version {storedSchemaVersion}, but grain type '{contract.GrainTypeName}' declares no 'eventSchema'. Restore that schema pipeline before replaying the journal."
                            )
                        )
              OnActivate = onActivate
              OnDeactivate = onDeactivate
              OnTentativeStateChanged = stateChanged definition.OnTentativeStateChanged
              OnStateChanged = stateChanged definition.OnStateChanged
              OnConnectionIssue = connectionChanged definition.OnConnectionIssue
              OnConnectionIssueResolved = connectionChanged definition.OnConnectionIssueResolved }

        FunctionalHostedDefinition(
            box definition,
            contract.GrainTypeName,
            contract.Version,
            contract.AcceptedVersions,
            contract.IsReentrant,
            contract.MayInterleave,
            typeof<'Actor>,
            metadata.InterfaceType,
            metadata.InterfaceId,
            contract.ApiType,
            typeof<'State>,
            operations,
            (fun grainId -> box (contract.KeyOf grainId)),
            (fun key -> box (definition.Initial(unbox<'Key> key))),
            contract.DeclaredTypes,
            None,
            Array.empty,
            Array.empty,
            None,
            None,
            definition.CollectionAge,
            reminders,
            timers,
            List.toArray definition.StreamBindings,
            definition.Placement,
            migrationParticipants,
            Array.empty,
            Some blueprint
        )
