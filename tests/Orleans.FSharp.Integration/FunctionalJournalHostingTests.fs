/// <summary>
/// Spec 004 item 3, hosting: what silo startup validates for a journaled definition, and the
/// composition claim that makes a third-party log-consistency provider a drop-in.
/// </summary>
/// <remarks>
/// Every rejection is paired with the positive control that differs from it in exactly the one
/// respect the rule is about — a startup-failure assertion on its own cannot tell "the rule fired"
/// from "the silo could not start for some unrelated reason".
/// </remarks>
module Orleans.FSharp.Integration.FunctionalJournalHostingTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.EventSourcing
open Orleans.Hosting
open Orleans.Storage
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Names
// ──────────────────────────────────────────────────────────────────────────────

[<Literal>]
let private StockProvider = "JournalHostingStock"

[<Literal>]
let private CustomProvider = "JournalHostingCustom"

[<Literal>]
let private JournalStore = "JournalHostingStore"

// ──────────────────────────────────────────────────────────────────────────────
// One journaled definition per hosting scenario
// ──────────────────────────────────────────────────────────────────────────────

type NoteState = { notes: string list }

type NoteEvent = Noted of string

[<NoEquality; NoComparison>]
type NoteApi =
    { note: string -> Task<int>
      notes: unit -> Task<string list> }

type StockNoteActor = private StockNoteActor of unit
type CustomNoteActor = private CustomNoteActor of unit
type MissingProviderActor = private MissingProviderActor of unit
type MissingStorageActor = private MissingStorageActor of unit

let private noteDefinition (contract: GrainContract<'Actor, string, NoteApi>) (provider: string) (storage: string) =
    journaledGrainFor contract {
        initialEventState (fun (_: string) -> { notes = [] })
        apply (fun state (Noted note) -> { notes = state.notes @ [ note ] })
        logProvider provider
        journalStorage storage

        handle (_.note) (fun _ state (text: string) -> task { return [ Noted text ], List.length state.notes + 1 })

        handle (_.notes) (fun _ state () -> task { return ([]: NoteEvent list), state.notes })
    }

let private stockContract =
    grainContract<StockNoteActor, string, NoteApi> {
        grainType "journalhosting.stock"
        stringKey
    }

let private customContract =
    grainContract<CustomNoteActor, string, NoteApi> {
        grainType "journalhosting.custom"
        stringKey
    }

let private missingProviderContract =
    grainContract<MissingProviderActor, string, NoteApi> {
        grainType "journalhosting.missingprovider"
        stringKey
    }

let private missingStorageContract =
    grainContract<MissingStorageActor, string, NoteApi> {
        grainType "journalhosting.missingstorage"
        stringKey
    }

let private stockDefinition = noteDefinition stockContract StockProvider JournalStore
let private customDefinition = noteDefinition customContract CustomProvider JournalStore

let private missingProviderDefinition =
    noteDefinition missingProviderContract "NoSuchLogConsistencyProvider" JournalStore

let private missingStorageDefinition =
    noteDefinition missingStorageContract StockProvider "NoSuchJournalStore"

// ──────────────────────────────────────────────────────────────────────────────
// Silo configurations
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The composition claim: a log-consistency provider registered by hand, under a name of its own,
/// is resolved by the functional runtime exactly like a stock one. It is a different Orleans
/// provider implementation from the stock registration alongside it, so "the name was resolved"
/// cannot be confused with "the stock provider happened to serve it".
/// </summary>
/// <remarks>
/// This is the shape any third-party adapter package takes: register an
/// <c>ILogViewAdaptorFactory</c> under a name and let applications name it. Nothing
/// functional-specific is needed on either side.
/// </remarks>
type CustomProviderSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.Services.AddKeyedSingleton<IGrainStorage>(
                JournalStore,
                Func<IServiceProvider, obj, IGrainStorage>(fun _ _ ->
                    FunctionalStateRestartTests.RetainedGrainStorage JournalStore :> IGrainStorage)
            )
            |> ignore

            // The stock call is what registers Factory<IGrainContext, ILogConsistencyProtocolServices>;
            // AddLogConsistencyProtocolServicesFactory itself is internal to Orleans, so a
            // hand-registered provider has to ride along with a stock one. That constraint is the
            // subject of the negative control below.
            siloBuilder.AddLogStorageBasedLogConsistencyProvider StockProvider |> ignore

            siloBuilder.Services.AddKeyedSingleton<ILogViewAdaptorFactory>(
                CustomProvider,
                Func<IServiceProvider, obj, ILogViewAdaptorFactory>(fun _ _ ->
                    Orleans.EventSourcing.StateStorage.LogConsistencyProvider() :> ILogViewAdaptorFactory)
            )
            |> ignore

            siloBuilder.AddFunctionalJournaledGrain stockDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain customDefinition |> ignore

/// <summary>A silo whose journaled definition names a provider nobody registered.</summary>
type MissingLogProviderSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage JournalStore |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider StockProvider |> ignore
            siloBuilder.AddFunctionalJournaledGrain missingProviderDefinition |> ignore

/// <summary>A silo whose journaled definition names a journal storage nobody registered.</summary>
type MissingJournalStorageSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage JournalStore |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider StockProvider |> ignore
            siloBuilder.AddFunctionalJournaledGrain missingStorageDefinition |> ignore

/// <summary>
/// A silo with a hand-registered provider and NO stock log-consistency registration at all, so the
/// protocol-services factory Orleans' adaptors need is absent.
/// </summary>
type OrphanProviderSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage JournalStore |> ignore

            siloBuilder.Services.AddKeyedSingleton<ILogViewAdaptorFactory>(
                CustomProvider,
                Func<IServiceProvider, obj, ILogViewAdaptorFactory>(fun _ _ ->
                    Orleans.EventSourcing.LogStorage.LogConsistencyProvider() :> ILogViewAdaptorFactory)
            )
            |> ignore

            siloBuilder.AddFunctionalJournaledGrain customDefinition |> ignore

type JournalHostingClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────────────────────────────────────

let rec private messages (error: exn) : string list =
    match error with
    | null -> []
    | :? AggregateException as aggregate ->
        error.Message :: (aggregate.InnerExceptions |> Seq.collect messages |> List.ofSeq)
    | _ -> error.Message :: messages error.InnerException

let private deployExpectingFailure<'Configurator
    when 'Configurator :> ISiloConfigurator and 'Configurator: (new: unit -> 'Configurator)>
    ()
    =
    let builder = TestClusterBuilder 1s
    builder.AddSiloBuilderConfigurator<'Configurator>() |> ignore
    let cluster = builder.Build()

    let error = Assert.ThrowsAny<exn>(fun () -> cluster.Deploy())

    try
        try
            cluster.StopAllSilos()
        with _ ->
            ()
    finally
        cluster.Dispose()

    messages error

// ──────────────────────────────────────────────────────────────────────────────
// Tests
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// The positive control for all three startup rejections below, and the composition claim in one:
/// two journaled definitions on one silo, one on a stock provider registration and one on a
/// hand-registered provider of a different implementation, both working.
/// </remarks>
[<Fact>]
let ``a hand-registered log-consistency provider serves a journaled definition`` () =
    let builder = TestClusterBuilder 1s
    builder.AddSiloBuilderConfigurator<CustomProviderSiloConfigurator>() |> ignore
    builder.AddClientBuilderConfigurator<JournalHostingClientConfigurator>() |> ignore
    let cluster = builder.Build()
    cluster.Deploy()
    cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()

    task {
        try
            let stock =
                FunctionalGrain.ref stockContract cluster.Client $"stock-{Guid.NewGuid():N}"

            let custom =
                FunctionalGrain.ref customContract cluster.Client $"custom-{Guid.NewGuid():N}"

            let! stockCount = stock.note "from the stock provider"
            let! customCount = custom.note "from the custom provider"

            Assert.Equal(1, stockCount)
            Assert.Equal(1, customCount)

            let! stockNotes = stock.notes ()
            let! customNotes = custom.notes ()

            Assert.Equal<string list>([ "from the stock provider" ], stockNotes)
            Assert.Equal<string list>([ "from the custom provider" ], customNotes)
        finally
            cluster.StopAllSilos()
            cluster.Dispose()
    }

[<Fact>]
let ``a journaled definition naming an unregistered log-consistency provider fails silo startup`` () =
    let reported = deployExpectingFailure<MissingLogProviderSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "Orleans.FSharp functional silo startup"))
    Assert.Contains(reported, (fun message -> message.Contains "NoSuchLogConsistencyProvider"))
    Assert.Contains(reported, (fun message -> message.Contains "which is not registered on this silo"))
    Assert.Contains(reported, (fun message -> message.Contains "journalhosting.missingprovider"))

[<Fact>]
let ``a journaled definition naming an unregistered journal storage fails silo startup`` () =
    let reported = deployExpectingFailure<MissingJournalStorageSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "Orleans.FSharp functional silo startup"))
    Assert.Contains(reported, (fun message -> message.Contains "NoSuchJournalStore"))
    Assert.Contains(reported, (fun message -> message.Contains "journalhosting.missingstorage"))

/// <remarks>
/// The one constraint a third-party adapter package has to know about, and the reason it is worth
/// a startup check of its own: <c>AddLogConsistencyProtocolServicesFactory</c> is internal to
/// Orleans, so a provider registered by hand has no way to register the factory its own adaptors
/// will be handed. Without this check the silo starts and every activation of the grain fails.
/// </remarks>
[<Fact>]
let ``a hand-registered provider without the protocol-services factory fails silo startup`` () =
    let reported = deployExpectingFailure<OrphanProviderSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "Orleans.FSharp functional silo startup"))
    Assert.Contains(reported, (fun message -> message.Contains "ILogConsistencyProtocolServices"))
    Assert.Contains(reported, (fun message -> message.Contains "AddLogConsistencyProtocolServicesFactory"))
