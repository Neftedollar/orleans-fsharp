/// <summary>
/// Live Orleans proof that silo-wide F# JSON persistence works for both functional state and
/// functional journals, including replay after deactivation.
/// </summary>
module Orleans.FSharp.Integration.FunctionalPersistenceCodecIntegrationTests

open System
open System.Collections
open System.Collections.Concurrent
open System.Reflection
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.EventSourcing
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Storage
open Orleans.TestingHost
open Xunit
open Orleans.FSharp

[<RequireQualifiedAccess>]
module private CodecCapture =
    let records = ConcurrentDictionary<string, obj * string>()
    let envelopeCodecIds = ConcurrentQueue<string>()
    let journalCodecIds = ConcurrentQueue<string>()
    let journalEntryCodecIds = ConcurrentQueue<string>()

    let key stateName grainId = $"{stateName}/{grainId}"

    let rec inspect depth (value: obj) =
        if depth < 0 || isNull value then
            ()
        else
            match value with
            | :? FunctionalPersistenceEnvelope as envelope ->
                envelopeCodecIds.Enqueue envelope.CodecId
            | :? FunctionalJournalView as view ->
                journalCodecIds.Enqueue view.CodecId
            | :? FunctionalJournalEntry as entry ->
                journalEntryCodecIds.Enqueue entry.CodecId
            | :? string -> ()
            | :? (byte[]) -> ()
            | :? IEnumerable as items when depth > 0 ->
                for item in items do
                    inspect (depth - 1) item
            | _ when depth > 0 ->
                let candidateType = value.GetType()
                let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

                seq {
                    for property in candidateType.GetProperties flags do
                        if property.GetIndexParameters().Length = 0 then
                            try
                                let candidate = property.GetValue value
                                if not (obj.ReferenceEquals(candidate, value)) then
                                    yield candidate
                            with _ ->
                                ()

                    for field in candidateType.GetFields flags do
                        try
                            let candidate = field.GetValue value
                            if not (obj.ReferenceEquals(candidate, value)) then
                                yield candidate
                        with _ ->
                            ()
                }
                |> Seq.iter (inspect (depth - 1))
            | _ -> ()

    let rec private tryFindJournalView depth (value: obj) =
        if depth < 0 || isNull value then
            None
        else
            match value with
            | :? FunctionalJournalView as view -> Some view
            | _ when depth > 0 ->
                let candidateType = value.GetType()
                let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

                seq {
                    for property in candidateType.GetProperties flags do
                        if property.GetIndexParameters().Length = 0 then
                            try
                                yield property.GetValue value
                            with _ ->
                                ()

                    for field in candidateType.GetFields flags do
                        try
                            yield field.GetValue value
                        with _ ->
                            ()
                }
                |> Seq.filter (fun candidate -> not (obj.ReferenceEquals(candidate, value)))
                |> Seq.tryPick (tryFindJournalView (depth - 1))
            | _ -> None

    let corruptJournalViewFor (grainKey: string) =
        records
        |> Seq.filter (fun pair -> pair.Key.Contains(grainKey, StringComparison.Ordinal))
        |> Seq.tryPick (fun pair ->
            let state, _ = pair.Value
            tryFindJournalView 8 state)
        |> function
            | Some view ->
                view.Payload <- null
                true
            | None -> false

    let reset () =
        records.Clear()
        envelopeCodecIds.Clear()
        journalCodecIds.Clear()
        journalEntryCodecIds.Clear()

[<Sealed>]
type private CodecCaptureStorage() =
    interface IGrainStorage with
        member _.ReadStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            match CodecCapture.records.TryGetValue(CodecCapture.key stateName grainId) with
            | true, (state, etag) ->
                grainState.State <- unbox<'T> state
                grainState.ETag <- etag
                grainState.RecordExists <- true
            | _ -> grainState.RecordExists <- false

            Task.CompletedTask

        member _.WriteStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            let state = box grainState.State
            let etag = Guid.NewGuid().ToString "N"
            CodecCapture.inspect 3 state
            CodecCapture.records.[CodecCapture.key stateName grainId] <- state, etag
            grainState.ETag <- etag
            grainState.RecordExists <- true
            Task.CompletedTask

        member _.ClearStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            CodecCapture.records.TryRemove(CodecCapture.key stateName grainId) |> ignore
            grainState.ETag <- null
            grainState.RecordExists <- false
            Task.CompletedTask

[<RequireQualifiedAccess>]
module private Activations =
    let counts = ConcurrentDictionary<string, int>()

    let bump key = counts.AddOrUpdate(key, 1, fun _ count -> count + 1) |> ignore

    let count key =
        match counts.TryGetValue key with
        | true, value -> value
        | _ -> 0

type JsonStateActor = private JsonStateActor of unit

[<NoEquality; NoComparison>]
type JsonStateApi =
    { write: string -> Task<unit>
      read: unit -> Task<string>
      goAway: unit -> Task<unit> }

let private jsonState = PersistentState.create<string> "json-state" "CodecStore"

let private jsonStateContract =
    grainContract<JsonStateActor, string, JsonStateApi> {
        grainType "codec.integration.state"
        stringKey
    }

let private jsonStateDefinition =
    grainFor jsonStateContract {
        defaultState (fun () -> "initial")
        stateFrom jsonState

        onActivate (fun context state ->
            Activations.bump $"state:{context.key}"
            Task.FromResult state)

        handle (_.write) (fun context _ value ->
            task {
                let facet = context.persistentState jsonState
                facet.State <- value
                do! facet.WriteStateAsync()
                return value, ()
            })

        handle (_.read) (fun _ state () -> task { return state, state })

        handle (_.goAway) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return state, ()
            })
    }

let private jsonStateRef = FunctionalGrain.ref jsonStateContract

type JsonJournalActor = private JsonJournalActor of unit

type JsonLogJournalActor = private JsonLogJournalActor of unit

[<NoEquality; NoComparison>]
type JsonJournalApi =
    { add: int -> Task<unit>
      total: unit -> Task<int>
      goAway: unit -> Task<unit> }

type JsonJournalState = { Total: int }
type JsonJournalEvent = Added of int

[<Sealed>]
type private TaggedJournalStateConverter() =
    inherit JsonConverter<JsonJournalState>()

    override _.Write(writer: Utf8JsonWriter, value: JsonJournalState, _options: JsonSerializerOptions) =
        writer.WriteStringValue $"total:{value.Total}"

    override _.Read
        (
            reader: byref<Utf8JsonReader>,
            _typeToConvert: Type,
            _options: JsonSerializerOptions
        ) =
        let value = reader.GetString()

        if isNull value || not (value.StartsWith("total:", StringComparison.Ordinal)) then
            raise (JsonException "Expected a tagged journal state.")

        { Total = Int32.Parse(value.Substring "total:".Length) }

let private historicalJournalCodec =
    let options = JsonSerializerOptions(FSharpJson.serializerOptions)
    options.Converters.Insert(0, TaggedJournalStateConverter())
    FunctionalPersistenceCodec.CreateFSharpJson("codec-integration-json-v1", options)

let private migratedJournalCodec =
    FunctionalPersistenceCodec.OrleansBinary.WithReadCodec historicalJournalCodec

let private jsonJournalContract =
    grainContract<JsonJournalActor, string, JsonJournalApi> {
        grainType "codec.integration.journal"
        stringKey
    }

let private jsonJournalDefinition =
    journaledGrainFor jsonJournalContract {
        initialEventState (fun (_: string) -> { Total = 0 })
        apply (fun state (Added amount) -> { Total = state.Total + amount })
        logProvider "CodecJournal"
        journalStorage "CodecStore"

        onActivate (fun context _ ->
            Activations.bump $"journal:{context.key}"
            Task.FromResult(()))

        handle (_.add) (fun _ _ amount -> task { return [ Added amount ], () })
        handle (_.total) (fun _ state () -> task { return [], state.Total })

        handle (_.goAway) (fun context _ () ->
            task {
                context.deactivateOnIdle ()
                return [], ()
            })
    }

let private jsonJournalRef = FunctionalGrain.ref jsonJournalContract

let private jsonLogJournalContract =
    grainContract<JsonLogJournalActor, string, JsonJournalApi> {
        grainType "codec.integration.journal-log"
        stringKey
    }

let private jsonLogJournalDefinition =
    journaledGrainFor jsonLogJournalContract {
        initialEventState (fun (_: string) -> { Total = 0 })
        apply (fun state (Added amount) -> { Total = state.Total + amount })
        logProvider "CodecJournalLog"
        journalStorage "CodecStore"

        handle (_.add) (fun _ _ amount -> task { return [ Added amount ], () })
        handle (_.total) (fun _ state () -> task { return [], state.Total })

        handle (_.goAway) (fun context _ () ->
            task {
                context.deactivateOnIdle ()
                return [], ()
            })
    }

let private jsonLogJournalRef = FunctionalGrain.ref jsonLogJournalContract

type private JsonCodecSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.Services.AddKeyedSingleton<IGrainStorage>(
                "CodecStore",
                Func<IServiceProvider, obj, IGrainStorage>(fun _ _ -> CodecCaptureStorage() :> IGrainStorage)
            )
            |> ignore

            siloBuilder.AddStateStorageBasedLogConsistencyProvider "CodecJournal" |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider "CodecJournalLog" |> ignore

            siloBuilder.ConfigureFunctionalPersistence(fun options ->
                options.DefaultStateCodec <- FunctionalPersistenceCodec.FSharpJson
                options.DefaultJournalCodec <- historicalJournalCodec)
            |> ignore

            siloBuilder.AddFunctionalGrain jsonStateDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain jsonJournalDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain jsonLogJournalDefinition |> ignore

type private MigratedCodecSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.Services.AddKeyedSingleton<IGrainStorage>(
                "CodecStore",
                Func<IServiceProvider, obj, IGrainStorage>(fun _ _ -> CodecCaptureStorage() :> IGrainStorage)
            )
            |> ignore

            siloBuilder.AddStateStorageBasedLogConsistencyProvider "CodecJournal" |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider "CodecJournalLog" |> ignore

            siloBuilder.ConfigureFunctionalPersistence(fun options ->
                options.DefaultStateCodec <- FunctionalPersistenceCodec.FSharpJson
                options.DefaultJournalCodec <- migratedJournalCodec)
            |> ignore

            siloBuilder.AddFunctionalGrain jsonStateDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain jsonJournalDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain jsonLogJournalDefinition |> ignore

type private JsonCodecClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

let private awaitReactivation key (read: unit -> Task<'T>) before =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 30.0

        while Activations.count key <= before && DateTime.UtcNow < deadline do
            do! Task.Delay 100
            let! _ = read ()
            ()

        Assert.True(Activations.count key > before, $"grain '{key}' did not reactivate")
    }

[<Fact>]
let ``JSON state and journal survive reactivation codec migration and corrupt views fail closed`` () =
    task {
        CodecCapture.reset ()
        let journalKey = $"journal-{Guid.NewGuid():N}"
        let logJournalKey = $"journal-log-{Guid.NewGuid():N}"
        let corruptKey = $"corrupt-{Guid.NewGuid():N}"

        let firstBuilder = TestClusterBuilder 1s
        firstBuilder.AddSiloBuilderConfigurator<JsonCodecSiloConfigurator>() |> ignore
        firstBuilder.AddClientBuilderConfigurator<JsonCodecClientConfigurator>() |> ignore
        let firstCluster = firstBuilder.Build()
        firstCluster.Deploy()

        try
            let stateKey = $"state-{Guid.NewGuid():N}"
            let stateApi = jsonStateRef firstCluster.Client stateKey

            do! stateApi.write "persisted as F# JSON"
            let! stateBefore = stateApi.read ()
            Assert.Equal("persisted as F# JSON", stateBefore)

            let stateActivation = Activations.count $"state:{stateKey}"
            do! stateApi.goAway ()
            do! awaitReactivation $"state:{stateKey}" stateApi.read stateActivation

            let! stateAfter = stateApi.read ()
            Assert.Equal("persisted as F# JSON", stateAfter)

            let journalApi = jsonJournalRef firstCluster.Client journalKey
            do! journalApi.add 4
            do! journalApi.add 7
            let! totalBefore = journalApi.total ()
            Assert.Equal(11, totalBefore)

            let journalActivation = Activations.count $"journal:{journalKey}"
            do! journalApi.goAway ()
            do! awaitReactivation $"journal:{journalKey}" journalApi.total journalActivation

            let! totalAfter = journalApi.total ()
            Assert.Equal(11, totalAfter)

            let logJournalApi = jsonLogJournalRef firstCluster.Client logJournalKey
            do! logJournalApi.add 5
            do! logJournalApi.add 6
            let! logTotalBefore = logJournalApi.total ()
            Assert.Equal(11, logTotalBefore)
        finally
            firstCluster.StopAllSilos()
            firstCluster.Dispose()

        // The write codec is now Orleans binary, but the historical custom JSON reader remains
        // registered. The first call must decode the old durable view before a new binary write.
        let migratedBuilder = TestClusterBuilder 1s
        migratedBuilder.AddSiloBuilderConfigurator<MigratedCodecSiloConfigurator>() |> ignore
        migratedBuilder.AddClientBuilderConfigurator<JsonCodecClientConfigurator>() |> ignore
        let migratedCluster = migratedBuilder.Build()
        migratedCluster.Deploy()

        try
            let journalApi = jsonJournalRef migratedCluster.Client journalKey
            let! migratedTotal = journalApi.total ()
            Assert.Equal(11, migratedTotal)

            do! journalApi.add 3
            let! totalWithBinaryWrite = journalApi.total ()
            Assert.Equal(14, totalWithBinaryWrite)

            let journalActivation = Activations.count $"journal:{journalKey}"
            do! journalApi.goAway ()
            do! awaitReactivation $"journal:{journalKey}" journalApi.total journalActivation

            let! totalAfterBinaryReplay = journalApi.total ()
            Assert.Equal(14, totalAfterBinaryReplay)

            let logJournalApi = jsonLogJournalRef migratedCluster.Client logJournalKey
            let! migratedLogTotal = logJournalApi.total ()
            Assert.Equal(11, migratedLogTotal)

            do! logJournalApi.add 3
            let! logTotalWithBinaryWrite = logJournalApi.total ()
            Assert.Equal(14, logTotalWithBinaryWrite)

            let corruptApi = jsonJournalRef migratedCluster.Client corruptKey
            do! corruptApi.add 1
            do! corruptApi.goAway ()
        finally
            migratedCluster.StopAllSilos()
            migratedCluster.Dispose()

        Assert.Contains("fsharp-json-v1", CodecCapture.envelopeCodecIds)
        Assert.Contains(historicalJournalCodec.Id, CodecCapture.journalCodecIds)
        Assert.Contains(FunctionalPersistenceCodec.OrleansBinary.Id, CodecCapture.journalCodecIds)
        Assert.Contains(historicalJournalCodec.Id, CodecCapture.journalEntryCodecIds)
        Assert.Contains(FunctionalPersistenceCodec.OrleansBinary.Id, CodecCapture.journalEntryCodecIds)
        Assert.True(CodecCapture.corruptJournalViewFor corruptKey, "journal view was not found in durable storage")

        // A fresh activation must refuse the corrupted durable view instead of treating it as the
        // initial state and accepting new events on the wrong base.
        let corruptBuilder = TestClusterBuilder 1s
        corruptBuilder.AddSiloBuilderConfigurator<MigratedCodecSiloConfigurator>() |> ignore
        corruptBuilder.AddClientBuilderConfigurator<JsonCodecClientConfigurator>() |> ignore
        let corruptCluster = corruptBuilder.Build()
        corruptCluster.Deploy()

        try
            // LogStorage must replay the old JSON entries and the new binary entry together.
            let logJournalApi = jsonLogJournalRef corruptCluster.Client logJournalKey
            let! replayedMixedLogTotal = logJournalApi.total ()
            Assert.Equal(14, replayedMixedLogTotal)

            let corruptApi = jsonJournalRef corruptCluster.Client corruptKey

            let! error =
                Assert.ThrowsAnyAsync<Exception>(
                    Func<Task>(fun () ->
                        (task {
                            let! _ = corruptApi.total ()
                            return ()
                         }
                         :> Task))
                )

            Assert.True(
                error.ToString().Contains("null payload", StringComparison.OrdinalIgnoreCase),
                error.ToString()
            )
        finally
            corruptCluster.StopAllSilos()
            corruptCluster.Dispose()
    }
