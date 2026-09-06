module Orleans.FSharp.Tests.FunctionalPersistentHolderRegressionTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Orleans.Core
open Orleans.Runtime
open Orleans.FSharp
open Orleans.FSharp.Tests.FunctionalTransportHarness
open Xunit

// Mutable values are deliberate here: the holder exposes Orleans' IPersistentState contract,
// including in-place edits before an explicit write. Application examples remain immutable.
type MutableState =
    { mutable Count: int
      Items: int array
      mutable Text: string }

let private initial () = { Count = 1; Items = [| 10; 20 |]; Text = "small" }

type private RecordingStorage<'T>(clone: 'T -> 'T) =
    // A missing record follows the uninitialized Orleans holder boundary.
    let mutable current = Unchecked.defaultof<'T>
    let mutable durable: 'T option = None
    let mutable exists = false
    let mutable etag = ""

    member val Writes = 0 with get, set
    member val Reads = 0 with get, set
    member val Clears = 0 with get, set
    member val Dehydrates = 0 with get, set
    member val MigrationState: 'T option = None with get, set
    member val Failure: Exception option = None with get, set
    member val LastToken = CancellationToken.None with get, set
    member _.Durable = durable |> Option.map clone
    member _.Seed(value: 'T) = durable <- Some(clone value)

    member private this.Before(token: CancellationToken) =
        this.LastToken <- token
        token.ThrowIfCancellationRequested()
        match this.Failure with
        | Some error -> raise error
        | None -> ()

    member private this.Read(token) : Task =
        task {
            this.Before token
            this.Reads <- this.Reads + 1
            current <- durable |> Option.map clone |> Option.defaultValue Unchecked.defaultof<'T>
            exists <- durable.IsSome
        }

    member private this.Write(token) : Task =
        task {
            this.Before token
            this.Writes <- this.Writes + 1
            durable <- Some(clone current)
            exists <- true
            etag <- $"etag-{this.Writes}"
        }

    member private this.Clear(token) : Task =
        task {
            this.Before token
            this.Clears <- this.Clears + 1
            durable <- None
            current <- Unchecked.defaultof<'T> // A clear resets the in-memory holder too.
            exists <- false
            etag <- ""
        }

    interface IPersistentState<'T>
    interface IGrainMigrationParticipant with
        member this.OnDehydrate(_) =
            this.Dehydrates <- this.Dehydrates + 1
            this.MigrationState <- Some(clone current)
        member this.OnRehydrate(_) =
            current <- this.MigrationState |> Option.map clone |> Option.defaultValue Unchecked.defaultof<'T>
    interface IStorage<'T> with
        member _.State with get () = current and set value = current <- value
    interface IStorage with
        member _.Etag = etag
        member _.RecordExists = exists
        member this.ReadStateAsync() = this.Read CancellationToken.None
        member this.WriteStateAsync() = this.Write CancellationToken.None
        member this.ClearStateAsync() = this.Clear CancellationToken.None
        member this.ReadStateAsync(token) = this.Read token
        member this.WriteStateAsync(token) = this.Write token
        member this.ClearStateAsync(token) = this.Clear token

type private Harness =
    { State: IPersistentState<MutableState>
      Stored: unit -> MutableState option
      Seed: MutableState -> unit
      LoadNative: unit -> Task
      Writes: unit -> int
      SetFailure: Exception option -> unit
      LastToken: unit -> CancellationToken }

// Exercise the real blueprint so the direct-binary path cannot accidentally bypass the guard.
let private holder mode limit =
    let services = buildServices true None
    let binary = payloadCodec services :> IFunctionalPayloadCodec
    let codec =
        (if mode = 1 || mode = 3 then FunctionalPersistenceCodec.FSharpJson
         else FunctionalPersistenceCodec.OrleansBinary)
            .WithMaxPayloadBytes limit

    let reference =
        PersistentState.create<MutableState> "holder-regression" "Default"
        |> PersistentState.withCodec codec
        |> fun reference ->
            if mode >= 2 then
                reference |> PersistentState.withSchema (FunctionalSchema.current<MutableState> 7)
            else reference

    let descriptor = FunctionalFacet.blueprint reference (fun _ -> box (initial ()))
    let create (inner: IPersistentState<'T>) =
        let factory =
            { new IPersistentStateFactory with
                member _.Create<'Actual>(_context, _configuration) =
                    Assert.Equal(typeof<'T>, typeof<'Actual>)
                    unbox<IPersistentState<'Actual>>(box inner) }
        descriptor.Create codec binary factory Unchecked.defaultof<IGrainContext>
        |> unbox<IPersistentState<MutableState>>

    let harness =
        if mode = 0 then
            let inner = RecordingStorage<MutableState>(fun value -> binary.Deserialize(binary.Serialize value))
            { State = create (inner :> IPersistentState<MutableState>)
              Stored = fun () -> inner.Durable
              Seed = inner.Seed
              LoadNative = (inner :> IPersistentState<MutableState>).ReadStateAsync
              Writes = fun () -> inner.Writes
              SetFailure = fun error -> inner.Failure <- error
              LastToken = fun () -> inner.LastToken }
        else
            let clone (value: FunctionalPersistenceEnvelope) =
                if isNull value then null
                else
                    FunctionalPersistenceEnvelope(
                        CodecId = value.CodecId,
                        SchemaVersion = value.SchemaVersion,
                        HasValue = value.HasValue,
                        Payload = Array.copy value.Payload)
            let inner = RecordingStorage<FunctionalPersistenceEnvelope>(clone)
            let encode value =
                FunctionalPersistenceEnvelope(
                    CodecId = codec.Id,
                    SchemaVersion = (if mode >= 2 then 7 else 0),
                    HasValue = true,
                    Payload = FunctionalPersistenceEncoding.encode
                        (codec.WithMaxPayloadBytes FunctionalPersistenceCodec.DefaultMaxPayloadBytes) binary value)
            { State = create (inner :> IPersistentState<FunctionalPersistenceEnvelope>)
              Stored = fun () -> inner.Durable |> Option.map (fun value ->
                  FunctionalPersistenceEncoding.decode<MutableState> codec binary value.CodecId value.Payload)
              Seed = encode >> inner.Seed
              LoadNative = (inner :> IPersistentState<FunctionalPersistenceEnvelope>).ReadStateAsync
              Writes = fun () -> inner.Writes
              SetFailure = fun error -> inner.Failure <- error
              LastToken = fun () -> inner.LastToken }
    services, harness

[<Theory; InlineData(0); InlineData(1); InlineData(2); InlineData(3)>]
let ``in-place state and nested-array mutations reach every explicit write`` mode =
    task {
        let services, holder = holder mode 4096
        use services = services
        let state = holder.State
        state.State <- initial ()
        let current = state.State
        Assert.Same(current, state.State)
        current.Count <- 2
        current.Items.[0] <- 99
        do! state.WriteStateAsync()
        let stored = holder.Stored().Value
        Assert.Equal(2, stored.Count)
        Assert.Equal(99, stored.Items.[0])
        Assert.Equal("etag-1", state.Etag)
        Assert.True(state.RecordExists)
        // The successful write must not detach the caller's live state object.
        current.Count <- 3
        use cancellation = new CancellationTokenSource()
        do! state.WriteStateAsync(cancellation.Token)
        Assert.Equal(3, holder.Stored().Value.Count)
        Assert.Equal(cancellation.Token, holder.LastToken())
    }

[<Theory; InlineData(0); InlineData(1); InlineData(2); InlineData(3)>]
let ``read and clear replace cached values while failed writes remain retryable`` mode =
    task {
        let services, holder = holder mode 4096
        use services = services
        let state = holder.State
        state.State <- initial ()
        do! state.WriteStateAsync()
        state.State.Count <- 9
        let error = IOException "provider unavailable"
        holder.SetFailure(Some error)
        let! actual = Assert.ThrowsAsync<IOException>(Func<Task>(fun () -> state.WriteStateAsync()))
        Assert.Same(error, actual)
        Assert.Equal(1, holder.Stored().Value.Count)
        Assert.Equal(9, state.State.Count)
        holder.SetFailure None
        do! state.WriteStateAsync()
        Assert.Equal(9, holder.Stored().Value.Count)
        holder.Seed { initial () with Count = 20 }
        use cancellation = new CancellationTokenSource()
        do! state.ReadStateAsync(cancellation.Token)
        Assert.Equal(cancellation.Token, holder.LastToken())
        Assert.Equal(20, state.State.Count)
        state.State.Count <- 21
        do! state.WriteStateAsync()
        Assert.Equal(21, holder.Stored().Value.Count)
        do! state.ClearStateAsync(cancellation.Token)
        Assert.False(state.RecordExists)
        Assert.Null(box state.State)
        Assert.True(holder.Stored().IsNone)
        state.State <- initial ()
        do! state.WriteStateAsync()
        Assert.Equal(1, holder.Stored().Value.Count)
    }

[<Theory; InlineData(0); InlineData(1); InlineData(2); InlineData(3)>]
let ``cancelled writes do not commit and retain live state for a later retry`` mode =
    task {
        let services, holder = holder mode 4096
        use services = services
        let state = holder.State
        state.State <- initial ()
        do! state.WriteStateAsync()
        state.State.Count <- 8
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(Func<Task>(fun () -> state.WriteStateAsync(cancellation.Token)))
        Assert.Equal(1, holder.Stored().Value.Count)
        Assert.Equal(8, state.State.Count)
        do! state.WriteStateAsync()
        Assert.Equal(8, holder.Stored().Value.Count)
    }

[<Theory; InlineData(0); InlineData(1); InlineData(2); InlineData(3)>]
let ``mutated oversized state is rejected before reaching the storage provider`` mode =
    task {
        let services, holder = holder mode 256
        use services = services
        holder.State.State <- initial ()
        do! holder.State.WriteStateAsync()
        holder.State.State.Text <- String('x', 4096)
        let! error = Assert.ThrowsAsync<InvalidDataException>(Func<Task>(fun () -> holder.State.WriteStateAsync()))
        Assert.Contains("256", error.Message)
        Assert.Equal(1, holder.Writes())
        Assert.Equal("small", holder.Stored().Value.Text)
    }

[<Theory; InlineData(0); InlineData(1); InlineData(2); InlineData(3)>]
let ``oversized stored state is rejected on explicit reload`` mode =
    task {
        let services, holder = holder mode 256
        use services = services
        holder.Seed { initial () with Text = String('x', 4096) }
        let! _ = Assert.ThrowsAsync<InvalidDataException>(Func<Task>(fun () -> holder.State.ReadStateAsync()))
        Assert.Equal(0, holder.Writes())
        Assert.Throws<InvalidDataException>(fun () -> holder.State.State |> ignore) |> ignore
    }

[<Theory; InlineData(0); InlineData(1); InlineData(2); InlineData(3)>]
let ``initial native lifecycle load cannot bypass the payload budget`` mode =
    task {
        let services, holder = holder mode 256
        use services = services
        holder.Seed { initial () with Text = String('x', 4096) }
        // Orleans' SetupState participant loads the inner facet, not our returned facade.
        do! holder.LoadNative()
        Assert.Throws<InvalidDataException>(fun () -> holder.State.State |> ignore) |> ignore
        Assert.Equal(0, holder.Writes())
    }

[<Theory; InlineData(0); InlineData(1); InlineData(2); InlineData(3)>]
let ``failed and cancelled read or clear do not discard a live edited holder`` mode =
    task {
        let services, holder = holder mode 4096
        use services = services
        let state = holder.State
        state.State <- initial ()
        do! state.WriteStateAsync()
        let current = state.State
        current.Count <- 22
        let operations =
            [ (fun () -> state.ReadStateAsync())
              (fun () -> state.ClearStateAsync()) ]
        holder.SetFailure(Some(IOException "provider unavailable"))
        for operation in operations do
            let! _ = Assert.ThrowsAsync<IOException>(Func<Task> operation)
            Assert.Same(current, state.State)
            Assert.Equal(22, state.State.Count)
            Assert.True(state.RecordExists)
        holder.SetFailure None
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        for operation in
            [ (fun () -> state.ReadStateAsync(cancellation.Token))
              (fun () -> state.ClearStateAsync(cancellation.Token)) ] do
            let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(Func<Task> operation)
            Assert.Same(current, state.State)
            Assert.Equal(22, state.State.Count)
        do! state.WriteStateAsync()
        Assert.Equal(22, holder.Stored().Value.Count)
    }

[<Theory; InlineData(0); InlineData(1); InlineData(2); InlineData(3)>]
let ``codec budget accepts the exact byte boundary and rejects one byte below`` mode =
    task {
        use measuring = buildServices true None
        let binary = payloadCodec measuring :> IFunctionalPayloadCodec
        let codec =
            if mode = 1 || mode = 3 then FunctionalPersistenceCodec.FSharpJson
            else FunctionalPersistenceCodec.OrleansBinary
        let size = (FunctionalPersistenceEncoding.encode codec binary (initial ())).Length
        let services, atLimit = holder mode size
        use services = services
        atLimit.State.State <- initial ()
        do! atLimit.State.WriteStateAsync()
        Assert.Equal(1, atLimit.Writes())
        let smallerServices, tooSmall = holder mode (size - 1)
        use smallerServices = smallerServices
        // A direct state is checked at write. An encoded holder also validates its setter.
        if mode = 0 then
            tooSmall.State.State <- initial ()
            let! _ = Assert.ThrowsAsync<InvalidDataException>(Func<Task>(fun () -> tooSmall.State.WriteStateAsync()))
            ()
        else
            Assert.Throws<InvalidDataException>(fun () -> tooSmall.State.State <- initial ()) |> ignore
        Assert.Equal(0, tooSmall.Writes())
    }

[<Theory; InlineData(1); InlineData(2); InlineData(3)>]
let ``migration refreshes live encoded state and refuses oversized transfer before native dehydration`` mode =
    use services = buildServices true None
    let binary = payloadCodec services :> IFunctionalPayloadCodec
    let codec =
        (if mode = 2 then FunctionalPersistenceCodec.OrleansBinary
         else FunctionalPersistenceCodec.FSharpJson).WithMaxPayloadBytes 256
    let schema = if mode >= 2 then Some(FunctionalSchema.current<MutableState> 7) else None
    let clone (value: FunctionalPersistenceEnvelope) =
        FunctionalPersistenceEnvelope(
            CodecId = value.CodecId, SchemaVersion = value.SchemaVersion,
            HasValue = value.HasValue, Payload = Array.copy value.Payload)
    let inner = RecordingStorage<FunctionalPersistenceEnvelope>(clone)
    let encoded = FunctionalEncodedPersistentState<MutableState>(inner, codec, binary, schema)
    let state = encoded :> IPersistentState<MutableState>
    let migration = encoded :> IGrainMigrationParticipant
    state.State <- initial ()
    state.State.Count <- 8
    state.State.Items.[0] <- 88
    migration.OnDehydrate Unchecked.defaultof<IDehydrationContext>
    let transferred = inner.MigrationState.Value
    let decoded = FunctionalPersistenceEncoding.decode<MutableState> codec binary transferred.CodecId transferred.Payload
    Assert.Equal(8, decoded.Count)
    Assert.Equal(88, decoded.Items.[0])
    Assert.Equal(1, inner.Dehydrates)
    Assert.Equal(0, inner.Writes)

    // Rehydration must invalidate a pre-existing decoded cache before the next application read.
    state.State.Count <- 99
    migration.OnRehydrate Unchecked.defaultof<IRehydrationContext>
    Assert.Equal(8, state.State.Count)
    state.State.Text <- String('x', 4096)
    Assert.Throws<InvalidDataException>(fun () -> migration.OnDehydrate Unchecked.defaultof<IDehydrationContext>) |> ignore
    Assert.Equal(1, inner.Dehydrates)
    Assert.Same(transferred, inner.MigrationState.Value)
    Assert.Equal(0, inner.Writes)
