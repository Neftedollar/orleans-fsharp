namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open Orleans
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

        (closed.CreateDelegate typeof<Func<obj, FunctionalServerAdapter>> :?> Func<obj, FunctionalServerAdapter>)
            .Invoke handler

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
type internal FunctionalDeactivateAdapter =
    delegate of obj * FunctionalContextCore * DeactivationReason * obj -> Task

/// <summary>One hosted operation: the immutable descriptor plus its preclosed server adapter.</summary>
[<ReferenceEquality>]
type internal FunctionalHostedOperation =
    {
        /// The stable ordinal wire operation ID.
        OperationId: string
        /// The source API-record field name.
        FieldName: string
        /// The precomputed request-direction protocol token.
        RequestToken: byte[]
        /// The precomputed reply-direction protocol token.
        ReplyToken: byte[]
        /// The precomputed admission-flag byte of this operation.
        AdmissionFlags: byte
        /// Orleans read-only scheduling.
        IsReadOnly: bool
        /// One-way delivery.
        IsOneWay: bool
        /// Always-interleave admission.
        IsAlwaysInterleave: bool
        /// The operation's exact argument type.
        ArgumentType: Type
        /// The operation's exact reply type.
        ReplyType: Type
        /// The preclosed typed handler adapter.
        Adapter: FunctionalServerAdapter
    }

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
        onActivate: FunctionalActivateAdapter option,
        onDeactivate: FunctionalDeactivateAdapter option
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
    member _.TryFindOperation(operationId: string) =
        match byId.TryGetValue operationId with
        | true, operation -> Some operation
        | _ -> None

    /// <summary>Decode the boxed domain key from an Orleans grain identity.</summary>
    member _.DecodeKey(grainId: GrainId) = decodeKey grainId

    /// <summary>Create the initial primary state of one activation from its boxed domain key.</summary>
    member _.CreateState(key: obj) = createState key

    /// <summary>
    /// The primary persistent holder when <c>stateFrom</c> is configured. Its
    /// <c>IPersistentState.State</c> is then the authoritative in-memory primary state.
    /// </summary>
    member _.PrimaryFacet = primaryFacet

    /// <summary>Every attached facet: the primary one first, then the additional ones.</summary>
    member _.Facets = facets

    /// <summary>The preclosed activation-hook adapter, when the definition declares one.</summary>
    member _.OnActivate = onActivate

    /// <summary>The preclosed deactivation-hook adapter, when the definition declares one.</summary>
    member _.OnDeactivate = onDeactivate

    override _.ToString() =
        $"FunctionalHostedDefinition(grainType = '{grainTypeName}', version = {version}, operations = {operations.Length})"

/// <summary>Construction of the non-generic hosted view of a sealed definition.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalHosted =

    /// <summary>Build the silo-side view of one sealed definition, preclosing every adapter.</summary>
    let create (definition: FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>) : FunctionalHostedDefinition =
        if obj.ReferenceEquals(definition, null) then
            fail DefinitionStage "AddFunctionalGrain requires a sealed functional grain definition."

        let contract = definition.Contract
        let metadata = contract.TargetMetadata

        let operations =
            contract.Operations
            |> Array.map (fun operation ->
                let adapter =
                    ServerAdapter.precompute
                        typeof<'Actor>
                        typeof<'Key>
                        typeof<'State>
                        operation.ArgumentType
                        operation.ReplyType
                        (definition.HandlerFor operation)

                { OperationId = operation.OperationId
                  FieldName = operation.FieldName
                  RequestToken = operation.RequestToken
                  ReplyToken = operation.ReplyToken
                  AdmissionFlags = operation.AdmissionFlags
                  IsReadOnly = operation.IsReadOnly
                  IsOneWay = operation.IsOneWay
                  IsAlwaysInterleave = operation.IsAlwaysInterleave
                  ArgumentType = operation.ArgumentType
                  ReplyType = operation.ReplyType
                  Adapter = adapter })

        // The primary facet blueprint closes over 'State here; the additional ones were closed
        // over their own stored types by 'usePersistentState'.
        let primaryFacet =
            definition.Primary
            |> Option.map (fun reference ->
                FunctionalFacet.blueprint reference (fun key -> box (definition.Initializer(unbox<'Key> key))))

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

        FunctionalHostedDefinition(
            box definition,
            contract.GrainTypeName,
            contract.Version,
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
            List.toArray definition.Additional,
            onActivate,
            onDeactivate
        )
