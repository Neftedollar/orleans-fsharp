module Orleans.FSharp.Rolling.Durable

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Serialization
open Orleans.Storage
open Orleans.FSharp

// Retained historical CLR types: neither field reshaping nor event renaming is implicit.
type StateV1 = { Total: int; Count: int }
type EventV1 = Added of int
#if !OLD_SCHEMA
type StateV2 = { Balance: int64; Bonus: int64; Count: int }
type EventV2 = Credited of int64 | BonusCredited of int64

let upcastState (old: StateV1) : StateV2 =
    { Balance = int64 old.Total; Bonus = 0L; Count = old.Count }
let upcastEvent (Added amount) = Credited(int64 amount)
#endif

#if OLD_SCHEMA
type State = StateV1
type Event = EventV1
let schemaVersion = 1
let initial () : State = { Total = 0; Count = 0 }
let added amount = Added amount
let fold (state: State) (Added amount) = { Total = state.Total + amount; Count = state.Count + 1 }
let describe (state: State) = $"{state.Total}|0|{state.Count}"
let statePipeline = FunctionalSchema.current<StateV1> 1
let eventPipeline = FunctionalSchema.current<EventV1> 1
#else
type State = StateV2
type Event = EventV2
let schemaVersion = 2
let initial () : State = { Balance = 0L; Bonus = 0L; Count = 0 }
let added amount = Credited(int64 amount)
let fold (state: State) event =
    match event with
    | Credited amount -> { state with Balance = state.Balance + amount; Count = state.Count + 1 }
    | BonusCredited amount ->
        { Balance = state.Balance + amount; Bonus = state.Bonus + amount; Count = state.Count + 1 }
let describe (state: State) = $"{state.Balance}|{state.Bonus}|{state.Count}"
let statePipeline = FunctionalSchema.current<StateV1> 1 |> FunctionalSchema.upcaster upcastState
let eventPipeline = FunctionalSchema.current<EventV1> 1 |> FunctionalSchema.upcaster upcastEvent
#endif

type OrdinaryActor = OrdinaryActor of unit
type ViewActor = ViewActor of unit
type LogActor = LogActor of unit
type CustomActor = CustomActor of unit
[<NoEquality; NoComparison>]
type Api =
    { add: int -> Task<unit>
      read: unit -> Task<string>
      snapshot: unit -> Task<unit>
#if NEW_API
      bonus: int -> Task<unit>
#endif
    }

let contract<'Actor> lane =
    grainContract<'Actor, string, Api> {
        grainType $"rolling.durable.{lane}"
#if NEW_API
        version 2
        acceptsVersions (Orleans.FSharp.BackwardCompatible 1)
        sinceVersion 2 (_.bonus)
#else
        version 1
#endif
        stringKey
    }

// Test-only, same-machine store. A persistent lock file is never unlinked: all cooperating
// processes lock the same inode. Atomic rename publishes one complete CAS result. Flush(true)
// flushes file contents, but this is not a power-loss/directory-fsync or network-FS guarantee.
module Files =
    let path root (key: string) =
        Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes key)) + ".json")

    let withLock root key action = task {
        Directory.CreateDirectory root |> ignore
        let file = path root key
        let deadline = DateTime.UtcNow.AddSeconds 10.
        let mutable lease: FileStream option = None
        while lease.IsNone && DateTime.UtcNow < deadline do
            try
                lease <- Some(new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            with :? IOException -> do! Task.Delay 20
        use held = lease |> Option.defaultWith (fun () -> raise (TimeoutException $"store lock timeout: {key}"))
        return action file
    }

    let write file (bytes: byte[]) =
        let temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            use stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            stream.Write bytes
            stream.Flush true
            stream.Close()
            File.Move(temporary, file, true)
        finally
            if File.Exists temporary then File.Delete temporary

type NativeRecord = { ETag: string; Payload: byte[] }

type NativeStore(root: string, serializer: Serializer) =
    let read file =
        if File.Exists file then Some(JsonSerializer.Deserialize<NativeRecord>(File.ReadAllBytes file)) else None
    let check expected stored =
        let actual = stored |> Option.map _.ETag |> Option.defaultValue null
        if actual <> expected then raise (InconsistentStateException "durable rolling CAS mismatch")
    interface IGrainStorage with
        member _.ReadStateAsync<'T>(name, id, state: IGrainState<'T>) =
            Files.withLock root $"{name}/{id}" (fun file ->
                match read file with
                | Some record ->
                    state.State <- serializer.Deserialize<'T>(record.Payload)
                    state.ETag <- record.ETag
                    state.RecordExists <- true
                | None -> state.RecordExists <- false) :> Task
        member _.WriteStateAsync<'T>(name, id, state: IGrainState<'T>) =
            Files.withLock root $"{name}/{id}" (fun file ->
                check state.ETag (read file)
                let record = { ETag = Guid.NewGuid().ToString("N"); Payload = serializer.SerializeToArray state.State }
                Files.write file (JsonSerializer.SerializeToUtf8Bytes record)
                state.ETag <- record.ETag
                state.RecordExists <- true) :> Task
        member _.ClearStateAsync<'T>(name, id, state: IGrainState<'T>) =
            Files.withLock root $"{name}/{id}" (fun file ->
                check state.ETag (read file)
                if File.Exists file then File.Delete file
                state.ETag <- null
                state.RecordExists <- false) :> Task

// This format belongs to CustomStore, NOT the runtime schema pipeline. Each snapshot and
// retained event carries its own format version so mixed old/new tails remain decodable.
type Payload = { Schema: int; Json: string }
type CustomRecord = { Version: int; SnapshotVersion: int; Snapshot: Payload option; Tail: Payload list }
let json = FSharpJson.serializerOptions
let encode value = { Schema = schemaVersion; Json = JsonSerializer.Serialize(value, json) }
let decodeState payload : State =
    match payload.Schema with
    | 1 ->
        let old = JsonSerializer.Deserialize<StateV1>(payload.Json, json)
#if OLD_SCHEMA
        old
#else
        upcastState old
    | 2 -> JsonSerializer.Deserialize<StateV2>(payload.Json, json)
#endif
    | other -> raise (FunctionalJournalPermanentStorageException $"custom store refuses schema {other}; reader {schemaVersion}")
let decodeEvent payload : Event =
    match payload.Schema with
    | 1 ->
        let old = JsonSerializer.Deserialize<EventV1>(payload.Json, json)
#if OLD_SCHEMA
        old
#else
        upcastEvent old
    | 2 -> JsonSerializer.Deserialize<EventV2>(payload.Json, json)
#endif
    | other -> raise (FunctionalJournalPermanentStorageException $"custom store refuses schema {other}; reader {schemaVersion}")

type CustomStore(root: string) =
    let read file =
        if File.Exists file then JsonSerializer.Deserialize<CustomRecord>(File.ReadAllBytes file, json)
        else { Version = 0; SnapshotVersion = 0; Snapshot = None; Tail = [] }
    interface IFunctionalJournalStorage<string, State, Event> with
        member _.Read identity =
            Files.withLock root $"custom/{identity.GrainId}" (fun file ->
                let record = read file
                if record.Version <> record.SnapshotVersion + record.Tail.Length then
                    raise (FunctionalJournalPermanentStorageException "custom store version/tail invariant")
                { Snapshot = record.Snapshot |> Option.map (fun payload -> { Version = record.SnapshotVersion; State = decodeState payload })
                  Events = record.Tail |> List.map decodeEvent |> List.toArray :> IReadOnlyList<Event> })
        member _.Append(identity, write) =
            Files.withLock root $"custom/{identity.GrainId}" (fun file ->
                let old = read file
                if old.Version <> write.ExpectedVersion then false
                else
                    let version = write.ExpectedVersion + write.Events.Count
                    let next =
                        match write.Snapshot with
                        | Some snapshot ->
                            if snapshot.Version <> version then invalidOp "snapshot version mismatch"
                            { Version = version; SnapshotVersion = version; Snapshot = Some(encode snapshot.State); Tail = [] }
                        | None -> { old with Version = version; Tail = old.Tail @ (write.Events |> Seq.map encode |> Seq.toList) }
                    Files.write file (JsonSerializer.SerializeToUtf8Bytes(next, json))
                    true)
        member _.Clear identity =
            Files.withLock root $"custom/{identity.GrainId}" (fun file -> if File.Exists file then File.Delete file) :> Task

let ordinary =
    let stateRef = PersistentState.create<State> "ordinary" "Durable" |> PersistentState.withSchema statePipeline
    grainFor (contract<OrdinaryActor> "ordinary") {
        defaultState initial
        stateFrom stateRef
        handle (_.add) (fun context state amount -> task {
            let next = fold state (added amount)
            let facet = context.persistentState stateRef
            facet.State <- next
            do! facet.WriteStateAsync()
            return next, () })
        handle (_.read) (fun _ state () -> task { return state, describe state })
        handle (_.snapshot) (fun _ state () -> task { return state, () })
#if NEW_API
        handle (_.bonus) (fun context state amount -> task {
            let next = fold state (BonusCredited(int64 amount * 2L))
            let facet = context.persistentState stateRef
            facet.State <- next
            do! facet.WriteStateAsync()
            return next, () })
#endif
    }

let journal (definitionContract: GrainContract<'Actor, string, Api>) provider =
    journaledGrainFor definitionContract {
        initialEventState (fun (_: string) -> initial ())
        apply fold
        logProvider provider
        journalStorage "Durable"
        stateSchema statePipeline
        eventSchema eventPipeline
        handle (_.add) (fun _ _ amount -> task { return [ added amount ], () })
        handle (_.read) (fun context state () -> task {
            if context.journalVersion <> state.Count then
                invalidOp $"native journal version {context.journalVersion} differs from folded count {state.Count}"
            return [], describe state })
        handle (_.snapshot) (fun _ _ () -> task { return [], () })
#if NEW_API
        handle (_.bonus) (fun _ _ amount -> task { return [ BonusCredited(int64 amount * 2L) ], () })
#endif
    }

let custom root =
    journaledGrainFor (contract<CustomActor> "custom") {
        initialEventState (fun (_: string) -> initial ())
        apply fold
        logProvider "Custom"
        customStorage (fun _ -> CustomStore(root) :> IFunctionalJournalStorage<string, State, Event>)
        snapshotPolicy FunctionalJournalSnapshotPolicy.Disabled
        handle (_.add) (fun _ _ amount -> task { return [ added amount ], () })
        handle (_.read) (fun context state () -> task {
            if context.journalVersion <> state.Count then
                invalidOp $"custom journal version {context.journalVersion} differs from folded count {state.Count}"
            return [], describe state })
        handle (_.snapshot) (fun context _ () -> task { context.snapshotNow(); return [], () })
#if NEW_API
        handle (_.bonus) (fun _ _ amount -> task { return [ BonusCredited(int64 amount * 2L) ], () })
#endif
    }

let configure root (silo: ISiloBuilder) =
    silo.Services.AddKeyedSingleton<IGrainStorage>("Durable", Func<IServiceProvider, obj, IGrainStorage>(fun sp _ -> NativeStore(root, sp.GetRequiredService<Serializer>()))) |> ignore
    silo.AddStateStorageBasedLogConsistencyProvider "View" |> ignore
    silo.AddLogStorageBasedLogConsistencyProvider "Log" |> ignore
    silo.AddCustomStorageBasedLogConsistencyProvider "Custom" |> ignore
    silo.ConfigureFunctionalPersistence(fun options ->
        options.DefaultStateCodec <- FunctionalPersistenceCodec.FSharpJson
        options.DefaultJournalCodec <- FunctionalPersistenceCodec.FSharpJson) |> ignore
    silo.AddFunctionalGrain ordinary |> ignore
    silo.AddFunctionalJournaledGrain(journal (contract<ViewActor> "view") "View") |> ignore
    silo.AddFunctionalJournaledGrain(journal (contract<LogActor> "log") "Log") |> ignore
    silo.AddFunctionalJournaledGrain(custom root) |> ignore

let lanes = [ "ordinary"; "view"; "log"; "custom" ]
let command (factory: IGrainFactory) (line: string) = task {
    let parts = line.Split(' ')
    let operation, key = parts[0], parts[1]
    let results = ResizeArray<string>()
    for lane in lanes do
        let api =
            match lane with
            | "ordinary" -> FunctionalGrain.ref (contract<OrdinaryActor> lane) factory key
            | "view" -> FunctionalGrain.ref (contract<ViewActor> lane) factory key
            | "log" -> FunctionalGrain.ref (contract<LogActor> lane) factory key
            | "custom" -> FunctionalGrain.ref (contract<CustomActor> lane) factory key
            | other -> invalidOp $"unknown lane {other}"
        match operation with
        | "seed" ->
            do! api.add 4
            if lane = "custom" then do! api.snapshot ()
            do! api.add 7
        | "add" -> do! api.add 2
        | "snapshot" -> do! api.snapshot ()
#if NEW_API
        | "bonus" -> do! api.bonus 3
#endif
        | "read" | "reject" -> ()
        | other -> invalidOp $"unknown durable command {other}"
        if operation = "reject" then
            try
                let! value = (api.read ()).WaitAsync(TimeSpan.FromSeconds 15.)
                invalidOp $"original reader unexpectedly accepted {lane}: {value}"
            with error ->
                let message = error.ToString()
                let expected =
                    if lane = "custom" then "custom store refuses schema 2; reader 1"
                    else "schema version 2 is newer than the configured current version 1"
                if not (message.Contains(expected, StringComparison.Ordinal)) then raise error
                results.Add $"{lane}=REFUSED"
        else
            let! value = (api.read ()).WaitAsync(TimeSpan.FromSeconds 15.)
            results.Add $"{lane}={value}"
    return String.concat ";" results
}
