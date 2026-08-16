namespace Orleans.FSharp

open System
open System.Collections.Generic
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

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
    }

/// <summary>
/// A sealed contract: the reflected API shape, immutable metadata, the key codec, and one
/// immutable descriptor per API-record field in declaration order.
/// </summary>
[<Sealed>]
type GrainContract<'Actor, 'Key, 'Api>
    internal
    (
        grainTypeName: string,
        version: int,
        shape: ApiShape,
        keyCodec: KeyCodec<'Key>,
        operations: FunctionalOperation[]
    ) =

    let grainType = GrainType.Create grainTypeName

    let byId =
        let map = Dictionary<string, FunctionalOperation>(StringComparer.Ordinal)

        for operation in operations do
            map.[operation.OperationId] <- operation

        map

    /// <summary>The explicit Orleans grain type name.</summary>
    member internal _.GrainTypeName = grainTypeName

    /// <summary>The application contract version carried in every request.</summary>
    member internal _.Version = version

    /// <summary>The cached reflected API shape.</summary>
    member internal _.Shape = shape

    /// <summary>The API record CLR type.</summary>
    member internal _.ApiType = shape.ApiType

    /// <summary>The configured key codec.</summary>
    member internal _.KeyCodec = keyCodec

    /// <summary>Immutable operation descriptors in API-record declaration order.</summary>
    member internal _.Operations = operations

    /// <summary>The Orleans grain type value derived from the explicit grain type name.</summary>
    member internal _.GrainType = grainType

    /// <summary>Encode a domain key into the exact Orleans grain identity of this contract.</summary>
    member internal _.GrainIdOf(key: 'Key) = GrainId.Create(grainType, keyCodec.EncodeKey key)

    /// <summary>Decode the domain key from an Orleans grain identity.</summary>
    member internal _.KeyOf(grainId: GrainId) = keyCodec.DecodeKey grainId

    /// <summary>Look up a descriptor by its ordinal wire operation ID.</summary>
    member internal _.TryFindOperation(operationId: string) =
        match byId.TryGetValue operationId with
        | true, operation -> Some operation
        | _ -> None

    /// <summary>Look up a descriptor by its source record-field name.</summary>
    member internal _.TryFindField(fieldName: string) =
        operations |> Array.tryFind (fun operation -> operation.FieldName = fieldName)

    /// <summary>Resolve a selector to its descriptor, running it once against the probe record.</summary>
    member internal _.Resolve(entry: string, selector: OperationSelector<'Api, 'Argument, 'Reply>) =
        let field = ApiShape.resolve shape entry selector
        operations.[field.Index]

    override _.ToString() =
        $"GrainContract(grainType = '{grainTypeName}', version = {version}, api = '{shape.ApiType.FullName}', operations = {operations.Length})"

/// <summary>Accumulated, not yet validated, contract configuration.</summary>
[<ReferenceEquality>]
type internal ContractDraftState<'Key> =
    { Shape: ApiShape
      GrainTypeName: string option
      Version: int option
      KeyCodec: KeyCodec<'Key> option
      ReadOnly: Set<int>
      OneWay: Set<int>
      AlwaysInterleave: Set<int>
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

    let create<'Actor, 'Key, 'Api> () =
        GrainContractDraft<'Actor, 'Key, 'Api>(
            { Shape = ApiShape.of'<'Api> ()
              GrainTypeName = None
              Version = None
              KeyCodec = None
              ReadOnly = Set.empty
              OneWay = Set.empty
              AlwaysInterleave = Set.empty
              OperationIds = Map.empty }
        )

    let withState<'Actor, 'Key, 'Api> (state: ContractDraftState<'Key>) =
        GrainContractDraft<'Actor, 'Key, 'Api>(state)

    /// <summary>Install a key codec, rejecting a second key operation.</summary>
    let withKey (operationName: string) (codec: KeyCodec<'Key>) (state: ContractDraftState<'Key>) =
        match state.KeyCodec with
        | Some existing ->
            fail
                ContractStage
                $"'{operationName}' conflicts with '{existing.OperationName}'. Exactly one native or mapped key operation is required."
        | None -> { state with KeyCodec = Some codec }

    /// <summary>Add a policy to one field, rejecting a repeated policy of the same kind.</summary>
    let addPolicy (policyName: string) (current: Set<int>) (operation: ApiOperationShape) =
        if current.Contains operation.Index then
            fail ContractStage $"'{policyName}' is applied more than once to API field '{operation.FieldName}'."

        current.Add operation.Index

    /// <summary>Seal a draft into an immutable contract.</summary>
    let run<'Actor, 'Key, 'Api> (draft: GrainContractDraft<'Actor, 'Key, 'Api>) : GrainContract<'Actor, 'Key, 'Api> =
        let state = draft.State
        let apiName = state.Shape.ApiType.FullName

        let grainTypeName =
            match state.GrainTypeName with
            | None -> fail ContractStage $"the contract for '{apiName}' requires exactly one 'grainType' operation."
            | Some value -> value

        let version = state.Version |> Option.defaultValue 1

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

                let isReadOnly = state.ReadOnly.Contains field.Index
                let isOneWay = state.OneWay.Contains field.Index
                let isAlwaysInterleave = state.AlwaysInterleave.Contains field.Index

                if isOneWay && field.ReplyType <> typeof<unit> then
                    fail
                        ContractStage
                        $"'oneWay' requires API field '{field.FieldName}' of '{grainTypeName}' to return Task<unit>, but it returns Task<{field.ReplyType.FullName}>."

                if isOneWay && isReadOnly then
                    fail
                        ContractStage
                        $"API field '{field.FieldName}' of '{grainTypeName}' combines 'oneWay' with 'readOnly', which is rejected."

                if isAlwaysInterleave && not (isReadOnly || isOneWay) then
                    fail
                        ContractStage
                        $"API field '{field.FieldName}' of '{grainTypeName}' uses 'alwaysInterleave' without 'readOnly' or 'oneWay'."

                { Index = field.Index
                  FieldName = field.FieldName
                  OperationId = operationId
                  FunctionType = field.FunctionType
                  ArgumentType = field.ArgumentType
                  ReplyType = field.ReplyType
                  IsReadOnly = isReadOnly
                  IsOneWay = isOneWay
                  IsAlwaysInterleave = isAlwaysInterleave })

        let seen = Dictionary<string, string>(StringComparer.Ordinal)

        for operation in operations do
            match seen.TryGetValue operation.OperationId with
            | true, owner ->
                fail
                    ContractStage
                    $"operation ID '{operation.OperationId}' of '{grainTypeName}' is used by both API field '{owner}' and API field '{operation.FieldName}'."
            | _ -> seen.[operation.OperationId] <- operation.FieldName

        GrainContract<'Actor, 'Key, 'Api>(grainTypeName, version, state.Shape, keyCodec, operations)

/// <summary>
/// The <c>grainContract</c> computation expression: immutable contract metadata, exactly one
/// key operation, and per-field policy and operation-ID overrides.
/// </summary>
[<Sealed>]
type GrainContractBuilder<'Actor, 'Key, 'Api> internal () =

    /// <summary>Start an empty draft for the API record type.</summary>
    member _.Yield(_: unit) : GrainContractDraft<'Actor, 'Key, 'Api> = ContractDraft.create<'Actor, 'Key, 'Api> ()

    /// <summary>Validate and seal the draft into an immutable contract.</summary>
    member _.Run(draft: GrainContractDraft<'Actor, 'Key, 'Api>) : GrainContract<'Actor, 'Key, 'Api> =
        ContractDraft.run draft

    /// <summary>Set the explicit Orleans grain type; required exactly once.</summary>
    [<CustomOperation("grainType")>]
    member _.GrainType(state: GrainContractDraft<'Actor, 'Key, 'Api>, value: string) =
        if isBlank value then
            fail ContractStage "'grainType' requires a non-blank value."

        if containsNul value then
            fail ContractStage $"'grainType' value '{value}' must not contain a NUL character."

        match state.State.GrainTypeName with
        | Some existing -> fail ContractStage $"'grainType' is already set to '{existing}'; it is required exactly once."
        | None -> ContractDraft.withState<'Actor, 'Key, 'Api> { state.State with GrainTypeName = Some value }

    /// <summary>Set the application contract version; defaults to <c>1</c>.</summary>
    [<CustomOperation("version")>]
    member _.Version(state: GrainContractDraft<'Actor, 'Key, 'Api>, value: int) =
        if value <= 0 then
            fail ContractStage $"'version' must be a positive integer, but {value} was supplied."

        match state.State.Version with
        | Some existing -> fail ContractStage $"'version' is already set to {existing}; it is allowed at most once."
        | None -> ContractDraft.withState<'Actor, 'Key, 'Api> { state.State with Version = Some value }

    /// <summary>Use the native Orleans string key.</summary>
    [<CustomOperation("stringKey")>]
    member _.StringKey(state: GrainContractDraft<'Actor, string, 'Api>) =
        ContractDraft.withState<'Actor, string, 'Api> (ContractDraft.withKey "stringKey" KeyCodecs.stringKey state.State)

    /// <summary>Map a domain key onto the native Orleans string key.</summary>
    [<CustomOperation("stringKeyMapped")>]
    member _.StringKeyMapped
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, encode: 'Key -> string, decode: string -> 'Key)
        =
        ContractDraft.withState<'Actor, 'Key, 'Api> (
            ContractDraft.withKey "stringKeyMapped" (KeyCodecs.stringKeyMapped encode decode) state.State
        )

    /// <summary>Use the native Orleans Guid key.</summary>
    [<CustomOperation("guidKey")>]
    member _.GuidKey(state: GrainContractDraft<'Actor, Guid, 'Api>) =
        ContractDraft.withState<'Actor, Guid, 'Api> (ContractDraft.withKey "guidKey" KeyCodecs.guidKey state.State)

    /// <summary>Map a domain key onto the native Orleans Guid key.</summary>
    [<CustomOperation("guidKeyMapped")>]
    member _.GuidKeyMapped(state: GrainContractDraft<'Actor, 'Key, 'Api>, encode: 'Key -> Guid, decode: Guid -> 'Key) =
        ContractDraft.withState<'Actor, 'Key, 'Api> (
            ContractDraft.withKey "guidKeyMapped" (KeyCodecs.guidKeyMapped encode decode) state.State
        )

    /// <summary>Use the native Orleans int64 key.</summary>
    [<CustomOperation("int64Key")>]
    member _.Int64Key(state: GrainContractDraft<'Actor, int64, 'Api>) =
        ContractDraft.withState<'Actor, int64, 'Api> (ContractDraft.withKey "int64Key" KeyCodecs.int64Key state.State)

    /// <summary>Map a domain key onto the native Orleans int64 key.</summary>
    [<CustomOperation("int64KeyMapped")>]
    member _.Int64KeyMapped
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, encode: 'Key -> int64, decode: int64 -> 'Key)
        =
        ContractDraft.withState<'Actor, 'Key, 'Api> (
            ContractDraft.withKey "int64KeyMapped" (KeyCodecs.int64KeyMapped encode decode) state.State
        )

    /// <summary>Use the native Orleans Guid compound key.</summary>
    [<CustomOperation("guidCompoundKey")>]
    member _.GuidCompoundKey(state: GrainContractDraft<'Actor, Guid * string, 'Api>) =
        ContractDraft.withState<'Actor, Guid * string, 'Api> (
            ContractDraft.withKey "guidCompoundKey" KeyCodecs.guidCompoundKey state.State
        )

    /// <summary>Map a domain key onto the native Orleans Guid compound key.</summary>
    [<CustomOperation("guidCompoundKeyMapped")>]
    member _.GuidCompoundKeyMapped
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, encode: 'Key -> Guid * string, decode: Guid -> string -> 'Key)
        =
        ContractDraft.withState<'Actor, 'Key, 'Api> (
            ContractDraft.withKey "guidCompoundKeyMapped" (KeyCodecs.guidCompoundKeyMapped encode decode) state.State
        )

    /// <summary>Use the native Orleans int64 compound key.</summary>
    [<CustomOperation("int64CompoundKey")>]
    member _.Int64CompoundKey(state: GrainContractDraft<'Actor, int64 * string, 'Api>) =
        ContractDraft.withState<'Actor, int64 * string, 'Api> (
            ContractDraft.withKey "int64CompoundKey" KeyCodecs.int64CompoundKey state.State
        )

    /// <summary>Map a domain key onto the native Orleans int64 compound key.</summary>
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
    [<CustomOperation("readOnly")>]
    member _.ReadOnly<'Argument, 'Reply>
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, selector: OperationSelector<'Api, 'Argument, 'Reply>)
        =
        let operation = ApiShape.resolve state.State.Shape "readOnly" selector

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                ReadOnly = ContractDraft.addPolicy "readOnly" state.State.ReadOnly operation }

    /// <summary>Select one-way delivery for one <c>Task&lt;unit&gt;</c> operation.</summary>
    [<CustomOperation("oneWay")>]
    member _.OneWay<'Argument>
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, selector: OperationSelector<'Api, 'Argument, unit>)
        =
        let operation = ApiShape.resolve state.State.Shape "oneWay" selector

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                OneWay = ContractDraft.addPolicy "oneWay" state.State.OneWay operation }

    /// <summary>Permit a read-only or one-way operation to interleave.</summary>
    [<CustomOperation("alwaysInterleave")>]
    member _.AlwaysInterleave<'Argument, 'Reply>
        (state: GrainContractDraft<'Actor, 'Key, 'Api>, selector: OperationSelector<'Api, 'Argument, 'Reply>)
        =
        let operation = ApiShape.resolve state.State.Shape "alwaysInterleave" selector

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                AlwaysInterleave = ContractDraft.addPolicy "alwaysInterleave" state.State.AlwaysInterleave operation }

    /// <summary>Override the stable wire ID of one operation, keeping it across a field rename.</summary>
    [<CustomOperation("operationId")>]
    member _.OperationId<'Argument, 'Reply>
        (
            state: GrainContractDraft<'Actor, 'Key, 'Api>,
            stableWireId: string,
            selector: OperationSelector<'Api, 'Argument, 'Reply>
        ) =
        if isBlank stableWireId then
            fail ContractStage "'operationId' requires a non-blank wire ID."

        if containsNul stableWireId then
            fail ContractStage $"'operationId' value '{stableWireId}' must not contain a NUL character."

        let operation = ApiShape.resolve state.State.Shape "operationId" selector

        if state.State.OperationIds.ContainsKey operation.Index then
            fail
                ContractStage
                $"'operationId' is applied more than once to API field '{operation.FieldName}'."

        ContractDraft.withState<'Actor, 'Key, 'Api>
            { state.State with
                OperationIds = state.State.OperationIds.Add(operation.Index, stableWireId) }
