namespace Orleans.FSharp

open System
open System.Collections.Generic
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// Which contract versions a hosted definition admits. A closed set rather than a predicate:
/// every accepted version needs its own precomputed pair of protocol tokens (a token is the
/// digest of grain type, <b>version</b>, operation ID, and direction), and a predicate's accepted
/// set is unbounded, so it could not be precomputed at all — the host would have to hash per
/// call. It is also what makes the diagnostic able to name the accepted range.
/// </summary>
/// <remarks>
/// Accepting a version <b>asserts wire compatibility of the argument and reply shapes</b> across
/// the accepted range. Nothing in the runtime converts between shapes: the payload is
/// deserialized as the hosted definition's exact declared CLR type whatever version admitted it.
/// A version this policy accepts must therefore be one whose argument and reply types the hosted
/// definition can still read — that is the application's responsibility, and there is no magic.
/// </remarks>
type VersionPolicy =
    /// <summary>Admit only the hosted contract version. The default, and spec-003 behaviour.</summary>
    | Exact
    /// <summary>
    /// Admit every version from <c>minVersion</c> up to and including the hosted contract
    /// version.
    /// </summary>
    | BackwardCompatible of minVersion: int

/// <summary>
/// An immutable operation descriptor sealed by contract construction. Later phases attach
/// protocol tokens and preclosed typed adapters to the same descriptor identity.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalOperation =
    {
        /// Zero-based API-record field index; also the descriptor's declaration order.
        Index: int
        /// The source record-field name.
        FieldName: string
        /// The stable wire operation ID.
        OperationId: string
        /// <summary>
        /// True when the API field returns <c>IAsyncEnumerable&lt;'Item&gt;</c> rather than
        /// <c>Task&lt;'Reply&gt;</c>. Spec 004 item 6. A streaming operation rides Orleans' own
        /// <c>IAsyncEnumerableGrainExtension</c> instead of the unary request path, so its protocol
        /// tokens carry the two streaming directions and its admission byte is always zero.
        /// </summary>
        IsStreaming: bool
        /// The field's exact CLR function type.
        FunctionType: Type
        /// The operation's exact argument type.
        ArgumentType: Type
        /// The operation's exact reply type.
        ReplyType: Type
        /// Orleans read-only scheduling.
        IsReadOnly: bool
        /// One-way delivery; the bound task acknowledges the local send only.
        IsOneWay: bool
        /// Always-interleave admission; valid only with read-only or one-way.
        IsAlwaysInterleave: bool
        /// The declared Orleans transaction option, when the operation is declared
        /// <c>transactional</c>.
        Transaction: Orleans.TransactionOption option
        /// The contract version this operation was introduced at; <c>1</c> unless declared.
        SinceVersion: int
        /// The precomputed request-direction protocol token.
        RequestToken: byte[]
        /// The precomputed reply-direction protocol token.
        ReplyToken: byte[]
        /// The precomputed admission-flag byte carried by every request for this operation.
        AdmissionFlags: byte
        /// The typed client-closure factory, closed over this operation's exact argument and
        /// reply types while the contract was sealed.
        ClosureFactory: Func<FunctionalCallSite, BoundCall>
    }

    /// <summary>
    /// True when this operation runs <b>inside</b> a transaction: the three options for which
    /// Orleans' own <c>TransactionRequestBase.IsTransactionRequired</c> is true, so a transaction
    /// is created or joined before the handler runs and the reply carries the participant set.
    /// </summary>
    /// <remarks>
    /// <c>Supported</c> is deliberately not in this set. Orleans forwards an ambient transaction
    /// context to a <c>Supported</c> call but starts none, so whether such a call has a
    /// transaction is a run-time property of its caller, not a declaration — which is exactly why
    /// the state-neutrality rule (a transaction-scoped operation publishes no primary state and
    /// gets read-only persistent facades) applies to the three declared options and not to it.
    /// </remarks>
    member this.IsTransactionScoped =
        match this.Transaction with
        | Some Orleans.TransactionOption.Create
        | Some Orleans.TransactionOption.CreateOrJoin
        | Some Orleans.TransactionOption.Join -> true
        | _ -> false

    /// <summary>
    /// True when this operation can see a transaction context at all — the transaction-scoped
    /// options plus <c>Supported</c>, which forwards a caller's context without starting one.
    /// </summary>
    member this.CanCarryTransaction =
        this.IsTransactionScoped || this.Transaction = Some Orleans.TransactionOption.Supported

/// <summary>
/// The non-generic view of a sealed contract.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="T:Orleans.FSharp.GrainContract`3"/> is the only type that derives from this one, and
/// its constructor is internal, so a value of this type is always a sealed contract. The base
/// exists so a caller that cannot name the three type parameters can still take a contract as a
/// typed parameter: C# has no partial type-argument inference, so
/// <c>FunctionalGrainInterop.For&lt;IChatRoom&gt;(contract, factory, key)</c> can only compile when
/// every parameter type is inferable or non-generic. Everything the facade needs before it knows
/// the type parameters -- the key type it must check a boxed key against, the operation
/// descriptors it maps interface members onto -- is readable here.
/// </para>
/// </remarks>
[<AbstractClass>]
type FunctionalContract
    internal (grainTypeName: string, keyType: Type, shape: ApiShape, operations: FunctionalOperation[]) =

    /// <summary>
    /// The contract's Orleans grain type name -- either the explicit <c>grainType</c> value, or,
    /// when omitted, the actor brand's CLR simple name.
    /// </summary>
    member internal _.GrainTypeName = grainTypeName

    /// <summary>The contract's exact domain key type.</summary>
    member internal _.KeyType = keyType

    /// <summary>The cached reflected API shape.</summary>
    member internal _.Shape = shape

    /// <summary>The API record CLR type.</summary>
    member internal _.ApiType = shape.ApiType

    /// <summary>Immutable operation descriptors in API-record declaration order.</summary>
    member internal _.Operations = operations

/// <summary>
/// A sealed contract: the reflected API shape, immutable metadata, the key codec, and one
/// immutable descriptor per API-record field in declaration order.
/// </summary>
[<Sealed>]
type GrainContract<'Actor, 'Key, 'Api>
    internal
    (
        grainTypeName: string,
        isGrainTypeExplicit: bool,
        version: int,
        acceptedVersions: VersionPolicy,
        isReentrant: bool,
        mayInterleave: (IFunctionalRequestMetadata -> bool) option,
        shape: ApiShape,
        keyCodec: KeyCodec<'Key>,
        operations: FunctionalOperation[]
    ) =
    inherit FunctionalContract(grainTypeName, typeof<'Key>, shape, operations)

    let grainType = GrainType.Create grainTypeName

    let byId =
        let map = Dictionary<string, FunctionalOperation>(StringComparer.Ordinal)

        for operation in operations do
            map.[operation.OperationId] <- operation

        map

    /// The closed actor-specific Orleans target metadata, constructed once per contract.
    let targetMetadata =
        lazy (FunctionalTarget.metadataFor typeof<'Actor> grainTypeName)

    /// The declared argument and reply types, in the shape the serializer preflight consumes.
    let declaredTypes =
        operations
        |> Array.map (fun operation -> operation.OperationId, operation.ArgumentType, operation.ReplyType)

    /// <summary>
    /// <c>true</c> when the contract declared an explicit <c>grainType</c>; <c>false</c> when it
    /// was derived from the actor brand's CLR simple name. A definition may attach durable state
    /// (<c>stateFrom</c>, <c>usePersistentState</c>) or declare <c>onReminder</c> only when this
    /// is <c>true</c> -- a derived grain type moves silently if the brand is ever renamed, which
    /// would orphan persisted state or lose durable reminders.
    /// </summary>
    member internal _.IsGrainTypeExplicit = isGrainTypeExplicit

    /// <summary>The application contract version carried in every request.</summary>
    member internal _.Version = version

    /// <summary>Which request versions a definition hosting this contract admits.</summary>
    member internal _.AcceptedVersions = acceptedVersions

    /// <summary>
    /// The lowest request version a definition hosting this contract admits: the contract version
    /// itself under <c>Exact</c>, the declared floor under <c>BackwardCompatible</c>.
    /// </summary>
    member internal _.MinAcceptedVersion =
        match acceptedVersions with
        | Exact -> version
        | BackwardCompatible minVersion -> minVersion

    /// <summary>True when the whole grain was declared <c>reentrant</c>.</summary>
    member internal _.IsReentrant = isReentrant

    /// <summary>The declared <c>mayInterleave</c> predicate, when the contract declares one.</summary>
    member internal _.MayInterleave = mayInterleave

    /// <summary>The configured key codec.</summary>
    member internal _.KeyCodec = keyCodec

    /// <summary>The Orleans grain type value derived from the explicit grain type name.</summary>
    member internal _.GrainType = grainType

    /// <summary>
    /// The closed actor-specific Orleans target interface and its dispatch method, built once
    /// per contract and reused by every reference bound from it.
    /// </summary>
    member internal _.TargetMetadata = targetMetadata.Value

    /// <summary>Operation ID, argument type, and reply type of every operation.</summary>
    member internal _.DeclaredTypes = declaredTypes

    /// <summary>Encode a domain key into the exact Orleans grain identity of this contract.</summary>
    /// <param name="key">The domain key to encode.</param>
    member internal _.GrainIdOf(key: 'Key) = GrainId.Create(grainType, keyCodec.EncodeKey key)

    /// <summary>Decode the domain key from an Orleans grain identity.</summary>
    /// <param name="grainId">The Orleans grain identity to decode.</param>
    member internal _.KeyOf(grainId: GrainId) = keyCodec.DecodeKey grainId

    /// <summary>Look up a descriptor by its ordinal wire operation ID.</summary>
    /// <param name="operationId">The stable wire operation ID to find.</param>
    member internal _.TryFindOperation(operationId: string) =
        match byId.TryGetValue operationId with
        | true, operation -> Some operation
        | _ -> None

    /// <summary>Look up a descriptor by its source record-field name.</summary>
    /// <param name="fieldName">The source record-field name to find.</param>
    member internal _.TryFindField(fieldName: string) =
        operations |> Array.tryFind (fun operation -> operation.FieldName = fieldName)

    /// <summary>Resolve a selector to its descriptor, running it once against the probe record.</summary>
    /// <param name="entry">The custom operation's own name, used to phrase the diagnostic.</param>
    /// <param name="selector">The caller-supplied field projection to resolve.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not return
    /// one of this contract's own API field values.
    /// </exception>
    member internal _.Resolve(entry: string, selector: OperationSelector<'Api, 'Argument, 'Reply>) =
        let field = ApiShape.resolve shape entry selector
        operations.[field.Index]

    /// <summary>Resolve a streaming selector to its descriptor. Spec 004 item 6.</summary>
    /// <param name="entry">The custom operation's own name, used to phrase the diagnostic.</param>
    /// <param name="selector">The caller-supplied streaming field projection to resolve.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not return
    /// one of this contract's own API field values.
    /// </exception>
    member internal _.ResolveStream(entry: string, selector: StreamSelector<'Api, 'Argument, 'Item>) =
        let field = ApiShape.resolveStream shape entry selector
        operations.[field.Index]

    override _.ToString() =
        $"GrainContract(grainType = '{grainTypeName}', version = {version}, api = '{shape.ApiType.FullName}', operations = {operations.Length})"

/// <summary>Accumulated, not yet validated, contract configuration.</summary>
[<ReferenceEquality>]
type internal ContractDraftState<'Key> =
    { /// The reflected API shape for 'Api.
      Shape: ApiShape
      /// The explicit 'grainType' value, when declared.
      GrainTypeName: string option
      /// The explicit 'version' value, when declared; defaults to 1 at sealing.
      Version: int option
      /// The explicit 'acceptsVersions' policy, when declared; defaults to Exact at sealing.
      AcceptedVersions: VersionPolicy option
      /// Whether 'reentrant' has been declared.
      IsReentrant: bool
      /// The declared 'mayInterleave' predicate, when declared.
      MayInterleave: (IFunctionalRequestMetadata -> bool) option
      /// The installed key codec, when a key operation has been declared.
      KeyCodec: KeyCodec<'Key> option
      /// API-field indices declared 'readOnly'.
      ReadOnly: Set<int>
      /// API-field indices declared 'oneWay'.
      OneWay: Set<int>
      /// API-field indices declared 'alwaysInterleave'.
      AlwaysInterleave: Set<int>
      /// The declared Orleans transaction option, keyed by API-field index.
      Transactions: Map<int, Orleans.TransactionOption>
      /// The declared 'sinceVersion' floor, keyed by API-field index.
      SinceVersions: Map<int, int>
      /// The declared 'operationId' override, keyed by API-field index.
      OperationIds: Map<int, string> }

/// <summary>
/// The intermediate state of a <c>grainContract</c> computation expression.
/// Every custom operation returns a new draft; nothing is mutated in place.
/// </summary>
[<Sealed>]
type GrainContractDraft<'Actor, 'Key, 'Api> internal (state: ContractDraftState<'Key>) =

    /// <summary>The accumulated configuration.</summary>
    member internal _.State = state

/// <summary>Contract-draft helpers shared by the computation-expression builder.</summary>
module internal ContractDraft =

    /// <summary>Start an empty contract draft for 'Api.</summary>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown, on the first call for this 'Api, when it is not a valid API shape; see
    /// <see cref="M:Orleans.FSharp.ApiShape.ofType"/>.
    /// </exception>
    let create<'Actor, 'Key, 'Api> () =
        GrainContractDraft<'Actor, 'Key, 'Api>(
            { Shape = ApiShape.of'<'Api> ()
              GrainTypeName = None
              Version = None
              AcceptedVersions = None
              IsReentrant = false
              MayInterleave = None
              KeyCodec = None
              ReadOnly = Set.empty
              OneWay = Set.empty
              AlwaysInterleave = Set.empty
              Transactions = Map.empty
              SinceVersions = Map.empty
              OperationIds = Map.empty }
        )

    /// <summary>Wrap an accumulated draft state back into a draft value.</summary>
    /// <param name="state">The accumulated state to wrap.</param>
    let withState<'Actor, 'Key, 'Api> (state: ContractDraftState<'Key>) =
        GrainContractDraft<'Actor, 'Key, 'Api>(state)

    /// <summary>Install a key codec, rejecting a second key operation.</summary>
    /// <param name="operationName">The custom operation's own name, used to phrase the diagnostic.</param>
    /// <param name="codec">The key codec to install.</param>
    /// <param name="state">The accumulated draft state to extend.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when a key codec is already installed.</exception>
    let withKey (operationName: string) (codec: KeyCodec<'Key>) (state: ContractDraftState<'Key>) =
        match state.KeyCodec with
        | Some existing ->
            fail
                ContractStage
                $"'{operationName}' conflicts with '{existing.OperationName}'. Exactly one native or mapped key operation is required."
        | None -> { state with KeyCodec = Some codec }

    /// <summary>Add a policy to one field, rejecting a repeated policy of the same kind.</summary>
    /// <param name="policyName">The policy's own name, used to phrase the diagnostic.</param>
    /// <param name="current">The set of field indices this policy is already applied to.</param>
    /// <param name="operation">The field to add the policy to.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when this policy is already applied to <paramref name="operation"/>.</exception>
    let addPolicy (policyName: string) (current: Set<int>) (operation: ApiOperationShape) =
        if current.Contains operation.Index then
            fail ContractStage $"'{policyName}' is applied more than once to API field '{operation.FieldName}'."

        current.Add operation.Index

    /// <summary>
    /// Derive the default grain type name from the actor brand's CLR simple name, used when the
    /// contract omits an explicit 'grainType'. Only a simple, non-generic, non-nested brand
    /// qualifies -- a generic brand's <c>Name</c> carries a backtick arity suffix (for example
    /// <c>"CounterActor`1"</c>), and a brand nested in another type or in an F# <c>module</c>
    /// (every type a <c>module</c> declares is a CLR-nested type, unlike a <c>namespace</c>)
    /// carries a '+' separator in its qualified name. Either case must declare 'grainType'
    /// explicitly instead.
    /// </summary>
    /// <param name="actorType">The actor brand type to derive a grain type name from.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="actorType"/> is a generic type or a nested type.
    /// </exception>
    let private deriveGrainTypeName (actorType: Type) =
        if actorType.IsGenericType then
            fail
                ContractStage
                $"the actor brand '{actorType.FullName}' is a generic type, so its CLR name is not a simple non-generic name and cannot supply a derived 'grainType'. Declare an explicit 'grainType' for this contract."

        if actorType.IsNested then
            fail
                ContractStage
                $"the actor brand '{actorType.FullName}' is a nested type (declared inside another type or inside an F# 'module' rather than a 'namespace'), so its CLR name is not a simple name and cannot supply a derived 'grainType'. Declare an explicit 'grainType' for this contract."

        actorType.Name

    /// <summary>Seal a draft into an immutable contract.</summary>
    /// <param name="draft">The accumulated draft to validate and seal.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the draft fails sealing validation: an explicit or derived grain type name, or
    /// an explicit or defaulted operation ID, that fails the fixed transport's wire-text bounds; a
    /// 'acceptsVersions (BackwardCompatible n)' floor that is non-positive or above the contract
    /// version; both 'reentrant' and 'mayInterleave' declared; no key operation declared; a
    /// streaming field combined with 'oneWay', 'readOnly', 'alwaysInterleave', or 'transactional';
    /// 'oneWay' on a field that does not return <c>Task&lt;unit&gt;</c>, or combined with
    /// 'readOnly'; 'transactional' combined with 'oneWay' or 'alwaysInterleave';
    /// 'alwaysInterleave' without 'readOnly' or 'oneWay', or combined with a contract declared
    /// 'reentrant' or 'mayInterleave'; a 'sinceVersion' that is non-positive, above the contract
    /// version, or unable to ever reject a call given the accepted-versions floor; or two API
    /// fields sharing one operation ID.
    /// </exception>
    let run<'Actor, 'Key, 'Api> (draft: GrainContractDraft<'Actor, 'Key, 'Api>) : GrainContract<'Actor, 'Key, 'Api> =
        let state = draft.State

        let grainTypeName, isGrainTypeExplicit =
            match state.GrainTypeName with
            | Some value -> value, true
            | None -> deriveGrainTypeName typeof<'Actor>, false

        // Defence in depth for an explicit value (already checked when the 'grainType' custom
        // operation ran, but a draft can also be built directly through the internal state, as
        // the 'oneWay on a non-unit reply' test below does); the sole check for the derived path,
        // which never runs through that custom operation at all. A CLR simple name is not exempt
        // from the fixed transport's own bounds just because nobody typed it as a string literal.
        ensureWireText
            ContractStage
            (if isGrainTypeExplicit then
                 "'grainType'"
             else
                 $"the 'grainType' derived from actor brand '{typeof<'Actor>.FullName}'")
            grainTypeName

        let version = state.Version |> Option.defaultValue 1

        let acceptedVersions = state.AcceptedVersions |> Option.defaultValue Exact

        // A version floor above the hosted version admits nothing at all, and a floor at or below
        // zero is not a version. Both are checked here rather than in the custom operation so the
        // rule holds however 'acceptsVersions' and 'version' were ordered in the expression.
        let minAcceptedVersion =
            match acceptedVersions with
            | Exact -> version
            | BackwardCompatible minVersion ->
                if minVersion <= 0 then
                    fail
                        ContractStage
                        $"'acceptsVersions (BackwardCompatible {minVersion})' for grain type '{grainTypeName}' requires a positive minimum version."

                if minVersion > version then
                    fail
                        ContractStage
                        $"'acceptsVersions (BackwardCompatible {minVersion})' for grain type '{grainTypeName}' is above its own contract version {version}, so the contract would admit no request at all."

                minVersion

        // "Sealing: mutually exclusive with 'reentrant'." A whole-grain reentrant activation
        // interleaves every request unconditionally, so a predicate could only ever be consulted
        // to be ignored -- Orleans' ReentrantPredicate returns true before any other predicate is
        // reached (GrainCanInterleave.MayInterleave returns on the first true).
        if state.IsReentrant && state.MayInterleave.IsSome then
            fail
                ContractStage
                $"grain type '{grainTypeName}' declares both 'reentrant' and 'mayInterleave'. A reentrant activation interleaves every request unconditionally, so the predicate could never refuse one; declare exactly one of the two."

        let keyCodec =
            match state.KeyCodec with
            | None ->
                fail
                    ContractStage
                    $"the contract '{grainTypeName}' requires exactly one native or mapped key operation."
            | Some codec -> codec

        let operations =
            state.Shape.Operations
            |> Array.map (fun field ->
                let operationId =
                    state.OperationIds
                    |> Map.tryFind field.Index
                    |> Option.defaultValue field.FieldName

                // Defence in depth for an explicit override (see the 'grainType' comment above);
                // the sole check for the default, field-name-derived case, which never runs
                // through the 'operationId' custom operation at all. An F# double-backtick field
                // name can carry the same "unusual characters" a hand-written override can.
                ensureWireText
                    ContractStage
                    (if state.OperationIds.ContainsKey field.Index then
                         "'operationId'"
                     else
                         $"the operation ID defaulted from API field '{field.FieldName}' of '{grainTypeName}'")
                    operationId

                let isReadOnly = state.ReadOnly.Contains field.Index
                let isOneWay = state.OneWay.Contains field.Index
                let isAlwaysInterleave = state.AlwaysInterleave.Contains field.Index
                let transaction = state.Transactions |> Map.tryFind field.Index

                // Spec 004 item 6. A streaming operation composes with none of the four admission
                // policies, and every rejection below is a statement about a mechanism rather than
                // a matter of taste. The F# types already make all four unreachable from the
                // computation expression -- readOnly/oneWay/alwaysInterleave/transactional take an
                // OperationSelector, whose range is Task<'Reply>, so none of them accepts a
                // streaming field -- so these are defence in depth for a draft built directly.
                if field.IsStreaming then
                    if isOneWay then
                        fail
                            ContractStage
                            $"API field '{field.FieldName}' of '{grainTypeName}' combines 'oneWay' with a streaming reply. The stream IS the reply, so there is nothing left to deliver one-way."

                    if isReadOnly then
                        fail
                            ContractStage
                            $"API field '{field.FieldName}' of '{grainTypeName}' declares 'readOnly' on a streaming operation, where it can have no effect. The message that crosses the network is Orleans' own IAsyncEnumerableGrainExtension call, whose scheduling is fixed by the [AlwaysInterleave] on that interface, and a streaming handler is already state-neutral: it publishes no replacement state and its persistent facades reject every mutation."

                    if isAlwaysInterleave then
                        fail
                            ContractStage
                            $"API field '{field.FieldName}' of '{grainTypeName}' declares 'alwaysInterleave' on a streaming operation, where it can have no effect. Every message of a streaming enumeration -- StartEnumeration, MoveNext and DisposeAsync -- already carries Orleans' own [AlwaysInterleave], so the enumeration never becomes this activation's blocking request."

                    if transaction.IsSome then
                        fail
                            ContractStage
                            $"API field '{field.FieldName}' of '{grainTypeName}' combines 'transactional' with a streaming reply. A transaction is scoped to one call: Orleans reports the participant set back inside the TransactionResponse of that call, and a stream is many calls (one StartEnumeration and one MoveNext per batch) whose producer outlives all of them, so there is no call whose response could carry the participants and no boundary at which the transaction could commit."

                if isOneWay && field.ReplyType <> typeof<unit> then
                    fail
                        ContractStage
                        $"'oneWay' requires API field '{field.FieldName}' of '{grainTypeName}' to return Task<unit>, but it returns Task<{field.ReplyType.FullName}>."

                if isOneWay && isReadOnly then
                    fail
                        ContractStage
                        $"API field '{field.FieldName}' of '{grainTypeName}' combines 'oneWay' with 'readOnly', which is rejected."

                // Spec 004 item 2. A transactional call must be acknowledged: Orleans' transaction
                // machinery reports the participant set back to the caller inside a
                // TransactionResponse, and TransactionRequestBase's outgoing call filter joins it
                // into the caller's TransactionInfo when the call returns. A one-way send
                // completes at the local acknowledgement and has no response at all, so the
                // participants this call enlisted would never be reported and the transaction
                // could neither commit them nor abort them.
                if transaction.IsSome && isOneWay then
                    fail
                        ContractStage
                        $"API field '{field.FieldName}' of '{grainTypeName}' combines 'transactional' with 'oneWay'. A one-way call has no reply, so the participants it enlists are never reported back to the transaction and could neither be committed nor aborted."

                // Spec 004 item 2. Orleans admits an AlwaysInterleave message before it consults
                // any interleaving policy, so an always-interleaving transactional operation would
                // run concurrently with another turn of the same activation while both hold
                // transactional locks on the same states -- the lock-recursion and
                // broken-lock paths of ReaderWriterLock exist precisely for that shape. The
                // combination is rejected rather than left to fail at run time.
                if transaction.IsSome && isAlwaysInterleave then
                    fail
                        ContractStage
                        $"API field '{field.FieldName}' of '{grainTypeName}' combines 'transactional' with 'alwaysInterleave'. Orleans admits an always-interleave request before any interleaving policy is consulted, so two turns of this activation could hold transactional locks on the same states at once."

                if isAlwaysInterleave && not (isReadOnly || isOneWay) then
                    fail
                        ContractStage
                        $"API field '{field.FieldName}' of '{grainTypeName}' uses 'alwaysInterleave' without 'readOnly' or 'oneWay'."

                // Spec 004 item 5. A contract-level interleaving policy and the per-operation
                // 'alwaysInterleave' flag are decided in different places and in a fixed order:
                // Orleans' ActivationData.MayInvokeRequest returns true for an
                // InvokeMethodOptions.AlwaysInterleave message BEFORE it ever looks at the
                // GrainCanInterleave component both 'reentrant' and 'mayInterleave' install. So
                // the flag is an unconditional grant the contract-level policy can neither widen
                // (under 'reentrant' every request already interleaves) nor revoke (under
                // 'mayInterleave' the predicate is never consulted for it). Either combination is
                // an unambiguously dead declaration, and it is rejected here rather than left to
                // surprise the author at the first concurrent call.
                //
                // 'readOnly' and 'oneWay' are NOT rejected, because in this runtime neither is
                // only a scheduling flag: 'readOnly' also makes the invocation state-neutral (its
                // replacement state is discarded and its persistent-state facades reject the
                // setter -- FunctionalDispatch.dispatch), and 'oneWay' is a delivery mode with no
                // reply. Both keep their full meaning on a reentrant grain.
                if isAlwaysInterleave && state.IsReentrant then
                    fail
                        ContractStage
                        $"API field '{field.FieldName}' of '{grainTypeName}' uses 'alwaysInterleave' on a contract declared 'reentrant'. Every request to a reentrant activation already interleaves, so the flag adds nothing; remove it, or remove 'reentrant'."

                if isAlwaysInterleave && state.MayInterleave.IsSome then
                    fail
                        ContractStage
                        $"API field '{field.FieldName}' of '{grainTypeName}' uses 'alwaysInterleave' on a contract declared 'mayInterleave'. Orleans admits an always-interleave request before it consults any predicate, so the predicate could never refuse this operation; remove the flag, or decide this operation inside the predicate."

                // Spec 004 item 7. A 'sinceVersion' that is not strictly above the lowest admitted
                // version can never reject anything -- every admitted version is already at or
                // above it -- so it is a declaration with no effect, in a contract whose other
                // declarations all have one. Rejected here, naming the policy that made it dead.
                let sinceVersion =
                    match state.SinceVersions |> Map.tryFind field.Index with
                    | None -> 1
                    | Some declared ->
                        if declared <= 0 then
                            fail
                                ContractStage
                                $"'sinceVersion {declared}' on API field '{field.FieldName}' of '{grainTypeName}' must be a positive integer."

                        if declared > version then
                            fail
                                ContractStage
                                $"'sinceVersion {declared}' on API field '{field.FieldName}' of '{grainTypeName}' is above the contract version {version}, so the operation does not exist at the version this contract publishes."

                        if declared <= minAcceptedVersion then
                            let policy =
                                match acceptedVersions with
                                | Exact ->
                                    $"the default 'acceptsVersions Exact' policy admits version {version} only"
                                | BackwardCompatible floor ->
                                    $"'acceptsVersions (BackwardCompatible {floor})' admits versions {floor} through {version}"

                            fail
                                ContractStage
                                $"'sinceVersion {declared}' on API field '{field.FieldName}' of '{grainTypeName}' can never reject a call, because {policy}. Declare a lower 'acceptsVersions' floor, or remove 'sinceVersion'."

                        declared

                { Index = field.Index
                  FieldName = field.FieldName
                  OperationId = operationId
                  IsStreaming = field.IsStreaming
                  FunctionType = field.FunctionType
                  ArgumentType = field.ArgumentType
                  ReplyType = field.ReplyType
                  IsReadOnly = isReadOnly
                  IsOneWay = isOneWay
                  IsAlwaysInterleave = isAlwaysInterleave
                  Transaction = transaction
                  SinceVersion = sinceVersion
                  RequestToken =
                    if field.IsStreaming then
                        ProtocolToken.streamRequest grainTypeName version operationId
                    else
                        ProtocolToken.request grainTypeName version operationId
                  ReplyToken =
                    if field.IsStreaming then
                        ProtocolToken.streamItem grainTypeName version operationId
                    else
                        ProtocolToken.reply grainTypeName version operationId
                  AdmissionFlags = AdmissionFlags.compose isReadOnly isOneWay isAlwaysInterleave transaction
                  ClosureFactory =
                    if field.IsStreaming then
                        BoundClosure.precomputeStream field.ArgumentType field.ReplyType
                    else
                        BoundClosure.precompute field.ArgumentType field.ReplyType })

        let seen = Dictionary<string, string>(StringComparer.Ordinal)

        for operation in operations do
            match seen.TryGetValue operation.OperationId with
            | true, owner ->
                fail
                    ContractStage
                    $"operation ID '{operation.OperationId}' of '{grainTypeName}' is used by both API field '{owner}' and API field '{operation.FieldName}'."
            | _ -> seen.[operation.OperationId] <- operation.FieldName

        GrainContract<'Actor, 'Key, 'Api>(
            grainTypeName,
            isGrainTypeExplicit,
            version,
            acceptedVersions,
            state.IsReentrant,
            state.MayInterleave,
            state.Shape,
            keyCodec,
            operations
        )

/// <summary>
/// The <c>grainContract</c> computation expression: immutable contract metadata, exactly one
/// key operation, and per-field policy and operation-ID overrides.
/// </summary>
[<Sealed>]
type GrainContractBuilder<'Actor, 'Key, 'Api> internal () =

    /// <summary>Start an empty draft for the API record type.</summary>
    member _.Yield(_: unit) : GrainContractDraft<'Actor, 'Key, 'Api> = ContractDraft.create<'Actor, 'Key, 'Api> ()

    /// <summary>Validate and seal the draft into an immutable contract.</summary>
    /// <param name="draft">The accumulated draft to validate and seal.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when sealing validation fails; see
    /// <see cref="M:Orleans.FSharp.ContractDraft.run"/> for the complete list of checks.
    /// </exception>
    member _.Run(draft: GrainContractDraft<'Actor, 'Key, 'Api>) : GrainContract<'Actor, 'Key, 'Api> =
        ContractDraft.run draft

    /// <summary>Set the explicit Orleans grain type; required exactly once.</summary>
    /// <param name="value">The explicit Orleans grain type name.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="value"/> is blank, fails the fixed transport's wire-text
    /// bounds, or 'grainType' is already set.
    /// </exception>
    [<CustomOperation("grainType")>]
    member _.GrainType(state: GrainContractDraft<'Actor, 'Key, 'Api>, value: string) =
        if isBlank value then
            fail ContractStage "'grainType' requires a non-blank value."

        ensureWireText ContractStage "'grainType'" value

        match state.State.GrainTypeName with
        | Some existing -> fail ContractStage $"'grainType' is already set to '{existing}'; it is required exactly once."
        | None -> ContractDraft.withState<'Actor, 'Key, 'Api> { state.State with GrainTypeName = Some value }

    /// <summary>Set the application contract version; defaults to <c>1</c>.</summary>
    /// <param name="value">The application contract version; must be positive.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="value"/> is not positive, or 'version' is already set.
    /// </exception>
    [<CustomOperation("version")>]
    member _.Version(state: GrainContractDraft<'Actor, 'Key, 'Api>, value: int) =
        if value <= 0 then
            fail ContractStage $"'version' must be a positive integer, but {value} was supplied."

        match state.State.Version with
        | Some existing -> fail ContractStage $"'version' is already set to {existing}; it is allowed at most once."
        | None -> ContractDraft.withState<'Actor, 'Key, 'Api> { state.State with Version = Some value }

    /// <summary>
    /// Admit request versions other than the hosted contract version; defaults to
    /// <see cref="F:Orleans.FSharp.VersionPolicy.Exact"/>, which is spec-003 behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Admission only. The wire format, the storage identity, and the stable operation IDs are
    /// unchanged by this operation — an accepted older request is dispatched through exactly the
    /// same descriptor, with exactly the same admission flags, onto exactly the same grain
    /// identity as a current-version one.
    /// </para>
    /// <para>
    /// <b>Accepting a version asserts wire compatibility.</b> The argument payload is deserialized
    /// as this definition's exact declared CLR type no matter which version admitted it, and the
    /// reply is serialized the same way. Nothing converts between shapes and nothing inspects an
    /// older shape: declaring <c>BackwardCompatible n</c> is the application stating that every
    /// version from <c>n</c> upwards still sends and reads the same argument and reply types for
    /// every operation it can invoke. An operation whose shape did change needs a new operation
    /// (a new <c>operationId</c>), not a wider policy.
    /// </para>
    /// </remarks>
    /// <param name="policy">The version admission policy.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when 'acceptsVersions' is already set.</exception>
    [<CustomOperation("acceptsVersions")>]
    member _.AcceptsVersions(state: GrainContractDraft<'Actor, 'Key, 'Api>, policy: VersionPolicy) =
        match state.State.AcceptedVersions with
        | Some existing ->
            fail ContractStage $"'acceptsVersions' is already set to {existing}; it is allowed at most once."
        | None -> ContractDraft.withState<'Actor, 'Key, 'Api> { state.State with AcceptedVersions = Some policy }

    /// <summary>
    /// Declare the contract version one operation was introduced at, so a call admitted at an
    /// older version is refused for that operation by name.
    /// </summary>
    /// <remarks>
    /// Only meaningful together with an <c>acceptsVersions</c> floor below the declared value:
    /// sealing rejects a <c>sinceVersion</c> that no admitted version could ever fall below.
    /// </remarks>
    /// <param name="introducedAt">The contract version this operation was introduced at.</param>
    /// <param name="selector">The API field to declare a version floor for.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; or when 'sinceVersion' is already applied to that
    /// field.
    /// </exception>
    [<CustomOperation("sinceVersion")>]
    member _.SinceVersion<'Argument, 'Reply>
        (
            state: GrainContractDraft<'Actor, 'Key, 'Api>,
            introducedAt: int,
            selector: OperationSelector<'Api, 'Argument, 'Reply>
        ) =
        let operation = ApiShape.resolve state.State.Shape "sinceVersion" selector

        if state.State.SinceVersions.ContainsKey operation.Index then
            fail ContractStage $"'sinceVersion' is applied more than once to API field '{operation.FieldName}'."

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                SinceVersions = state.State.SinceVersions.Add(operation.Index, introducedAt) }

    /// <summary>
    /// Declare the contract version one <b>streaming</b> operation was introduced at. Spec 004
    /// item 6: same rule and same diagnostics as the unary overload; only the selector's range
    /// differs.
    /// </summary>
    /// <param name="introducedAt">The contract version this operation was introduced at.</param>
    /// <param name="selector">The streaming API field to declare a version floor for.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; or when 'sinceVersion' is already applied to that
    /// field.
    /// </exception>
    [<CustomOperation("sinceVersion")>]
    member _.SinceVersion<'Argument, 'Item>
        (
            state: GrainContractDraft<'Actor, 'Key, 'Api>,
            introducedAt: int,
            selector: StreamSelector<'Api, 'Argument, 'Item>
        ) =
        let operation = ApiShape.resolveStream state.State.Shape "sinceVersion" selector

        if state.State.SinceVersions.ContainsKey operation.Index then
            fail ContractStage $"'sinceVersion' is applied more than once to API field '{operation.FieldName}'."

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                SinceVersions = state.State.SinceVersions.Add(operation.Index, introducedAt) }

    /// <summary>
    /// Make the whole grain reentrant: every request may enter an activation that is already
    /// executing one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This publishes Orleans' own <c>reentrant</c> grain-type property, so the activation is
    /// reentrant in exactly the sense a <c>[Reentrant]</c> grain class is.
    /// </para>
    /// <para>
    /// <b>Whole-state replacement is not made concurrency-safe by this.</b> A handler receives the
    /// state as it was when it started and publishes its replacement when it returns, so two
    /// interleaved handlers that both mutate state produce a last-writer-wins result: the second
    /// one's replacement overwrites the first's. Reentrancy is for activations whose interleaving
    /// operations do not both write — a long call that awaits an external service while short
    /// reads continue, for instance. Declare the non-mutating operations <c>readOnly</c> so their
    /// replacement is discarded rather than published.
    /// </para>
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">Thrown when 'reentrant' is already declared.</exception>
    [<CustomOperation("reentrant")>]
    member _.Reentrant(state: GrainContractDraft<'Actor, 'Key, 'Api>) =
        if state.State.IsReentrant then
            fail ContractStage "'reentrant' is declared more than once; it is allowed at most once."

        ContractDraft.withState<'Actor, 'Key, 'Api> { state.State with IsReentrant = true }

    /// <summary>
    /// Decide per request whether it may enter an activation that is already executing one.
    /// </summary>
    /// <param name="predicate">
    /// Receives the request's <see cref="T:Orleans.FSharp.IFunctionalRequestMetadata"/> — grain
    /// type, contract version, operation ID, the three admission flags, and the payload length.
    /// <b>Metadata only:</b> the argument payload is never deserialized to decide admission, which
    /// is what keeps spec 003's protocol-before-payload invariant intact. The predicate runs on
    /// the activation's own scheduling path, so it must be cheap, pure, and non-blocking.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Orleans consults the predicate for the running request too.</b>
    /// <c>ActivationData.MayInvokeRequest</c> admits the incoming request when
    /// <c>predicate(incoming) || predicate(blocking)</c> — so an operation the predicate accepts
    /// also lets <i>anything</i> interleave with it while it is the one executing. Write the
    /// predicate as a statement about which operations are safe to overlap, not as a one-sided
    /// allow-list.
    /// </para>
    /// <para>
    /// A throwing predicate rejects the incoming request: Orleans logs the failure and rethrows,
    /// and the message is rejected to its caller as transient. The runtime wraps the fault in a
    /// transport-stage diagnostic naming the grain type and operation so it is attributable.
    /// </para>
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="predicate"/> is null, or 'mayInterleave' is already declared.
    /// </exception>
    [<CustomOperation("mayInterleave")>]
    member _.MayInterleave
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, predicate: IFunctionalRequestMetadata -> bool)
        =
        if obj.ReferenceEquals(predicate, null) then
            fail ContractStage "'mayInterleave' requires a predicate."

        match state.State.MayInterleave with
        | Some _ -> fail ContractStage "'mayInterleave' is declared more than once; it is allowed at most once."
        | None -> ContractDraft.withState<'Actor, 'Key, 'Api> { state.State with MayInterleave = Some predicate }

    /// <summary>Use the native Orleans string key.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("stringKey")>]
    member _.StringKey(state: GrainContractDraft<'Actor, string, 'Api>) =
        ContractDraft.withState<'Actor, string, 'Api> (ContractDraft.withKey "stringKey" KeyCodecs.stringKey state.State)

    /// <summary>Map a domain key onto the native Orleans string key.</summary>
    /// <param name="encode">Encodes the domain key as the native Orleans string key.</param>
    /// <param name="decode">Decodes the domain key from the native Orleans string key.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("stringKeyMapped")>]
    member _.StringKeyMapped
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, encode: 'Key -> string, decode: string -> 'Key)
        =
        ContractDraft.withState<'Actor, 'Key, 'Api> (
            ContractDraft.withKey "stringKeyMapped" (KeyCodecs.stringKeyMapped encode decode) state.State
        )

    /// <summary>Use the native Orleans Guid key.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("guidKey")>]
    member _.GuidKey(state: GrainContractDraft<'Actor, Guid, 'Api>) =
        ContractDraft.withState<'Actor, Guid, 'Api> (ContractDraft.withKey "guidKey" KeyCodecs.guidKey state.State)

    /// <summary>Map a domain key onto the native Orleans Guid key.</summary>
    /// <param name="encode">Encodes the domain key as the native Orleans Guid key.</param>
    /// <param name="decode">Decodes the domain key from the native Orleans Guid key.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("guidKeyMapped")>]
    member _.GuidKeyMapped(state: GrainContractDraft<'Actor, 'Key, 'Api>, encode: 'Key -> Guid, decode: Guid -> 'Key) =
        ContractDraft.withState<'Actor, 'Key, 'Api> (
            ContractDraft.withKey "guidKeyMapped" (KeyCodecs.guidKeyMapped encode decode) state.State
        )

    /// <summary>Use the native Orleans int64 key.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("int64Key")>]
    member _.Int64Key(state: GrainContractDraft<'Actor, int64, 'Api>) =
        ContractDraft.withState<'Actor, int64, 'Api> (ContractDraft.withKey "int64Key" KeyCodecs.int64Key state.State)

    /// <summary>Map a domain key onto the native Orleans int64 key.</summary>
    /// <param name="encode">Encodes the domain key as the native Orleans int64 key.</param>
    /// <param name="decode">Decodes the domain key from the native Orleans int64 key.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("int64KeyMapped")>]
    member _.Int64KeyMapped
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, encode: 'Key -> int64, decode: int64 -> 'Key)
        =
        ContractDraft.withState<'Actor, 'Key, 'Api> (
            ContractDraft.withKey "int64KeyMapped" (KeyCodecs.int64KeyMapped encode decode) state.State
        )

    /// <summary>Use the native Orleans Guid compound key.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("guidCompoundKey")>]
    member _.GuidCompoundKey(state: GrainContractDraft<'Actor, Guid * string, 'Api>) =
        ContractDraft.withState<'Actor, Guid * string, 'Api> (
            ContractDraft.withKey "guidCompoundKey" KeyCodecs.guidCompoundKey state.State
        )

    /// <summary>Map a domain key onto the native Orleans Guid compound key.</summary>
    /// <param name="encode">Encodes the domain key as the native Orleans Guid compound key.</param>
    /// <param name="decode">Decodes the domain key from the native Orleans Guid compound key.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("guidCompoundKeyMapped")>]
    member _.GuidCompoundKeyMapped
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, encode: 'Key -> Guid * string, decode: Guid -> string -> 'Key)
        =
        ContractDraft.withState<'Actor, 'Key, 'Api> (
            ContractDraft.withKey "guidCompoundKeyMapped" (KeyCodecs.guidCompoundKeyMapped encode decode) state.State
        )

    /// <summary>Use the native Orleans int64 compound key.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("int64CompoundKey")>]
    member _.Int64CompoundKey(state: GrainContractDraft<'Actor, int64 * string, 'Api>) =
        ContractDraft.withState<'Actor, int64 * string, 'Api> (
            ContractDraft.withKey "int64CompoundKey" KeyCodecs.int64CompoundKey state.State
        )

    /// <summary>Map a domain key onto the native Orleans int64 compound key.</summary>
    /// <param name="encode">Encodes the domain key as the native Orleans int64 compound key.</param>
    /// <param name="decode">Decodes the domain key from the native Orleans int64 compound key.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when a key operation is already installed.</exception>
    [<CustomOperation("int64CompoundKeyMapped")>]
    member _.Int64CompoundKeyMapped
        (
            state: GrainContractDraft<'Actor, 'Key, 'Api>,
            encode: 'Key -> int64 * string,
            decode: int64 -> string -> 'Key
        ) =
        ContractDraft.withState<'Actor, 'Key, 'Api> (
            ContractDraft.withKey "int64CompoundKeyMapped" (KeyCodecs.int64CompoundKeyMapped encode decode) state.State
        )

    /// <summary>Select Orleans read-only scheduling for one operation.</summary>
    /// <param name="selector">The API field to declare read-only.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; or when 'readOnly' is already applied to that field.
    /// </exception>
    [<CustomOperation("readOnly")>]
    member _.ReadOnly<'Argument, 'Reply>
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, selector: OperationSelector<'Api, 'Argument, 'Reply>)
        =
        let operation = ApiShape.resolve state.State.Shape "readOnly" selector

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                ReadOnly = ContractDraft.addPolicy "readOnly" state.State.ReadOnly operation }

    /// <summary>Select one-way delivery for one <c>Task&lt;unit&gt;</c> operation.</summary>
    /// <param name="selector">The API field to declare one-way.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; or when 'oneWay' is already applied to that field.
    /// </exception>
    [<CustomOperation("oneWay")>]
    member _.OneWay<'Argument>
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, selector: OperationSelector<'Api, 'Argument, unit>)
        =
        let operation = ApiShape.resolve state.State.Shape "oneWay" selector

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                OneWay = ContractDraft.addPolicy "oneWay" state.State.OneWay operation }

    /// <summary>Permit a read-only or one-way operation to interleave.</summary>
    /// <param name="selector">The API field to declare always-interleaving.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; or when 'alwaysInterleave' is already applied to
    /// that field.
    /// </exception>
    [<CustomOperation("alwaysInterleave")>]
    member _.AlwaysInterleave<'Argument, 'Reply>
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, selector: OperationSelector<'Api, 'Argument, 'Reply>)
        =
        let operation = ApiShape.resolve state.State.Shape "alwaysInterleave" selector

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                AlwaysInterleave = ContractDraft.addPolicy "alwaysInterleave" state.State.AlwaysInterleave operation }

    /// <summary>
    /// Run one operation under an Orleans distributed transaction, with the given
    /// <see cref="T:Orleans.TransactionOption"/>.
    /// </summary>
    /// <param name="option">
    /// Orleans' own option enum, used unchanged rather than mirrored into an F# union: it is the
    /// closed set, its six members and their numeric values are identical on Orleans 10.1.0 and
    /// 10.2.2, and the admission byte encodes the value directly, so there is no mapping to drift.
    /// Note that <c>Orleans.TransactionOption</c> (this one) and the legacy
    /// <c>Orleans.FSharp.Transactions.TransactionOption</c> union of the classic KEEP-path share a
    /// simple name; open only the one you mean.
    /// </param>
    /// <param name="selector">The API field to declare transactional.</param>
    /// <remarks>
    /// <para>
    /// <b>Create, CreateOrJoin and Join are transaction-scoped</b> — Orleans creates or joins a
    /// transaction before the handler runs. In such an operation the ONLY durable effect available
    /// is a <c>transactionalStateFrom</c> facet: the handler's replacement primary state is
    /// discarded exactly as a <c>readOnly</c> handler's is, and its persistent-state facades
    /// reject the setter and every storage call. Nothing can roll back an in-memory publication or
    /// a storage write when the transaction aborts, so allowing either would let one aborted call
    /// leave the activation half-updated.
    /// </para>
    /// <para>
    /// <b>Supported, Suppress and NotAllowed are not transaction-scoped.</b> They declare how the
    /// operation behaves towards a caller's ambient transaction — forward it, hide it, or refuse
    /// the call — and leave state publication and persistent facets exactly as they are for an
    /// ordinary operation.
    /// </para>
    /// <para>
    /// <b>readOnly composes.</b> A transaction started by a <c>readOnly</c> transactional
    /// operation is started read-only (<c>TransactionRequestBase.Invoke</c> passes
    /// <c>Options.HasFlag(InvokeMethodOptions.ReadOnly)</c> to
    /// <c>ITransactionAgent.StartTransaction</c>), and the transactional facade rejects every
    /// update.
    /// </para>
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="option"/> is not a defined <c>Orleans.TransactionOption</c>
    /// value; when <paramref name="selector"/> is null, invoking it throws, or it does not resolve
    /// to one of the contract's own API fields; or when 'transactional' is already applied to that
    /// field.
    /// </exception>
    [<CustomOperation("transactional")>]
    member _.Transactional<'Argument, 'Reply>
        (
            state: GrainContractDraft<'Actor, 'Key, 'Api>,
            option: Orleans.TransactionOption,
            selector: OperationSelector<'Api, 'Argument, 'Reply>
        ) =
        if not (Enum.IsDefined(typeof<Orleans.TransactionOption>, option)) then
            fail
                ContractStage
                $"'transactional' received the undefined Orleans.TransactionOption value {int option}."

        let operation = ApiShape.resolve state.State.Shape "transactional" selector

        if state.State.Transactions.ContainsKey operation.Index then
            fail ContractStage $"'transactional' is applied more than once to API field '{operation.FieldName}'."

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                Transactions = state.State.Transactions.Add(operation.Index, option) }

    /// <summary>Override the stable wire ID of one operation, keeping it across a field rename.</summary>
    /// <param name="stableWireId">The stable wire ID to use instead of the default field-name-derived one.</param>
    /// <param name="selector">The API field to override the wire ID of.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="stableWireId"/> is blank or fails the fixed transport's
    /// wire-text bounds; when <paramref name="selector"/> is null, invoking it throws, or it does
    /// not resolve to one of the contract's own API fields; or when 'operationId' is already
    /// applied to that field.
    /// </exception>
    [<CustomOperation("operationId")>]
    member _.OperationId<'Argument, 'Reply>
        (
            state: GrainContractDraft<'Actor, 'Key, 'Api>,
            stableWireId: string,
            selector: OperationSelector<'Api, 'Argument, 'Reply>
        ) =
        if isBlank stableWireId then
            fail ContractStage "'operationId' requires a non-blank wire ID."

        ensureWireText ContractStage "'operationId'" stableWireId

        let operation = ApiShape.resolve state.State.Shape "operationId" selector

        if state.State.OperationIds.ContainsKey operation.Index then
            fail
                ContractStage
                $"'operationId' is applied more than once to API field '{operation.FieldName}'."

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                OperationIds = state.State.OperationIds.Add(operation.Index, stableWireId) }

    /// <summary>
    /// Override the stable wire ID of one <b>streaming</b> operation. Spec 004 item 6: same rule
    /// and same diagnostics as the unary overload; only the selector's range differs.
    /// </summary>
    /// <param name="stableWireId">The stable wire ID to use instead of the default field-name-derived one.</param>
    /// <param name="selector">The streaming API field to override the wire ID of.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="stableWireId"/> is blank or fails the fixed transport's
    /// wire-text bounds; when <paramref name="selector"/> is null, invoking it throws, or it does
    /// not resolve to one of the contract's own API fields; or when 'operationId' is already
    /// applied to that field.
    /// </exception>
    [<CustomOperation("operationId")>]
    member _.OperationId<'Argument, 'Item>
        (
            state: GrainContractDraft<'Actor, 'Key, 'Api>,
            stableWireId: string,
            selector: StreamSelector<'Api, 'Argument, 'Item>
        ) =
        if isBlank stableWireId then
            fail ContractStage "'operationId' requires a non-blank wire ID."

        ensureWireText ContractStage "'operationId'" stableWireId

        let operation = ApiShape.resolveStream state.State.Shape "operationId" selector

        if state.State.OperationIds.ContainsKey operation.Index then
            fail
                ContractStage
                $"'operationId' is applied more than once to API field '{operation.FieldName}'."

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                OperationIds = state.State.OperationIds.Add(operation.Index, stableWireId) }
