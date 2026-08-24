/// <summary>
/// Durable-codec selection for functional persistent state and journals, plus the provider-wide
/// F# JSON serializer for ordinary Orleans persistence.
/// </summary>
module Orleans.FSharp.Tests.FunctionalPersistenceSerializationTests

open System
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Orleans.Core
open Orleans.Runtime
open Orleans.Storage
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Tests.FunctionalTransportHarness

type PayloadStatus =
    | Draft
    | Published of revision: int

type JsonPayload =
    { DisplayName: string
      Status: PayloadStatus
      Tags: Map<string, string>
      Retry: int option }

type TaggedJsonPayload = { Value: int }

[<Sealed>]
type private TaggedJsonPayloadConverter() =
    inherit JsonConverter<TaggedJsonPayload>()

    override _.Write(writer: Utf8JsonWriter, value: TaggedJsonPayload, _options: JsonSerializerOptions) =
        writer.WriteStringValue $"tagged:{value.Value}"

    override _.Read
        (
            reader: byref<Utf8JsonReader>,
            _typeToConvert: Type,
            _options: JsonSerializerOptions
        ) =
        let value = reader.GetString()

        if isNull value || not (value.StartsWith("tagged:", StringComparison.Ordinal)) then
            raise (JsonException "Expected a tagged payload.")

        { Value = Int32.Parse(value.Substring "tagged:".Length) }

let private payload =
    { DisplayName = "F# durable value"
      Status = Published 7
      Tags = Map [ "team", "storage"; "format", "json" ]
      Retry = Some 3 }

let private binaryCodec () =
    let services = buildServices true None
    let provider = SerializerPreflight.providerOf services "codec.tests"

    SerializerPreflight.ensureStoredTypes
        provider
        "codec.tests"
        [| "payload", typeof<JsonPayload> |]

    services, (payloadCodec services :> IFunctionalPayloadCodec)

[<Fact>]
let ``provider-wide FSharp JSON serializer round-trips records unions options and maps`` () =
    let serializer = FSharpJsonGrainStorageSerializer() :> IGrainStorageSerializer
    let encoded = serializer.Serialize payload
    let restored = serializer.Deserialize<JsonPayload> encoded

    test <@ restored = payload @>

[<Fact>]
let ``provider-wide serializer clones custom JSON options`` () =
    let options = JsonSerializerOptions(FSharpJson.serializerOptions)
    options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

    let serializer = FSharpJsonGrainStorageSerializer(options) :> IGrainStorageSerializer

    // Mutation after construction must not change the durable format of the serializer instance.
    options.PropertyNamingPolicy <- null

    let encoded = serializer.Serialize payload
    let json = Encoding.UTF8.GetString(encoded.ToArray())
    let restored = serializer.Deserialize<JsonPayload> encoded

    test <@ json.Contains "\"displayName\"" @>
    test <@ restored = payload @>

[<Fact>]
let ``functional codec identifiers and defaults are stable`` () =
    let options = FunctionalPersistenceOptions()

    test <@ FunctionalPersistenceCodec.OrleansBinary.Id = "orleans-binary-v1" @>
    test <@ FunctionalPersistenceCodec.FSharpJson.Id = "fsharp-json-v1" @>
    test <@ options.DefaultStateCodec.Id = "orleans-binary-v1" @>
    test <@ options.DefaultJournalCodec.Id = "orleans-binary-v1" @>

[<Fact>]
let ``functional FSharp JSON encoding round-trips without using the Orleans payload codec`` () =
    let services, binary = binaryCodec ()
    use services = services

    let encoded =
        FunctionalPersistenceEncoding.encode FunctionalPersistenceCodec.FSharpJson binary payload

    let restored =
        FunctionalPersistenceEncoding.decode<JsonPayload>
            FunctionalPersistenceCodec.FSharpJson
            binary
            FunctionalPersistenceEncoding.FSharpJsonId
            encoded

    test <@ restored = payload @>

[<Fact>]
let ``blank durable codec id remains the pre-feature Orleans binary format`` () =
    let services, binary = binaryCodec ()
    use services = services
    let encoded = binary.Serialize payload

    let restored =
        FunctionalPersistenceEncoding.decode<JsonPayload>
            FunctionalPersistenceCodec.FSharpJson
            binary
            ""
            encoded

    test <@ restored = payload @>

[<Fact>]
let ``a stored JSON payload remains readable after the selected write codec changes`` () =
    let services, binary = binaryCodec ()
    use services = services

    let encoded =
        FunctionalPersistenceEncoding.encode FunctionalPersistenceCodec.FSharpJson binary payload

    let restored =
        FunctionalPersistenceEncoding.decode<JsonPayload>
            FunctionalPersistenceCodec.OrleansBinary
            binary
            FunctionalPersistenceEncoding.FSharpJsonId
            encoded

    test <@ restored = payload @>

[<Fact>]
let ``a historical custom JSON reader survives a switch to Orleans binary writes`` () =
    let services, binary = binaryCodec ()
    use services = services
    let options = JsonSerializerOptions(FSharpJson.serializerOptions)
    options.Converters.Insert(0, TaggedJsonPayloadConverter())
    let historical = FunctionalPersistenceCodec.CreateFSharpJson(options)
    let current = FunctionalPersistenceCodec.OrleansBinary.WithReadCodec historical
    let original = { Value = 42 }
    let encoded = FunctionalPersistenceEncoding.encode historical binary original

    let withoutHistoricalReader () =
        FunctionalPersistenceEncoding.decode<TaggedJsonPayload>
            FunctionalPersistenceCodec.OrleansBinary
            binary
            historical.Id
            encoded
        |> ignore

    Assert.ThrowsAny<JsonException>(withoutHistoricalReader) |> ignore

    let restored =
        FunctionalPersistenceEncoding.decode<TaggedJsonPayload> current binary historical.Id encoded

    test <@ restored = original @>

[<Fact>]
let ``custom JSON codec ids are stable and reserved ids are rejected`` () =
    let options = JsonSerializerOptions(FSharpJson.serializerOptions)
    let codec = FunctionalPersistenceCodec.CreateFSharpJson("orders-json-v2", options)
    test <@ codec.Id = "orders-json-v2" @>

    Assert.Throws<ArgumentException>(fun () ->
        FunctionalPersistenceCodec.CreateFSharpJson("orleans-binary-v1", options) |> ignore)
    |> ignore

[<Fact>]
let ``historical readers reject duplicate and transitive current codec ids`` () =
    let options = JsonSerializerOptions(FSharpJson.serializerOptions)
    let v1 = FunctionalPersistenceCodec.CreateFSharpJson("orders-json-v1", options)
    let currentV2 = FunctionalPersistenceCodec.CreateFSharpJson("orders-json-v2", options)

    let withV1 = FunctionalPersistenceCodec.OrleansBinary.WithReadCodec v1

    Assert.Throws<ArgumentException>(fun () -> withV1.WithReadCodec(v1) |> ignore)
    |> ignore

    let conflictingOptions = JsonSerializerOptions(FSharpJson.serializerOptions)
    conflictingOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

    let conflictingV2 =
        FunctionalPersistenceCodec.CreateFSharpJson("orders-json-v2", conflictingOptions)

    let nestedHistory = v1.WithReadCodec conflictingV2

    Assert.Throws<ArgumentException>(fun () -> currentV2.WithReadCodec(nestedHistory) |> ignore)
    |> ignore

[<Fact>]
let ``an unknown durable codec id fails with a migration diagnostic`` () =
    let services, binary = binaryCodec ()
    use services = services

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FunctionalPersistenceEncoding.decode<JsonPayload>
                FunctionalPersistenceCodec.FSharpJson
                binary
                "future-codec-v1"
                [| 1uy |]
            |> ignore)

    test <@ error.Message.Contains "future-codec-v1" @>
    test <@ error.Message.Contains "migrating" @>

[<Sealed>]
type private RecordingEnvelopeFacet(initial: FunctionalPersistenceEnvelope, recordExists: bool) =
    let mutable current = initial
    let mutable exists = recordExists

    member _.Current = current
    member val Reads = 0 with get, set
    member val Writes = 0 with get, set
    member val Clears = 0 with get, set

    interface IPersistentState<FunctionalPersistenceEnvelope>

    interface IStorage<FunctionalPersistenceEnvelope> with
        member _.State
            with get () = current
            and set value = current <- value

    interface IStorage with
        member _.Etag = "etag-json"
        member _.RecordExists = exists

        member this.ReadStateAsync() =
            this.Reads <- this.Reads + 1
            Task.CompletedTask

        member this.WriteStateAsync() =
            this.Writes <- this.Writes + 1
            exists <- true
            Task.CompletedTask

        member this.ClearStateAsync() =
            this.Clears <- this.Clears + 1
            current <- null
            exists <- false
            Task.CompletedTask

        member this.ReadStateAsync(_cancellationToken: CancellationToken) =
            this.Reads <- this.Reads + 1
            Task.CompletedTask

        member this.WriteStateAsync(_cancellationToken: CancellationToken) =
            this.Writes <- this.Writes + 1
            exists <- true
            Task.CompletedTask

        member this.ClearStateAsync(_cancellationToken: CancellationToken) =
            this.Clears <- this.Clears + 1
            current <- null
            exists <- false
            Task.CompletedTask

[<Fact>]
let ``codec-backed persistent state stores a tagged envelope and preserves storage semantics`` () =
    task {
        let services, binary = binaryCodec ()
        use services = services
        let inner = RecordingEnvelopeFacet(null, false)

        let state =
            FunctionalEncodedPersistentState<JsonPayload>(
                inner :> IPersistentState<FunctionalPersistenceEnvelope>,
                FunctionalPersistenceCodec.FSharpJson,
                binary
            )
            :> IPersistentState<JsonPayload>

        test <@ state.State = Unchecked.defaultof<JsonPayload> @>

        state.State <- payload
        let envelope = inner.Current

        test <@ envelope.HasValue @>
        test <@ envelope.CodecId = "fsharp-json-v1" @>
        test <@ not (isNull envelope.Payload) @>
        test <@ state.State = payload @>
        test <@ state.Etag = "etag-json" @>
        test <@ not state.RecordExists @>

        do! state.WriteStateAsync()
        test <@ inner.Writes = 1 @>
        test <@ state.RecordExists @>

        do! state.ReadStateAsync(CancellationToken.None)
        test <@ inner.Reads = 1 @>

        do! state.ClearStateAsync()
        test <@ inner.Clears = 1 @>
        test <@ not state.RecordExists @>
    }

type CodecActor = private CodecActor of unit

[<NoEquality; NoComparison>]
type CodecApi = { touch: unit -> Task<unit> }

type CodecState = { Count: int }
type CodecAudit = { Entries: string list }

let private codecContract =
    grainContract<CodecActor, string, CodecApi> {
        grainType "codec.precedence"
        stringKey
    }

let private touch _ state () = task { return state, () }

[<Fact>]
let ``persistent element override wins over grain override and an absent grain override inherits silo`` () =
    let primary = PersistentState.create<CodecState> "state" "Default"
    let inherited = PersistentState.create<CodecAudit> "inherited" "Default"

    let element =
        PersistentState.create<CodecAudit> "element" "Default"
        |> PersistentState.withCodec FunctionalPersistenceCodec.OrleansBinary

    let definition =
        grainFor codecContract {
            defaultState (fun () -> { Count = 0 })
            stateFrom primary
            persistenceCodec FunctionalPersistenceCodec.FSharpJson
            usePersistentState inherited (fun _ -> { Entries = [] })
            usePersistentState element (fun _ -> { Entries = [] })
            handle (_.touch) touch
        }

    let hosted = FunctionalHosted.create definition

    let selected =
        hosted.Facets
        |> Array.map (fun facet -> facet.Descriptor.StateName, facet.CodecOverride |> Option.map _.Id)
        |> Map.ofArray

    test <@ selected.["state"] = Some "fsharp-json-v1" @>
    test <@ selected.["inherited"] = Some "fsharp-json-v1" @>
    test <@ selected.["element"] = Some "orleans-binary-v1" @>

    let siloInherited =
        grainFor codecContract {
            defaultState (fun () -> { Count = 0 })
            stateFrom primary
            handle (_.touch) touch
        }
        |> FunctionalHosted.create

    test <@ siloInherited.Facets.[0].CodecOverride.IsNone @>

[<Fact>]
let ``FSharp JSON allows a persistent state shape stock Orleans cannot activate directly`` () =
    let text =
        PersistentState.create<string> "text" "Default"
        |> PersistentState.withCodec FunctionalPersistenceCodec.FSharpJson

    let definition =
        grainFor codecContract {
            defaultState (fun () -> { Count = 0 })
            usePersistentState text (fun _ -> "")
            handle (_.touch) touch
        }

    let hosted = FunctionalHosted.create definition
    test <@ hosted.Facets.[0].Descriptor.StoredType = typeof<string> @>
    test <@ hosted.Facets.[0].CodecOverride.Value.Id = "fsharp-json-v1" @>

type JournalCodecActor = private JournalCodecActor of unit

[<NoEquality; NoComparison>]
type JournalCodecApi = { add: int -> Task<unit> }

type JournalCodecState = { Total: int }
type JournalCodecEvent = Added of int

let private journalCodecContract =
    grainContract<JournalCodecActor, string, JournalCodecApi> {
        grainType "codec.journal"
        stringKey
    }

let private journalDefinition codec =
    journaledGrainFor journalCodecContract {
        initialEventState (fun (_: string) -> { Total = 0 })
        apply (fun state (Added amount) -> { Total = state.Total + amount })
        logProvider "LogStorage"
        journalCodec codec
        handle (_.add) (fun _ _ amount -> task { return [ Added amount ], () })
    }

[<Fact>]
let ``journal definition override is retained and encodes state and events as FSharp JSON`` () =
    let services, binary = binaryCodec ()
    use services = services
    let hosted = journalDefinition FunctionalPersistenceCodec.FSharpJson |> FunctionalJournaledHosted.create
    let journal = hosted.Journal.Value

    let stateBytes = journal.EncodeState FunctionalPersistenceCodec.FSharpJson binary (box { Total = 9 })
    let eventBytes = journal.EncodeEvent FunctionalPersistenceCodec.FSharpJson binary (box (Added 4))

    let state =
        journal.DecodeState FunctionalPersistenceCodec.FSharpJson binary "fsharp-json-v1" stateBytes
        |> unbox<JournalCodecState>

    let event =
        journal.DecodeEvent FunctionalPersistenceCodec.FSharpJson binary "fsharp-json-v1" eventBytes
        |> unbox<JournalCodecEvent>

    test <@ journal.CodecOverride.Value.Id = "fsharp-json-v1" @>
    test <@ state = { Total = 9 } @>
    test <@ event = Added 4 @>

[<Fact>]
let ``journalCodec rejects null and duplicate declarations`` () =
    let nullError =
        Assert.Throws<InvalidOperationException>(fun () ->
            journalDefinition (Unchecked.defaultof<FunctionalPersistenceCodec>) |> ignore)

    let duplicateError =
        Assert.Throws<InvalidOperationException>(fun () ->
            journaledGrainFor journalCodecContract {
                initialEventState (fun (_: string) -> { Total = 0 })
                apply (fun state (Added amount) -> { Total = state.Total + amount })
                logProvider "LogStorage"
                journalCodec FunctionalPersistenceCodec.FSharpJson
                journalCodec FunctionalPersistenceCodec.OrleansBinary
                handle (_.add) (fun _ _ amount -> task { return [ Added amount ], () })
            }
            |> ignore)

    test <@ nullError.Message.Contains "cannot be null" @>
    test <@ duplicateError.Message.Contains "declared more than once" @>
