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
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.EventSourcing
open Orleans.EventSourcing.CustomStorage
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

[<Literal>]
let private SnapshotProvider = "JournalHostingSnapshots"

// ──────────────────────────────────────────────────────────────────────────────
// One journaled definition per hosting scenario
// ──────────────────────────────────────────────────────────────────────────────

type NoteState = { notes: string list }

type NoteEvent = Noted of string

[<NoEquality; NoComparison>]
type NoteApi =
    { note: string -> Task<int>
      notes: unit -> Task<string list>
      recycle: unit -> Task<unit> }

type StockNoteActor = private StockNoteActor of unit
type CustomNoteActor = private CustomNoteActor of unit
type DefaultStorageNoteActor = private DefaultStorageNoteActor of unit
type MissingProviderActor = private MissingProviderActor of unit
type MissingStorageActor = private MissingStorageActor of unit
type MissingCustomStorageActor = private MissingCustomStorageActor of unit

let private noteDefinition (contract: GrainContract<'Actor, string, NoteApi>) (provider: string) (storage: string) =
    journaledGrainFor contract {
        initialEventState (fun (_: string) -> { notes = [] })
        apply (fun state (Noted note) -> { notes = state.notes @ [ note ] })
        logProvider provider
        journalStorage storage

        handle (_.note) (fun _ state (text: string) -> task { return [ Noted text ], List.length state.notes + 1 })

        handle (_.notes) (fun _ state () -> task { return ([]: NoteEvent list), state.notes })

        handle (_.recycle) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return [], ()
            })
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

let private defaultStorageContract =
    grainContract<DefaultStorageNoteActor, string, NoteApi> {
        grainType "journalhosting.defaultstorage"
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

let private missingCustomStorageContract =
    grainContract<MissingCustomStorageActor, string, NoteApi> {
        grainType "journalhosting.missingcustomstorage"
        stringKey
    }

let private stockDefinition = noteDefinition stockContract StockProvider JournalStore
let private customDefinition = noteDefinition customContract CustomProvider JournalStore

let private defaultStorageDefinition =
    journaledGrainFor defaultStorageContract {
        initialEventState (fun (_: string) -> { notes = [] })
        apply (fun state (Noted note) -> { notes = state.notes @ [ note ] })
        logProvider StockProvider

        handle (_.note) (fun _ state (text: string) -> task { return [ Noted text ], List.length state.notes + 1 })

        handle (_.notes) (fun _ state () -> task { return ([]: NoteEvent list), state.notes })

        handle (_.recycle) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return [], ()
            })
    }

let private missingProviderDefinition =
    noteDefinition missingProviderContract "NoSuchLogConsistencyProvider" JournalStore

let private missingStorageDefinition =
    noteDefinition missingStorageContract StockProvider "NoSuchJournalStore"

let private missingCustomStorageDefinition =
    noteDefinition missingCustomStorageContract SnapshotProvider JournalStore

// ──────────────────────────────────────────────────────────────────────────────
// Typed CustomStorage bridge and snapshot probes
// ──────────────────────────────────────────────────────────────────────────────

[<NoEquality; NoComparison>]
type SnapshotNoteApi =
    { append: string list -> Task<int>
      snapshot: unit -> Task<unit>
      forceAppend: string -> Task<int>
      clear: unit -> Task<unit>
      notes: unit -> Task<string list>
      recycle: unit -> Task<unit> }

type SnapshotNoteActor = private SnapshotNoteActor of unit
type DisabledSnapshotNoteActor = private DisabledSnapshotNoteActor of unit
type ConditionalSnapshotNoteActor = private ConditionalSnapshotNoteActor of unit
type WrongSnapshotProviderActor = private WrongSnapshotProviderActor of unit

[<ReferenceEquality>]
type private StoredSnapshotJournal =
    { Version: int
      Snapshot: FunctionalJournalSnapshot<NoteState> option
      Tail: NoteEvent list }

[<Sealed>]
type private SnapshotJournalStorage() =
    let gate = obj ()
    let values = Dictionary<string, StoredSnapshotJournal>(StringComparer.Ordinal)

    let storageKey (grainTypeName: string) (key: string) = $"{grainTypeName}|{key}"

    let empty =
        { Version = 0
          Snapshot = None
          Tail = [] }

    member _.SnapshotVersion(grainTypeName: string, key: string) =
        lock gate (fun () ->
            match values.TryGetValue(storageKey grainTypeName key) with
            | true, value -> value.Snapshot |> Option.map _.Version
            | false, _ -> None)

    member _.TailCount(grainTypeName: string, key: string) =
        lock gate (fun () ->
            match values.TryGetValue(storageKey grainTypeName key) with
            | true, value -> value.Tail.Length
            | false, _ -> 0)

    interface IFunctionalJournalStorage<string, NoteState, NoteEvent> with
        member _.Read(identity) =
            let read =
                lock gate (fun () ->
                    let key = storageKey identity.GrainTypeName identity.Key

                    let stored =
                        match values.TryGetValue key with
                        | true, value -> value
                        | false, _ -> empty

                    { Snapshot = stored.Snapshot
                      Events = stored.Tail |> List.toArray :> IReadOnlyList<NoteEvent> })

            Task.FromResult read

        member _.Append(identity, write) =
            let accepted =
                lock gate (fun () ->
                    let key = storageKey identity.GrainTypeName identity.Key

                    let stored =
                        match values.TryGetValue key with
                        | true, value -> value
                        | false, _ -> empty

                    if stored.Version <> write.ExpectedVersion then
                        false
                    else
                        let resultingVersion = write.ExpectedVersion + write.Events.Count

                        match write.Snapshot with
                        | Some snapshot when snapshot.Version <> resultingVersion ->
                            invalidOp
                                $"snapshot version {snapshot.Version} does not match resulting version {resultingVersion}"
                        | Some snapshot ->
                            values.[key] <-
                                { Version = resultingVersion
                                  Snapshot = Some snapshot
                                  Tail = [] }
                        | None ->
                            values.[key] <-
                                { stored with
                                    Version = resultingVersion
                                    Tail = stored.Tail @ (write.Events |> Seq.toList) }

                        true)

            Task.FromResult accepted

        member _.Clear(identity) =
            lock gate (fun () -> values.Remove(storageKey identity.GrainTypeName identity.Key) |> ignore)
            Task.CompletedTask

let private snapshotJournalStorage = SnapshotJournalStorage()

let private snapshotContract =
    grainContract<SnapshotNoteActor, string, SnapshotNoteApi> {
        grainType "journalhosting.snapshots"
        stringKey
        readOnly (_.notes)
    }

let private disabledSnapshotContract =
    grainContract<DisabledSnapshotNoteActor, string, SnapshotNoteApi> {
        grainType "journalhosting.snapshots.disabled"
        stringKey
        readOnly (_.notes)
    }

let private conditionalSnapshotContract =
    grainContract<ConditionalSnapshotNoteActor, string, SnapshotNoteApi> {
        grainType "journalhosting.snapshots.conditional"
        stringKey
        readOnly (_.notes)
    }

let private wrongSnapshotProviderContract =
    grainContract<WrongSnapshotProviderActor, string, SnapshotNoteApi> {
        grainType "journalhosting.snapshots.wrongprovider"
        stringKey
        readOnly (_.notes)
    }

let private snapshotDefinition
    (contract: GrainContract<'Actor, string, SnapshotNoteApi>)
    (providerName: string)
    (policyOverride: FunctionalJournalSnapshotPolicy<NoteState> option)
    =
    let resolve (services: IServiceProvider) =
        services.GetRequiredService<SnapshotJournalStorage>()
        :> IFunctionalJournalStorage<string, NoteState, NoteEvent>

    let append (_: FunctionalGrainContext<'Actor, string>) (state: NoteState) (notes: string list) =
        task {
            let events = notes |> List.map Noted
            return events, state.notes.Length + List.length events
        }

    let snapshot (context: FunctionalGrainContext<'Actor, string>) (_: NoteState) () =
        task {
            context.snapshotNow ()
            return [], ()
        }

    let forceAppend (context: FunctionalGrainContext<'Actor, string>) (state: NoteState) (note: string) =
        task {
            context.snapshotNow ()
            return [ Noted note ], state.notes.Length + 1
        }

    let clear (context: FunctionalGrainContext<'Actor, string>) (_: NoteState) () =
        task {
            do! context.clearJournal ()
            return [], ()
        }

    let notes (_: FunctionalGrainContext<'Actor, string>) (state: NoteState) () = task { return state.notes }

    let recycle (context: FunctionalGrainContext<'Actor, string>) (_: NoteState) () =
        task {
            context.deactivateOnIdle ()
            return [], ()
        }

    match policyOverride with
    | None ->
        journaledGrainFor contract {
            initialEventState (fun (_: string) -> ({ notes = [] }: NoteState))
            apply (fun (state: NoteState) (Noted note) -> { notes = state.notes @ [ note ] })
            logProvider providerName
            customStorage resolve
            handle (_.append) append
            handle (_.snapshot) snapshot
            handle (_.forceAppend) forceAppend
            handle (_.clear) clear
            handleQuery (_.notes) notes
            handle (_.recycle) recycle
        }
    | Some policyValue ->
        journaledGrainFor contract {
            initialEventState (fun (_: string) -> ({ notes = [] }: NoteState))
            apply (fun (state: NoteState) (Noted note) -> { notes = state.notes @ [ note ] })
            logProvider providerName
            customStorage resolve
            snapshotPolicy policyValue
            handle (_.append) append
            handle (_.snapshot) snapshot
            handle (_.forceAppend) forceAppend
            handle (_.clear) clear
            handleQuery (_.notes) notes
            handle (_.recycle) recycle
        }

let private inheritedSnapshotDefinition = snapshotDefinition snapshotContract SnapshotProvider None

let private disabledSnapshotDefinition =
    snapshotDefinition disabledSnapshotContract SnapshotProvider (Some FunctionalJournalSnapshotPolicy.Disabled)

let private conditionalSnapshotDefinition =
    snapshotDefinition
        conditionalSnapshotContract
        SnapshotProvider
        (Some(
            FunctionalJournalSnapshotPolicy.When(fun version state ->
                version >= 2 && state.notes |> List.contains "snapshot-me")
        ))

let private wrongSnapshotProviderDefinition =
    snapshotDefinition wrongSnapshotProviderContract StockProvider None

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
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore

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
            siloBuilder.AddFunctionalJournaledGrain defaultStorageDefinition |> ignore

/// <summary>A real Orleans CustomStorage provider backed by the typed functional bridge.</summary>
type SnapshotStorageSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddCustomStorageBasedLogConsistencyProvider SnapshotProvider |> ignore
            siloBuilder.UseFunctionalJournalSnapshots 3 |> ignore

            siloBuilder.Services.AddSingleton<SnapshotJournalStorage>(snapshotJournalStorage)
            |> ignore

            siloBuilder.AddFunctionalJournaledGrain inheritedSnapshotDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain disabledSnapshotDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain conditionalSnapshotDefinition |> ignore

/// <summary>A stock provider paired with a custom-storage declaration: rejected at startup.</summary>
type WrongSnapshotProviderSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider StockProvider |> ignore

            siloBuilder.Services.AddSingleton<SnapshotJournalStorage>(snapshotJournalStorage)
            |> ignore

            siloBuilder.AddFunctionalJournaledGrain wrongSnapshotProviderDefinition |> ignore

/// <summary>Orleans CustomStorage with no typed implementation declared by the definition.</summary>
type MissingCustomStorageSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddCustomStorageBasedLogConsistencyProvider SnapshotProvider |> ignore
            siloBuilder.AddFunctionalJournaledGrain missingCustomStorageDefinition |> ignore

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

[<Fact>]
let ``custom storage snapshots compact tails reload state and honor explicit precedence`` () =
    let builder = TestClusterBuilder 1s
    builder.AddSiloBuilderConfigurator<SnapshotStorageSiloConfigurator>() |> ignore
    builder.AddClientBuilderConfigurator<JournalHostingClientConfigurator>() |> ignore
    let cluster = builder.Build()
    cluster.Deploy()
    cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()

    task {
        try
            let inheritedKey = $"inherited-{Guid.NewGuid():N}"
            let inherited = FunctionalGrain.ref snapshotContract cluster.Client inheritedKey

            let! firstCount = inherited.append [ "one"; "two" ]
            Assert.Equal(2, firstCount)
            Assert.Equal(None, snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots", inheritedKey))
            Assert.Equal(2, snapshotJournalStorage.TailCount("journalhosting.snapshots", inheritedKey))

            // Every 3 is boundary based: a two-event batch from version 2 to 4 must not miss it.
            let! secondCount = inherited.append [ "three"; "four" ]
            Assert.Equal(4, secondCount)
            Assert.Equal(Some 4, snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots", inheritedKey))
            Assert.Equal(0, snapshotJournalStorage.TailCount("journalhosting.snapshots", inheritedKey))

            let! fifthCount = inherited.append [ "five" ]
            Assert.Equal(5, fifthCount)
            Assert.Equal(1, snapshotJournalStorage.TailCount("journalhosting.snapshots", inheritedKey))

            do! inherited.recycle ()
            do! Task.Delay 1500

            let! reloaded = inherited.notes ()
            Assert.Equal<string list>([ "one"; "two"; "three"; "four"; "five" ], reloaded)

            do! inherited.clear ()
            let! cleared = inherited.notes ()
            Assert.Empty cleared
            Assert.Equal(None, snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots", inheritedKey))
            Assert.Equal(0, snapshotJournalStorage.TailCount("journalhosting.snapshots", inheritedKey))

            let disabledKey = $"disabled-{Guid.NewGuid():N}"
            let disabled = FunctionalGrain.ref disabledSnapshotContract cluster.Client disabledKey

            let! disabledCount = disabled.append [ "one" ]
            Assert.Equal(1, disabledCount)
            Assert.Equal(
                None,
                snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots.disabled", disabledKey)
            )
            Assert.Equal(1, snapshotJournalStorage.TailCount("journalhosting.snapshots.disabled", disabledKey))

            // Manual force beats the definition's Disabled override, including a zero-event write.
            do! disabled.snapshot ()
            Assert.Equal(
                Some 1,
                snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots.disabled", disabledKey)
            )
            Assert.Equal(0, snapshotJournalStorage.TailCount("journalhosting.snapshots.disabled", disabledKey))

            let! forcedCount = disabled.forceAppend "two"
            Assert.Equal(2, forcedCount)
            Assert.Equal(
                Some 2,
                snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots.disabled", disabledKey)
            )
            Assert.Equal(0, snapshotJournalStorage.TailCount("journalhosting.snapshots.disabled", disabledKey))

            let conditionalKey = $"conditional-{Guid.NewGuid():N}"

            let conditional =
                FunctionalGrain.ref conditionalSnapshotContract cluster.Client conditionalKey

            let! _ = conditional.append [ "ordinary" ]
            Assert.Equal(
                None,
                snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots.conditional", conditionalKey)
            )

            let! _ = conditional.append [ "snapshot-me" ]
            Assert.Equal(
                Some 2,
                snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots.conditional", conditionalKey)
            )
            Assert.Equal(
                0,
                snapshotJournalStorage.TailCount("journalhosting.snapshots.conditional", conditionalKey)
            )
        finally
            cluster.StopAllSilos()
            cluster.Dispose()
    }

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

            let defaulted =
                FunctionalGrain.ref defaultStorageContract cluster.Client $"default-{Guid.NewGuid():N}"

            let! stockCount = stock.note "from the stock provider"
            let! customCount = custom.note "from the custom provider"
            let! defaultCount = defaulted.note "from default storage"

            Assert.Equal(1, stockCount)
            Assert.Equal(1, customCount)
            Assert.Equal(1, defaultCount)

            let! stockNotes = stock.notes ()
            let! customNotes = custom.notes ()

            do! defaulted.recycle ()
            do! Task.Delay 1500

            let! defaultNotes = defaulted.notes ()

            Assert.Equal<string list>([ "from the stock provider" ], stockNotes)
            Assert.Equal<string list>([ "from the custom provider" ], customNotes)
            Assert.Equal<string list>([ "from default storage" ], defaultNotes)
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

[<Fact>]
let ``customStorage requires Orleans CustomStorage provider`` () =
    let reported = deployExpectingFailure<WrongSnapshotProviderSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "journalhosting.snapshots.wrongprovider"))
    Assert.Contains(reported, (fun message -> message.Contains "declares 'customStorage'"))
    Assert.Contains(reported, (fun message -> message.Contains "AddCustomStorageBasedLogConsistencyProvider"))

[<Fact>]
let ``Orleans CustomStorage provider requires a typed customStorage declaration`` () =
    let reported = deployExpectingFailure<MissingCustomStorageSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "journalhosting.missingcustomstorage"))
    Assert.Contains(reported, (fun message -> message.Contains "uses Orleans CustomStorage"))
    Assert.Contains(reported, (fun message -> message.Contains "declares no 'customStorage'"))

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
