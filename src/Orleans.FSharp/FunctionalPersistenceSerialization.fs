namespace Orleans.FSharp

open System
open System.Text.Json
open Orleans.Storage

/// <summary>The implementation selected for one functional durable payload.</summary>
[<RequireQualifiedAccess>]
type internal FunctionalPersistenceCodecKind =
    | OrleansBinary
    | FSharpJson

[<RequireQualifiedAccess>]
module internal FunctionalPersistenceCodecIds =

    [<Literal>]
    let OrleansBinary = "orleans-binary-v1"

    [<Literal>]
    let FSharpJson = "fsharp-json-v1"

/// <summary>
/// Selects how a functional persistent-state value or journal payload is encoded.
/// </summary>
/// <remarks>
/// <c>OrleansBinary</c> preserves the existing Orleans exact-type payload. <c>FSharpJson</c>
/// uses System.Text.Json with FSharp.SystemTextJson converters. A descriptor-level selection
/// overrides its grain, and a grain-level selection overrides the silo default.
/// </remarks>
[<Sealed>]
type FunctionalPersistenceCodec private
    (
        id: string,
        kind: FunctionalPersistenceCodecKind,
        jsonOptions: JsonSerializerOptions,
        jsonReaders: Map<string, JsonSerializerOptions>
    ) =

    static let orleansBinary =
        FunctionalPersistenceCodec(
            FunctionalPersistenceCodecIds.OrleansBinary,
            FunctionalPersistenceCodecKind.OrleansBinary,
            null,
            Map.empty
        )

    static let fsharpJson =
        let options = JsonSerializerOptions(FSharpJson.serializerOptions)

        FunctionalPersistenceCodec(
            FunctionalPersistenceCodecIds.FSharpJson,
            FunctionalPersistenceCodecKind.FSharpJson,
            options,
            Map.ofList [ FunctionalPersistenceCodecIds.FSharpJson, options ]
        )

    /// <summary>A stable identifier stored beside encoded payloads.</summary>
    member _.Id = id

    /// <summary>The existing Orleans exact-type binary payload codec.</summary>
    static member OrleansBinary = orleansBinary

    /// <summary>F#-aware System.Text.Json using the library's standard FSharp.SystemTextJson options.</summary>
    static member FSharpJson = fsharpJson

    /// <summary>Create an F# JSON codec from application-supplied System.Text.Json options.</summary>
    /// <param name="options">
    /// Options containing every converter required by the stored F# types. The options are cloned
    /// so later application mutation cannot change a running activation's durable format.
    /// </param>
    static member CreateFSharpJson(options: JsonSerializerOptions) =
        FunctionalPersistenceCodec.CreateFSharpJson(FunctionalPersistenceCodecIds.FSharpJson, options)

    /// <summary>Create an F# JSON codec with an application-owned stable durable identifier.</summary>
    /// <param name="codecId">
    /// A stable identifier for this exact JSON contract. Change it when converter or schema options
    /// become incompatible, and retain the old codec with <c>WithReadCodec</c> while old data exists.
    /// </param>
    /// <param name="options">Options containing every converter required by the stored F# types.</param>
    static member CreateFSharpJson(codecId: string, options: JsonSerializerOptions) =
        if String.IsNullOrWhiteSpace codecId then
            invalidArg (nameof codecId) "A functional persistence codec id cannot be blank."

        if codecId = FunctionalPersistenceCodecIds.OrleansBinary then
            invalidArg
                (nameof codecId)
                $"Codec id '{FunctionalPersistenceCodecIds.OrleansBinary}' is reserved for the Orleans binary format."

        if isNull options then
            nullArg (nameof options)

        let cloned = JsonSerializerOptions(options)

        FunctionalPersistenceCodec(
            codecId,
            FunctionalPersistenceCodecKind.FSharpJson,
            cloned,
            Map.ofList [ codecId, cloned ]
        )

    /// <summary>Retain a historical JSON decoder while this codec remains the write codec.</summary>
    /// <remarks>
    /// The returned codec writes exactly like this instance and reads every registered historical
    /// id. A reader id already present on this codec is rejected, including a duplicate nested
    /// historical id or the current JSON write id, because those payloads would be ambiguous.
    /// </remarks>
    member _.WithReadCodec(historicalCodec: FunctionalPersistenceCodec) =
        if obj.ReferenceEquals(historicalCodec, null) then
            nullArg (nameof historicalCodec)

        if historicalCodec.Kind <> FunctionalPersistenceCodecKind.FSharpJson then
            invalidArg (nameof historicalCodec) "A historical functional read codec must be an F# JSON codec."

        let collisions =
            historicalCodec.JsonReaders
            |> Map.toSeq
            |> Seq.map fst
            |> Seq.filter jsonReaders.ContainsKey
            |> Seq.sort
            |> Seq.toArray

        if collisions.Length > 0 then
            let collisionIds = String.Join("', '", collisions)

            invalidArg
                (nameof historicalCodec)
                $"Historical JSON reader id(s) '{collisionIds}' are already registered by the current codec. A durable codec id must identify exactly one JSON contract."

        let writeOptions =
            if isNull jsonOptions then null else JsonSerializerOptions(jsonOptions)

        let readers =
            jsonReaders
            |> Map.map (fun _ options -> JsonSerializerOptions(options))
            |> fun readers ->
                (readers, historicalCodec.JsonReaders)
                ||> Map.fold (fun
                                  (current: Map<string, JsonSerializerOptions>)
                                  (codecId: string)
                                  (options: JsonSerializerOptions)
                                  ->
                    current.Add(codecId, JsonSerializerOptions(options)))

        FunctionalPersistenceCodec(id, kind, writeOptions, readers)

    member internal _.Kind = kind

    member internal _.JsonOptions = jsonOptions

    member internal _.JsonReaders = jsonReaders

    override _.ToString() = id

/// <summary>Silo-wide defaults for functional persistent states and functional journals.</summary>
[<Sealed>]
type FunctionalPersistenceOptions() =

    /// <summary>
    /// Default for <c>stateFrom</c> and <c>usePersistentState</c>. A grain-level
    /// <c>persistenceCodec</c>, then a descriptor-level <c>PersistentState.withCodec</c>, wins.
    /// </summary>
    member val DefaultStateCodec = FunctionalPersistenceCodec.OrleansBinary with get, set

    /// <summary>
    /// Default for functional journal state and event payloads. A definition-level
    /// <c>journalCodec</c> wins.
    /// </summary>
    member val DefaultJournalCodec = FunctionalPersistenceCodec.OrleansBinary with get, set

/// <summary>
/// An Orleans grain-storage serializer backed by System.Text.Json and FSharp.SystemTextJson.
/// </summary>
/// <remarks>
/// Assign this instance to a storage provider's <c>GrainStorageSerializer</c> option to use F#
/// JSON for ordinary Orleans persistence, including non-functional grains. Functional grains can
/// additionally select JSON per attached state through <c>PersistentState.withCodec</c>.
/// </remarks>
[<Sealed>]
type FSharpJsonGrainStorageSerializer(options: JsonSerializerOptions) =

    let options =
        if isNull options then
            nullArg (nameof options)

        JsonSerializerOptions(options)

    /// <summary>Use the library's standard FSharp.SystemTextJson options.</summary>
    new() = FSharpJsonGrainStorageSerializer(FSharpJson.serializerOptions)

    interface IGrainStorageSerializer with
        member _.Serialize<'T>(input: 'T) : BinaryData =
            BinaryData(JsonSerializer.SerializeToUtf8Bytes<'T>(input, options))

        member _.Deserialize<'T>(input: BinaryData) : 'T =
            if isNull input then
                nullArg (nameof input)

            JsonSerializer.Deserialize<'T>(input.ToArray(), options)

/// <summary>Exact-type encoding shared by functional state envelopes and journal payloads.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalPersistenceEncoding =

    let private standardFSharpJsonReader = JsonSerializerOptions(FSharpJson.serializerOptions)

    [<Literal>]
    let OrleansBinaryId = FunctionalPersistenceCodecIds.OrleansBinary

    [<Literal>]
    let FSharpJsonId = FunctionalPersistenceCodecIds.FSharpJson

    let ensure (name: string) (codec: FunctionalPersistenceCodec) =
        if obj.ReferenceEquals(codec, null) then
            invalidArg name "A functional persistence codec cannot be null."

        codec

    let encode<'T>
        (selected: FunctionalPersistenceCodec)
        (orleansCodec: IFunctionalPayloadCodec)
        (value: 'T)
        : byte[] =
        let selected = ensure (nameof selected) selected

        match selected.Kind with
        | FunctionalPersistenceCodecKind.OrleansBinary -> orleansCodec.Serialize<'T> value
        | FunctionalPersistenceCodecKind.FSharpJson ->
            JsonSerializer.SerializeToUtf8Bytes(box value, typeof<'T>, selected.JsonOptions)

    let decode<'T>
        (selected: FunctionalPersistenceCodec)
        (orleansCodec: IFunctionalPayloadCodec)
        (storedCodecId: string)
        (payload: byte[])
        : 'T =
        let selected = ensure (nameof selected) selected

        if isNull payload then
            invalidArg (nameof payload) "A functional durable payload cannot be null."

        // CodecId did not exist before the JSON codec feature. Such journal cells and entries are
        // the old Orleans binary form and remain readable after an upgrade.
        let codecId =
            if String.IsNullOrWhiteSpace storedCodecId then
                OrleansBinaryId
            else
                storedCodecId

        if codecId = OrleansBinaryId then
            orleansCodec.Deserialize<'T> payload
        else
            match selected.JsonReaders |> Map.tryFind codecId with
            | Some jsonOptions ->
                JsonSerializer.Deserialize(payload, typeof<'T>, jsonOptions) |> unbox<'T>
            | None when codecId = FSharpJsonId ->
                JsonSerializer.Deserialize(payload, typeof<'T>, standardFSharpJsonReader) |> unbox<'T>
            | None ->
                raise (
                    InvalidOperationException(
                        $"Functional durable payload codec '{codecId}' is not registered for reading. Keep the codec which wrote the data available with FunctionalPersistenceCodec.WithReadCodec while migrating it."
                    )
                )

/// <summary>One closed, typed step in a durable payload's schema-upcasting pipeline.</summary>
[<ReferenceEquality>]
type internal FunctionalSchemaStep =
    {
        FromVersion: int
        ToVersion: int
        InputType: Type
        OutputType: Type
        Upcast:
            FunctionalPersistenceCodec
                -> IFunctionalPayloadCodec
                -> string
                -> byte[]
                -> struct (string * byte[])
    }

/// <summary>
/// A typed, immutable schema evolution pipeline for one functional durable value.
/// </summary>
/// <remarks>
/// The current schema version is stored beside every encoded payload. Reading walks the registered
/// steps from the stored version to <see cref="P:Orleans.FSharp.FunctionalSchema`1.CurrentVersion"/>,
/// decoding each historical CLR type before invoking its pure F# upcaster. Records written before
/// schema evolution existed have version zero, so a pipeline which upgrades those records starts
/// with <c>FunctionalSchema.current&lt;LegacyType&gt; 0</c>.
/// </remarks>
[<Sealed>]
type FunctionalSchema<'Current> internal
    (earliestVersion: int, currentVersion: int, steps: Map<int, FunctionalSchemaStep>) =

    /// <summary>The version written for new values.</summary>
    member _.CurrentVersion = currentVersion

    /// <summary>The oldest version for which this pipeline has a typed decoder.</summary>
    member _.EarliestVersion = earliestVersion

    member internal _.Steps = steps

    /// <summary>Decode a current payload or walk every required typed upcaster first.</summary>
    member internal _.Decode
        (
            selectedCodec: FunctionalPersistenceCodec,
            orleansCodec: IFunctionalPayloadCodec,
            storedSchemaVersion: int,
            storedCodecId: string,
            payload: byte[]
        )
        : 'Current =
        if storedSchemaVersion < 0 then
            raise (
                InvalidOperationException(
                    $"Functional durable payload schema version {storedSchemaVersion} is invalid; schema versions cannot be negative."
                )
            )

        if storedSchemaVersion > currentVersion then
            raise (
                InvalidOperationException(
                    $"Functional durable payload schema version {storedSchemaVersion} is newer than the configured current version {currentVersion} for '{typeof<'Current>.FullName}'. Deploy code which understands the newer schema before reading this record."
                )
            )

        let rec evolve version codecId bytes =
            if version = currentVersion then
                FunctionalPersistenceEncoding.decode<'Current> selectedCodec orleansCodec codecId bytes
            else
                match steps |> Map.tryFind version with
                | None ->
                    raise (
                        InvalidOperationException(
                            $"Functional durable payload schema version {version} cannot reach configured version {currentVersion} for '{typeof<'Current>.FullName}'. Register an upcaster from version {version}; records written before schema evolution use version 0."
                        )
                    )
                | Some step ->
                    try
                        let struct (nextCodecId, nextPayload) =
                            step.Upcast selectedCodec orleansCodec codecId bytes

                        evolve step.ToVersion nextCodecId nextPayload
                    with
                    | :? InvalidOperationException as cause
                        when cause.Message.StartsWith("Functional durable payload schema version", StringComparison.Ordinal) ->
                        reraise ()
                    | cause ->
                        raise (
                            InvalidOperationException(
                                $"Functional durable payload upcaster from schema version {step.FromVersion} ('{step.InputType.FullName}') to {step.ToVersion} ('{step.OutputType.FullName}') failed.",
                                cause
                            )
                        )

        evolve storedSchemaVersion storedCodecId payload

    override _.ToString() =
        $"FunctionalSchema(currentVersion = {currentVersion}, type = '{typeof<'Current>.FullName}')"

/// <summary>Construction and composition of typed durable-schema pipelines.</summary>
[<RequireQualifiedAccess>]
module FunctionalSchema =

    let private validateVersion argumentName version =
        if version < 0 then
            invalidArg argumentName "A functional durable schema version cannot be negative."

    /// <summary>
    /// Define a value at one schema version. Compose older-to-newer steps with
    /// <c>FunctionalSchema.upcasterTo</c>.
    /// </summary>
    let current<'T> (version: int) : FunctionalSchema<'T> =
        validateVersion (nameof version) version

        if typeof<'T>.ContainsGenericParameters then
            invalidArg "typeParameter" "A functional durable schema requires a closed current type."

        FunctionalSchema<'T>(version, version, Map.empty)

    /// <summary>
    /// Append a typed upcaster and make its output type the pipeline's current durable type.
    /// The target version must be greater than the preceding current version; skipped numeric
    /// versions are allowed and remain explicit in the envelope.
    /// </summary>
    let upcasterTo<'Previous, 'Current>
        (targetVersion: int)
        (mapping: 'Previous -> 'Current)
        (schema: FunctionalSchema<'Previous>)
        : FunctionalSchema<'Current> =
        if obj.ReferenceEquals(schema, null) then
            nullArg (nameof schema)

        if obj.ReferenceEquals(mapping, null) then
            nullArg (nameof mapping)

        validateVersion (nameof targetVersion) targetVersion

        if targetVersion <= schema.CurrentVersion then
            invalidArg
                (nameof targetVersion)
                $"The next functional durable schema version ({targetVersion}) must be greater than the pipeline's current version ({schema.CurrentVersion})."

        if typeof<'Current>.ContainsGenericParameters then
            invalidArg "typeParameter" "A functional durable schema requires a closed target type."

        let step =
            { FromVersion = schema.CurrentVersion
              ToVersion = targetVersion
              InputType = typeof<'Previous>
              OutputType = typeof<'Current>
              Upcast =
                fun selectedCodec orleansCodec storedCodecId payload ->
                    let previous =
                        FunctionalPersistenceEncoding.decode<'Previous>
                            selectedCodec
                            orleansCodec
                            storedCodecId
                            payload

                    let current = mapping previous
                    let encoded = FunctionalPersistenceEncoding.encode selectedCodec orleansCodec current
                    struct (selectedCodec.Id, encoded) }

        FunctionalSchema<'Current>(
            schema.EarliestVersion,
            targetVersion,
            schema.Steps.Add(step.FromVersion, step)
        )

    /// <summary>Append a typed upcaster whose target is the next integer schema version.</summary>
    let upcaster<'Previous, 'Current>
        (mapping: 'Previous -> 'Current)
        (schema: FunctionalSchema<'Previous>)
        : FunctionalSchema<'Current> =
        if obj.ReferenceEquals(schema, null) then
            nullArg (nameof schema)

        if schema.CurrentVersion = Int32.MaxValue then
            invalidArg (nameof schema) "The functional durable schema is already at Int32.MaxValue."

        upcasterTo (schema.CurrentVersion + 1) mapping schema
