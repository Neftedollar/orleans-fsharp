module Orleans.FSharp.Integration.FunctionalPersistentHolderIntegrationTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans.Hosting
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

type HolderActor = private HolderActor of unit

type MutableState =
    { mutable Count: int
      Items: int array
      mutable Text: string }

type Change = { Mode: int; Value: int }
type Stored = { Count: int; First: int; Text: string }

[<NoEquality; NoComparison>]
type HolderApi =
    { mutate: Change -> Task<unit>
      peek: int -> Task<Stored>
      grow: int -> Task<unit>
      editMemory: Change -> Task<unit>
      migrate: SiloAddress -> Task<unit>
      location: unit -> Task<string>
      generation: unit -> Task<Guid>
      goAway: unit -> Task<unit> }

let private initial () = { Count = 0; Items = [| 0; 0 |]; Text = "small" }

let private states =
    Array.init 4 (fun mode ->
        let codec =
            if mode = 1 || mode = 3 then FunctionalPersistenceCodec.FSharpJson
            else FunctionalPersistenceCodec.OrleansBinary
        PersistentState.create<MutableState> $"live-holder-{mode}" "HolderStore"
        |> PersistentState.withCodec (codec.WithMaxPayloadBytes 512)
        |> fun reference ->
            if mode >= 2 then
                reference |> PersistentState.withSchema (FunctionalSchema.current<MutableState> 7)
            else reference)

let private contract =
    grainContract<HolderActor, string, HolderApi> {
        grainType "integration.mutable-persistent-holder"
        stringKey
    }

let private definition =
    grainFor contract {
        initialState (fun _ -> Guid.NewGuid())
        usePersistentState states.[0] (fun _ -> initial ())
        usePersistentState states.[1] (fun _ -> initial ())
        usePersistentState states.[2] (fun _ -> initial ())
        usePersistentState states.[3] (fun _ -> initial ())
        handle (_.mutate) (fun context generation change ->
            task {
                let storage = context.persistentState states.[change.Mode]
                let live = storage.State
                live.Count <- change.Value
                live.Items.[0] <- change.Value * 10
                do! storage.WriteStateAsync()
                // Reusing the live object after a successful native provider write must work.
                live.Count <- change.Value + 1
                do! storage.WriteStateAsync()
                return generation, ()
            })
        handle (_.peek) (fun context generation mode ->
            task {
                let value = (context.persistentState states.[mode]).State
                return generation, { Count = value.Count; First = value.Items.[0]; Text = value.Text }
            })
        handle (_.grow) (fun context generation mode ->
            task {
                let storage = context.persistentState states.[mode]
                storage.State.Text <- String('x', 4096)
                do! storage.WriteStateAsync()
                return generation, ()
            })
        handle (_.editMemory) (fun context generation change ->
            let live = (context.persistentState states.[change.Mode]).State
            live.Count <- change.Value
            live.Items.[0] <- change.Value * 10
            Task.FromResult(generation, ()))
        handle (_.migrate) (fun context generation target ->
            context.migrateOnIdle target
            Task.FromResult(generation, ()))
        handle (_.location) (fun context generation () ->
            let details = context.services.GetRequiredService<ILocalSiloDetails>()
            Task.FromResult(generation, details.Name))
        handle (_.generation) (fun _ generation () -> Task.FromResult(generation, generation))
        handle (_.goAway) (fun context generation () ->
            context.deactivateOnIdle ()
            Task.FromResult(generation, ()))
    }

type private SiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(silo: ISiloBuilder) =
            silo.AddMemoryGrainStorage "HolderStore" |> ignore
            silo.AddFunctionalGrain definition |> ignore

type private ClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_: IConfiguration, client: IClientBuilder) =
            client.AddFunctionalGrainClient() |> ignore

let private reactivate (api: HolderApi) =
    task {
        let! before = api.generation ()
        do! api.goAway ()
        let deadline = DateTime.UtcNow.AddSeconds 30.0
        let mutable after = before
        while after = before && DateTime.UtcNow < deadline do
            do! Task.Delay 25
            let! next = api.generation ()
            after <- next
        Assert.NotEqual(before, after)
    }

[<Fact>]
let ``live persistent holders retain mutable writes and reject oversized updates across reactivation`` () =
    task {
        let builder = TestClusterBuilder 1s
        builder.AddSiloBuilderConfigurator<SiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<ClientConfigurator>() |> ignore
        use cluster = builder.Build()
        do! cluster.DeployAsync()
        try
            let api = FunctionalGrain.ref contract cluster.Client $"holder-{Guid.NewGuid():N}"
            for mode in 0..3 do
                do! api.mutate { Mode = mode; Value = 10 + mode }
            do! reactivate api
            for mode in 0..3 do
                let! actual = api.peek mode
                Assert.Equal(11 + mode, actual.Count)
                Assert.Equal((10 + mode) * 10, actual.First)
                Assert.Equal("small", actual.Text)
                let! error = Assert.ThrowsAnyAsync<Exception>(Func<Task>(fun () -> api.grow mode))
                Assert.Contains("512", error.ToString())
            do! reactivate api
            for mode in 0..3 do
                let! afterRejectedWrite = api.peek mode
                Assert.Equal(11 + mode, afterRejectedWrite.Count)
                Assert.Equal("small", afterRejectedWrite.Text)
        finally
            cluster.StopAllSilos()
    }

[<Fact>]
let ``activation migration transfers edited persistent holders without committing them`` () =
    task {
        let builder = TestClusterBuilder 2s
        builder.AddSiloBuilderConfigurator<SiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<ClientConfigurator>() |> ignore
        use cluster = builder.Build()
        do! cluster.DeployAsync()
        try
            let api = FunctionalGrain.ref contract cluster.Client $"migrate-holder-{Guid.NewGuid():N}"
            for mode in 0..3 do
                do! api.mutate { Mode = mode; Value = 10 + mode }
                do! api.editMemory { Mode = mode; Value = 100 + mode }
            let! oldLocation = api.location ()
            let target = cluster.Silos |> Seq.find (fun silo -> silo.Name <> oldLocation)
            do! api.migrate target.SiloAddress
            let deadline = DateTime.UtcNow.AddSeconds 30.
            let mutable location = oldLocation
            while location <> target.Name && DateTime.UtcNow < deadline do
                do! Task.Delay 25
                let! current = api.location ()
                location <- current
            Assert.Equal(target.Name, location)
            for mode in 0..3 do
                let! migrated = api.peek mode
                Assert.Equal(100 + mode, migrated.Count)
                Assert.Equal((100 + mode) * 10, migrated.First)
            // Migration transfers memory, not durable writes.
            do! reactivate api
            for mode in 0..3 do
                let! reloaded = api.peek mode
                Assert.Equal(11 + mode, reloaded.Count)
                Assert.Equal((10 + mode) * 10, reloaded.First)
        finally
            cluster.StopAllSilos()
    }
