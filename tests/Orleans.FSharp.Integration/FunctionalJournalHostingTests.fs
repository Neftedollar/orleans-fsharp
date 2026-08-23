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
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
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
type GlobalSnapshotNoteActor = private GlobalSnapshotNoteActor of unit
type PoisonedSnapshotNoteActor = private PoisonedSnapshotNoteActor of unit
type WrongSnapshotProviderActor = private WrongSnapshotProviderActor of unit

[<ReferenceEquality>]
type private StoredSnapshotJournal =
    { Version: int
      Snapshot: FunctionalJournalSnapshot<NoteState> option
      Tail: NoteEvent list }

[<ReferenceEquality>]
type private StoragePlan =
    { mutable PermanentRead: bool
      mutable PermanentAppend: bool
      mutable PermanentClear: bool
      mutable TransientReadFailures: int
      mutable TransientAppendFailures: int
      mutable TransientClearFailures: int
      mutable RejectedAppends: int
      mutable ConflictEvent: NoteEvent option
      mutable MalformedSnapshot: bool
      mutable ReadAttempts: int
      mutable AppendAttempts: int
      mutable ClearAttempts: int }

[<Sealed>]
type private SnapshotJournalStorage() =
    let gate = obj ()
    let values = Dictionary<string, StoredSnapshotJournal>(StringComparer.Ordinal)
    let plans = Dictionary<string, StoragePlan>(StringComparer.Ordinal)

    let storageKey (grainTypeName: string) (key: string) = $"{grainTypeName}|{key}"

    let empty =
        { Version = 0
          Snapshot = None
          Tail = [] }

    let newPlan () =
        { PermanentRead = false
          PermanentAppend = false
          PermanentClear = false
          TransientReadFailures = 0
          TransientAppendFailures = 0
          TransientClearFailures = 0
          RejectedAppends = 0
          ConflictEvent = None
          MalformedSnapshot = false
          ReadAttempts = 0
          AppendAttempts = 0
          ClearAttempts = 0 }

    let planFor key =
        match plans.TryGetValue key with
        | true, plan -> plan
        | false, _ ->
            let plan = newPlan ()
            plans.Add(key, plan)
            plan

    member _.FailReadPermanently(grainTypeName: string, key: string) =
        lock gate (fun () -> (planFor (storageKey grainTypeName key)).PermanentRead <- true)

    member _.FailAppendPermanently(grainTypeName: string, key: string) =
        lock gate (fun () -> (planFor (storageKey grainTypeName key)).PermanentAppend <- true)

    member _.FailClearPermanently(grainTypeName: string, key: string) =
        lock gate (fun () -> (planFor (storageKey grainTypeName key)).PermanentClear <- true)

    member _.FailNextReads(grainTypeName: string, key: string, count: int) =
        lock gate (fun () -> (planFor (storageKey grainTypeName key)).TransientReadFailures <- count)

    member _.FailNextAppends(grainTypeName: string, key: string, count: int) =
        lock gate (fun () -> (planFor (storageKey grainTypeName key)).TransientAppendFailures <- count)

    member _.FailNextClears(grainTypeName: string, key: string, count: int) =
        lock gate (fun () -> (planFor (storageKey grainTypeName key)).TransientClearFailures <- count)

    member _.RejectNextAppends(grainTypeName: string, key: string, count: int) =
        lock gate (fun () -> (planFor (storageKey grainTypeName key)).RejectedAppends <- count)

    member _.ConflictOnNextAppend(grainTypeName: string, key: string, event: NoteEvent) =
        lock gate (fun () -> (planFor (storageKey grainTypeName key)).ConflictEvent <- Some event)

    member _.ReturnMalformedSnapshot(grainTypeName: string, key: string) =
        lock gate (fun () -> (planFor (storageKey grainTypeName key)).MalformedSnapshot <- true)

    member _.SeedTail(grainTypeName: string, key: string, events: NoteEvent list) =
        lock gate (fun () ->
            values.[storageKey grainTypeName key] <-
                { Version = events.Length
                  Snapshot = None
                  Tail = events })

    member _.ClearFaults(grainTypeName: string, key: string) =
        lock gate (fun () ->
            let plan = planFor (storageKey grainTypeName key)
            plan.PermanentRead <- false
            plan.PermanentAppend <- false
            plan.PermanentClear <- false
            plan.TransientReadFailures <- 0
            plan.TransientAppendFailures <- 0
            plan.TransientClearFailures <- 0
            plan.RejectedAppends <- 0
            plan.ConflictEvent <- None
            plan.MalformedSnapshot <- false)

    member _.Attempts(grainTypeName: string, key: string) =
        lock gate (fun () ->
            let plan = planFor (storageKey grainTypeName key)
            struct (plan.ReadAttempts, plan.AppendAttempts, plan.ClearAttempts))

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
            let outcome =
                lock gate (fun () ->
                    let key = storageKey identity.GrainTypeName identity.Key
                    let plan = planFor key
                    plan.ReadAttempts <- plan.ReadAttempts + 1

                    let stored =
                        match values.TryGetValue key with
                        | true, value -> value
                        | false, _ -> empty

                    if plan.PermanentRead then
                        Error(
                            FunctionalJournalPermanentStorageException(
                                $"permanent read failure for {identity.GrainTypeName}/{identity.Key}"
                            )
                            :> exn
                        )
                    elif plan.TransientReadFailures > 0 then
                        plan.TransientReadFailures <- plan.TransientReadFailures - 1
                        Error(IOException($"transient read failure for {identity.GrainTypeName}/{identity.Key}") :> exn)
                    elif plan.MalformedSnapshot then
                        Ok
                            { Snapshot =
                                Some
                                    { Version = -1
                                      State = { notes = [] } }
                              Events = Array.empty<NoteEvent> :> IReadOnlyList<NoteEvent> }
                    else
                        Ok
                            { Snapshot = stored.Snapshot
                              Events = stored.Tail |> List.toArray :> IReadOnlyList<NoteEvent> })

            match outcome with
            | Ok read -> Task.FromResult read
            | Error error -> Task.FromException<FunctionalJournalRead<NoteState, NoteEvent>> error

        member _.Append(identity, write) =
            let outcome =
                lock gate (fun () ->
                    let key = storageKey identity.GrainTypeName identity.Key
                    let plan = planFor key
                    plan.AppendAttempts <- plan.AppendAttempts + 1

                    let stored =
                        match values.TryGetValue key with
                        | true, value -> value
                        | false, _ -> empty

                    if plan.PermanentAppend then
                        Error(
                            FunctionalJournalPermanentStorageException(
                                $"permanent append failure for {identity.GrainTypeName}/{identity.Key}"
                            )
                            :> exn
                        )
                    elif plan.TransientAppendFailures > 0 then
                        plan.TransientAppendFailures <- plan.TransientAppendFailures - 1
                        Error(IOException($"transient append failure for {identity.GrainTypeName}/{identity.Key}") :> exn)
                    elif plan.ConflictEvent.IsSome then
                        let event = plan.ConflictEvent.Value
                        plan.ConflictEvent <- None

                        values.[key] <-
                            { stored with
                                Version = stored.Version + 1
                                Tail = stored.Tail @ [ event ] }

                        Ok false
                    elif plan.RejectedAppends > 0 then
                        plan.RejectedAppends <- plan.RejectedAppends - 1

                        // Model a real CAS conflict: another writer advances durable state before
                        // rejecting this expected version. The activation must synchronize after
                        // every rejection, including the final bounded attempt.
                        let external = Noted $"external-{stored.Version + 1}"

                        values.[key] <-
                            { stored with
                                Version = stored.Version + 1
                                Tail = stored.Tail @ [ external ] }

                        Ok false
                    elif stored.Version <> write.ExpectedVersion then
                        Ok false
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

                        Ok true)

            match outcome with
            | Ok accepted -> Task.FromResult accepted
            | Error error -> Task.FromException<bool> error

        member _.Clear(identity) =
            let outcome =
                lock gate (fun () ->
                    let key = storageKey identity.GrainTypeName identity.Key
                    let plan = planFor key
                    plan.ClearAttempts <- plan.ClearAttempts + 1

                    if plan.PermanentClear then
                        Error(
                            FunctionalJournalPermanentStorageException(
                                $"permanent clear failure for {identity.GrainTypeName}/{identity.Key}"
                            )
                            :> exn
                        )
                    elif plan.TransientClearFailures > 0 then
                        plan.TransientClearFailures <- plan.TransientClearFailures - 1
                        Error(IOException($"transient clear failure for {identity.GrainTypeName}/{identity.Key}") :> exn)
                    else
                        values.Remove key |> ignore
                        Ok())

            match outcome with
            | Ok() -> Task.CompletedTask
            | Error error -> Task.FromException error

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

let private globalSnapshotContract =
    grainContract<GlobalSnapshotNoteActor, string, SnapshotNoteApi> {
        grainType "journalhosting.snapshots.global"
        stringKey
        readOnly (_.notes)
    }

let private poisonedSnapshotContract =
    grainContract<PoisonedSnapshotNoteActor, string, SnapshotNoteApi> {
        grainType "journalhosting.snapshots.poisoned"
        stringKey
        readOnly (_.notes)
    }

let private wrongSnapshotProviderContract =
    grainContract<WrongSnapshotProviderActor, string, SnapshotNoteApi> {
        grainType "journalhosting.snapshots.wrongprovider"
        stringKey
        readOnly (_.notes)
    }

let private snapshotDefinitionWithApply
    (fold: NoteState -> NoteEvent -> NoteState)
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
            apply fold
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
            apply fold
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

let private snapshotDefinition
    (contract: GrainContract<'Actor, string, SnapshotNoteApi>)
    (providerName: string)
    (policyOverride: FunctionalJournalSnapshotPolicy<NoteState> option)
    =
    snapshotDefinitionWithApply
        (fun (state: NoteState) (Noted note) -> { notes = state.notes @ [ note ] })
        contract
        providerName
        policyOverride

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

let private globalSnapshotDefinition =
    snapshotDefinition globalSnapshotContract SnapshotProvider None

let private poisonedSnapshotDefinition =
    snapshotDefinitionWithApply
        (fun state (Noted note) ->
            if note = "poison" then
                invalidOp "poisoned retained event"

            { notes = state.notes @ [ note ] })
        poisonedSnapshotContract
        SnapshotProvider
        None

let private wrongSnapshotProviderDefinition =
    snapshotDefinition wrongSnapshotProviderContract StockProvider None

[<NoEquality; NoComparison>]
type private GlobalSnapshotObservation =
    { GrainTypeName: string
      Key: string
      Version: int
      StateType: Type
      Notes: string list }

let private globalSnapshotObservations = ConcurrentQueue<GlobalSnapshotObservation>()

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
            siloBuilder.AddFunctionalJournaledGrain poisonedSnapshotDefinition |> ignore

/// <summary>A conditional silo default used to prove context shape and failure behavior.</summary>
type GlobalSnapshotPolicySiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddCustomStorageBasedLogConsistencyProvider SnapshotProvider |> ignore

            siloBuilder.ConfigureFunctionalJournalSnapshots(
                Action<FunctionalJournalSnapshotOptions>(fun options ->
                    options.Policy <-
                        FunctionalJournalSnapshotDefault.When(fun context ->
                            let state = unbox<NoteState> context.State

                            globalSnapshotObservations.Enqueue
                                { GrainTypeName = context.GrainTypeName
                                  Key = unbox<string> context.Key
                                  Version = context.Version
                                  StateType = context.StateType
                                  Notes = state.notes }

                            if state.notes |> List.contains "policy-error" then
                                invalidOp "global snapshot predicate failed"

                            state.notes |> List.contains "global-snapshot"))
            )
            |> ignore

            siloBuilder.Services.AddSingleton<SnapshotJournalStorage>(snapshotJournalStorage)
            |> ignore

            siloBuilder.AddFunctionalJournaledGrain globalSnapshotDefinition |> ignore

/// <summary>A negative manual-snapshot retry budget is rejected before the silo serves calls.</summary>
type InvalidSnapshotRetrySiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddCustomStorageBasedLogConsistencyProvider SnapshotProvider |> ignore

            siloBuilder.ConfigureFunctionalJournalSnapshots(
                Action<FunctionalJournalSnapshotOptions>(fun options ->
                    options.ManualSnapshotMaxConflictRetries <- -1)
            )
            |> ignore

            siloBuilder.Services.AddSingleton<SnapshotJournalStorage>(snapshotJournalStorage)
            |> ignore

            siloBuilder.AddFunctionalJournaledGrain inheritedSnapshotDefinition |> ignore

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

let private expectFailureWithin (timeout: TimeSpan) (operation: Task) =
    Assert.ThrowsAnyAsync<exn>(Func<Task>(fun () -> operation.WaitAsync timeout))

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

[<Fact>]
let ``custom storage failures and manual snapshot conflicts terminate without losing durable state`` () =
    let builder = TestClusterBuilder 1s
    builder.AddSiloBuilderConfigurator<SnapshotStorageSiloConfigurator>() |> ignore
    builder.AddClientBuilderConfigurator<JournalHostingClientConfigurator>() |> ignore
    let cluster = builder.Build()
    cluster.Deploy()
    cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()

    task {
        try
            let transientReadKey = $"transient-read-{Guid.NewGuid():N}"

            snapshotJournalStorage.FailNextReads("journalhosting.snapshots", transientReadKey, 1)

            let transientRead = FunctionalGrain.ref snapshotContract cluster.Client transientReadKey
            let! initiallyEmpty = (transientRead.notes ()).WaitAsync(TimeSpan.FromSeconds 20.0)
            Assert.Empty initiallyEmpty

            let struct (transientReadAttempts, _, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", transientReadKey)

            Assert.Equal(2, transientReadAttempts)

            let transientAppendKey = $"transient-append-{Guid.NewGuid():N}"

            snapshotJournalStorage.FailNextAppends("journalhosting.snapshots", transientAppendKey, 1)

            let transientAppend = FunctionalGrain.ref snapshotContract cluster.Client transientAppendKey
            let! transientCount = (transientAppend.append [ "retried" ]).WaitAsync(TimeSpan.FromSeconds 20.0)
            Assert.Equal(1, transientCount)

            let struct (_, transientAppendAttempts, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", transientAppendKey)

            Assert.Equal(2, transientAppendAttempts)

            let directSnapshotKey = $"direct-snapshot-transient-{Guid.NewGuid():N}"
            let directSnapshot = FunctionalGrain.ref snapshotContract cluster.Client directSnapshotKey
            let! _ = directSnapshot.append [ "kept" ]

            let struct (_, beforeDirectSnapshotAttempts, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", directSnapshotKey)

            snapshotJournalStorage.FailNextAppends("journalhosting.snapshots", directSnapshotKey, 1)

            let! directSnapshotError =
                expectFailureWithin (TimeSpan.FromSeconds 5.0) (directSnapshot.snapshot () :> Task)

            Assert.Contains(
                messages directSnapshotError,
                fun message -> message.Contains "transient append failure"
            )

            let struct (_, afterDirectSnapshotAttempts, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", directSnapshotKey)

            // A zero-event snapshot bypasses Orleans' adaptor. Its ordinary exception surfaces
            // once to the caller; it neither enters the adaptor retry loop nor poisons the
            // activation. The caller can retry explicitly.
            Assert.Equal(1, afterDirectSnapshotAttempts - beforeDirectSnapshotAttempts)
            let! afterDirectSnapshotFailure = directSnapshot.notes ()
            Assert.Equal<string list>([ "kept" ], afterDirectSnapshotFailure)
            do! directSnapshot.snapshot ()

            Assert.Equal(
                Some 1,
                snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots", directSnapshotKey)
            )

            let permanentReadKey = $"permanent-read-{Guid.NewGuid():N}"

            snapshotJournalStorage.FailReadPermanently("journalhosting.snapshots", permanentReadKey)

            let permanentRead = FunctionalGrain.ref snapshotContract cluster.Client permanentReadKey

            let! permanentReadError =
                expectFailureWithin (TimeSpan.FromSeconds 5.0) (permanentRead.notes () :> Task)

            let permanentReadMessages = messages permanentReadError

            Assert.True(
                permanentReadMessages |> List.exists (fun message -> message.Contains "failed permanently"),
                String.concat Environment.NewLine permanentReadMessages
            )

            let struct (permanentReadAttempts, _, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", permanentReadKey)

            // Orleans may retry a failed activation as a whole, but each activation exits its
            // CustomStorage loop immediately. The client retry budget is bounded at three.
            Assert.InRange(permanentReadAttempts, 1, 3)
            snapshotJournalStorage.ClearFaults("journalhosting.snapshots", permanentReadKey)
            do! Task.Delay 750
            let! recoveredRead = permanentRead.notes ()
            Assert.Empty recoveredRead

            let permanentAppendKey = $"permanent-append-{Guid.NewGuid():N}"

            snapshotJournalStorage.FailAppendPermanently("journalhosting.snapshots", permanentAppendKey)

            let permanentAppend = FunctionalGrain.ref snapshotContract cluster.Client permanentAppendKey

            let! permanentAppendError =
                expectFailureWithin
                    (TimeSpan.FromSeconds 5.0)
                    (permanentAppend.append [ "not-durable" ] :> Task)

            Assert.Contains(messages permanentAppendError, fun message -> message.Contains "failed permanently")

            let struct (_, permanentAppendAttempts, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", permanentAppendKey)

            Assert.InRange(permanentAppendAttempts, 1, 3)
            snapshotJournalStorage.ClearFaults("journalhosting.snapshots", permanentAppendKey)
            do! Task.Delay 750
            let! afterPermanentAppend = permanentAppend.notes ()
            Assert.Empty afterPermanentAppend

            let permanentManualSnapshotKey = $"permanent-manual-snapshot-{Guid.NewGuid():N}"

            let permanentManualSnapshot =
                FunctionalGrain.ref snapshotContract cluster.Client permanentManualSnapshotKey

            let! _ = permanentManualSnapshot.append [ "kept" ]

            snapshotJournalStorage.FailAppendPermanently(
                "journalhosting.snapshots",
                permanentManualSnapshotKey
            )

            let! permanentManualSnapshotError =
                expectFailureWithin
                    (TimeSpan.FromSeconds 5.0)
                    (permanentManualSnapshot.snapshot () :> Task)

            Assert.Contains(
                messages permanentManualSnapshotError,
                fun message -> message.Contains "writing a manual snapshot"
            )

            snapshotJournalStorage.ClearFaults("journalhosting.snapshots", permanentManualSnapshotKey)
            do! Task.Delay 750
            let! afterPermanentManualSnapshot = permanentManualSnapshot.notes ()
            Assert.Equal<string list>([ "kept" ], afterPermanentManualSnapshot)

            let permanentClearKey = $"permanent-clear-{Guid.NewGuid():N}"
            let permanentClear = FunctionalGrain.ref snapshotContract cluster.Client permanentClearKey
            let! _ = permanentClear.append [ "kept" ]
            snapshotJournalStorage.FailClearPermanently("journalhosting.snapshots", permanentClearKey)

            let! permanentClearError =
                expectFailureWithin (TimeSpan.FromSeconds 5.0) (permanentClear.clear () :> Task)

            Assert.Contains(messages permanentClearError, fun message -> message.Contains "failed permanently")

            let struct (_, _, permanentClearAttempts) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", permanentClearKey)

            Assert.InRange(permanentClearAttempts, 1, 3)
            snapshotJournalStorage.ClearFaults("journalhosting.snapshots", permanentClearKey)
            do! Task.Delay 750
            let! afterPermanentClear = permanentClear.notes ()
            Assert.Equal<string list>([ "kept" ], afterPermanentClear)

            let transientClearKey = $"transient-clear-{Guid.NewGuid():N}"
            let transientClear = FunctionalGrain.ref snapshotContract cluster.Client transientClearKey
            let! _ = transientClear.append [ "kept" ]

            let struct (_, _, beforeTransientClearAttempts) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", transientClearKey)

            snapshotJournalStorage.FailNextClears("journalhosting.snapshots", transientClearKey, 1)

            let! transientClearError =
                expectFailureWithin (TimeSpan.FromSeconds 5.0) (transientClear.clear () :> Task)

            Assert.Contains(messages transientClearError, fun message -> message.Contains "transient clear failure")

            let struct (_, _, afterTransientClearAttempts) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", transientClearKey)

            // Clear is a direct CustomStorage call in Orleans. An ordinary failure surfaces once,
            // leaves durable state intact, and an explicit retry remains usable.
            Assert.Equal(1, afterTransientClearAttempts - beforeTransientClearAttempts)
            let! afterTransientClearFailure = transientClear.notes ()
            Assert.Equal<string list>([ "kept" ], afterTransientClearFailure)
            do! transientClear.clear ()
            let! afterTransientClearRetry = transientClear.notes ()
            Assert.Empty afterTransientClearRetry

            let malformedKey = $"malformed-{Guid.NewGuid():N}"
            snapshotJournalStorage.ReturnMalformedSnapshot("journalhosting.snapshots", malformedKey)
            let malformed = FunctionalGrain.ref snapshotContract cluster.Client malformedKey
            let! malformedError = expectFailureWithin (TimeSpan.FromSeconds 5.0) (malformed.notes () :> Task)
            Assert.Contains(messages malformedError, fun message -> message.Contains "versions cannot be negative")

            let struct (malformedReads, _, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", malformedKey)

            Assert.InRange(malformedReads, 1, 3)

            let poisonedKey = $"poisoned-{Guid.NewGuid():N}"
            snapshotJournalStorage.SeedTail("journalhosting.snapshots.poisoned", poisonedKey, [ Noted "poison" ])
            let poisoned = FunctionalGrain.ref poisonedSnapshotContract cluster.Client poisonedKey
            let! poisonedError = expectFailureWithin (TimeSpan.FromSeconds 5.0) (poisoned.notes () :> Task)
            Assert.Contains(messages poisonedError, fun message -> message.Contains "apply' fold")

            let struct (poisonedReads, _, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots.poisoned", poisonedKey)

            Assert.InRange(poisonedReads, 1, 3)

            let boundedKey = $"bounded-conflict-{Guid.NewGuid():N}"
            let bounded = FunctionalGrain.ref snapshotContract cluster.Client boundedKey
            let! _ = bounded.append [ "base" ]

            let struct (_, beforeBoundedAttempts, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", boundedKey)

            snapshotJournalStorage.RejectNextAppends("journalhosting.snapshots", boundedKey, 10)
            let! boundedError = expectFailureWithin (TimeSpan.FromSeconds 5.0) (bounded.snapshot () :> Task)

            Assert.Contains(
                messages boundedError,
                fun message -> message.Contains "ManualSnapshotMaxConflictRetries is 3"
            )

            let struct (_, afterBoundedAttempts, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots", boundedKey)

            Assert.Equal(4, afterBoundedAttempts - beforeBoundedAttempts)

            let! synchronizedAfterBoundedConflict = bounded.notes ()

            Assert.Equal<string list>(
                [ "base"; "external-2"; "external-3"; "external-4"; "external-5" ],
                synchronizedAfterBoundedConflict
            )

            snapshotJournalStorage.ClearFaults("journalhosting.snapshots", boundedKey)

            let recomputedKey = $"recomputed-conflict-{Guid.NewGuid():N}"
            let recomputed = FunctionalGrain.ref snapshotContract cluster.Client recomputedKey
            let! _ = recomputed.append [ "base" ]

            snapshotJournalStorage.ConflictOnNextAppend(
                "journalhosting.snapshots",
                recomputedKey,
                Noted "external"
            )

            do! recomputed.snapshot ()

            Assert.Equal(
                Some 2,
                snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots", recomputedKey)
            )

            let! recomputedNotes = recomputed.notes ()
            Assert.Equal<string list>([ "base"; "external" ], recomputedNotes)
        finally
            cluster.StopAllSilos()
            cluster.Dispose()
    }

[<Fact>]
let ``global snapshot When receives typed context and predicate failures do not enter Orleans retries`` () =
    let builder = TestClusterBuilder 1s
    builder.AddSiloBuilderConfigurator<GlobalSnapshotPolicySiloConfigurator>() |> ignore
    builder.AddClientBuilderConfigurator<JournalHostingClientConfigurator>() |> ignore
    let cluster = builder.Build()
    cluster.Deploy()
    cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()

    task {
        try
            let snapshotKey = $"global-when-{Guid.NewGuid():N}"
            let snapshot = FunctionalGrain.ref globalSnapshotContract cluster.Client snapshotKey
            let! count = snapshot.append [ "one"; "global-snapshot" ]
            Assert.Equal(2, count)

            Assert.Equal(
                Some 2,
                snapshotJournalStorage.SnapshotVersion("journalhosting.snapshots.global", snapshotKey)
            )

            let observation =
                globalSnapshotObservations.ToArray()
                |> Array.find (fun candidate -> candidate.Key = snapshotKey && candidate.Version = 2)

            Assert.Equal("journalhosting.snapshots.global", observation.GrainTypeName)
            Assert.Equal(typeof<NoteState>, observation.StateType)
            Assert.Equal<string list>([ "one"; "global-snapshot" ], observation.Notes)

            let failureKey = $"global-failure-{Guid.NewGuid():N}"
            let failure = FunctionalGrain.ref globalSnapshotContract cluster.Client failureKey
            let! _ = failure.append [ "before-error" ]

            let struct (_, beforeFailureAttempts, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots.global", failureKey)

            let! predicateError =
                expectFailureWithin
                    (TimeSpan.FromSeconds 5.0)
                    (failure.append [ "policy-error" ] :> Task)

            Assert.Contains(messages predicateError, fun message -> message.Contains "predicate failed")

            let struct (_, afterFailureAttempts, _) =
                snapshotJournalStorage.Attempts("journalhosting.snapshots.global", failureKey)

            Assert.Equal(beforeFailureAttempts, afterFailureAttempts)
            do! Task.Delay 750
            let! durableNotes = failure.notes ()
            Assert.Equal<string list>([ "before-error" ], durableNotes)
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

[<Fact>]
let ``manual snapshot conflict retry budget cannot be negative`` () =
    let reported = deployExpectingFailure<InvalidSnapshotRetrySiloConfigurator> ()

    Assert.Contains(reported, fun message -> message.Contains "ManualSnapshotMaxConflictRetries")
    Assert.Contains(reported, fun message -> message.Contains "cannot be negative")

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
