/// TEMPORARY spec-004 Phase C Step-0 seam probe. Deleted once the probes are recorded.
module Orleans.FSharp.Integration.ProbeC1

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Concurrency
open Orleans.Hosting
open Orleans.Metadata
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

type ReentrantActor = private ReentrantActor of unit
type PlainActor = private PlainActor of unit
type SelectiveActor = private SelectiveActor of unit

[<NoEquality; NoComparison>]
type ProbeGateApi =
    { park: int -> Task<string>
      entered: unit -> Task<bool>
      release: unit -> Task<string> }

[<NoEquality; NoComparison>]
type ProbeSelectiveApi =
    { park: int -> Task<string>
      release: unit -> Task<string>
      blocked: unit -> Task<string> }

[<Sealed>]
type GateCell() =
    let mutable entered = 0

    member val Gate =
        TaskCompletionSource<bool> TaskCreationOptions.RunContinuationsAsynchronously with get

    member _.Entered = Volatile.Read(&entered) = 1
    member _.Enter() = Volatile.Write(&entered, 1)
    member _.Leave() = Volatile.Write(&entered, 0)

[<RequireQualifiedAccess>]
module Gates =
    let private cells = ConcurrentDictionary<string, GateCell>()
    /// Keyed by the DOMAIN key, so a test can observe the gate without calling the grain --
    /// which matters for the control: a plain call cannot reach a parked activation at all.
    let cell (key: string) = cells.GetOrAdd(key, fun _ -> GateCell())

[<Literal>]
let ReentrantGrainType = "probe.reentrant"

[<Literal>]
let PlainGrainType = "probe.plain"

let reentrantContract =
    grainContract<ReentrantActor, string, ProbeGateApi> () {
        grainType ReentrantGrainType
        version 1
        stringKey
    }

let plainContract =
    grainContract<PlainActor, string, ProbeGateApi> () {
        grainType PlainGrainType
        version 1
        stringKey
    }

let reentrantDefinition =
    grainFor reentrantContract {
        defaultState (fun () -> 0)

        handle (_.park) (fun context state (timeout: int) ->
            task {
                let cell = Gates.cell context.key
                cell.Enter()

                try
                    let! finished = Task.WhenAny(cell.Gate.Task, Task.Delay timeout)

                    return
                        state,
                        (if obj.ReferenceEquals(finished, cell.Gate.Task) then
                             "released"
                         else
                             "timeout")
                finally
                    cell.Leave()
            })

        handle (_.entered) (fun context state () -> task { return state, (Gates.cell context.key).Entered })

        handle (_.release) (fun context state () ->
            task {
                (Gates.cell context.key).Gate.TrySetResult true |> ignore
                return state, "ok"
            })
    }

let plainDefinition =
    grainFor plainContract {
        defaultState (fun () -> 0)

        handle (_.park) (fun context state (timeout: int) ->
            task {
                let cell = Gates.cell context.key
                cell.Enter()

                try
                    let! finished = Task.WhenAny(cell.Gate.Task, Task.Delay timeout)

                    return
                        state,
                        (if obj.ReferenceEquals(finished, cell.Gate.Task) then
                             "released"
                         else
                             "timeout")
                finally
                    cell.Leave()
            })

        handle (_.entered) (fun context state () -> task { return state, (Gates.cell context.key).Entered })

        handle (_.release) (fun context state () ->
            task {
                (Gates.cell context.key).Gate.TrySetResult true |> ignore
                return state, "ok"
            })
    }

[<Literal>]
let SelectiveGrainType = "probe.mayinterleave"

let selectiveContract =
    grainContract<SelectiveActor, string, ProbeSelectiveApi> () {
        grainType SelectiveGrainType
        version 1
        stringKey
    }

let selectiveDefinition =
    grainFor selectiveContract {
        defaultState (fun () -> 0)

        handle (_.park) (fun context state (timeout: int) ->
            task {
                let cell = Gates.cell context.key
                cell.Enter()

                try
                    let! finished = Task.WhenAny(cell.Gate.Task, Task.Delay timeout)

                    return
                        state,
                        (if obj.ReferenceEquals(finished, cell.Gate.Task) then
                             "released"
                         else
                             "timeout")
                finally
                    cell.Leave()
            })

        handle (_.release) (fun context state () ->
            task {
                (Gates.cell context.key).Gate.TrySetResult true |> ignore
                return state, "ok"
            })

        handle (_.blocked) (fun _ state () -> task { return state, "blocked-ran" })
    }

let reentrantRef = FunctionalGrain.ref reentrantContract
let plainRef = FunctionalGrain.ref plainContract
let selectiveRef = FunctionalGrain.ref selectiveContract

/// PROBE 1 SEAM: an application-level IGrainPropertiesProvider that constructs Orleans' own
/// [Reentrant] attribute and writes its Populate output for the functional grain type.
[<Sealed>]
type ProbeReentrantPropertiesProvider(services: IServiceProvider) =
    interface IGrainPropertiesProvider with
        member _.Populate(grainClass: Type, grainType: GrainType, properties: Dictionary<string, string>) =
            if grainType.ToString() = ReentrantGrainType then
                ReentrantAttribute().Populate(services, grainClass, grainType, properties)

            // PROBE 2 SEAM: Orleans' own [MayInterleave] attribute writes the callback method
            // name. The temporary probe callback lives on FunctionalGrainMarker<'Actor>, which
            // IS the grain class of every functional grain type, so Orleans' own
            // MayInterleaveConfiguratorProvider reflects it off that class.
            if grainType.ToString() = SelectiveGrainType then
                MayInterleaveAttribute("MayInterleave").Populate(services, grainClass, grainType, properties)
            else
                // The temporary probe attribute sits on the SHARED marker, so Orleans'
                // AttributeGrainPropertiesProvider writes the key for every functional grain
                // type. Strip it everywhere except the one grain type under probe.
                properties.Remove "may-interleave-predicate" |> ignore

type ProbeSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain reentrantDefinition |> ignore
            siloBuilder.AddFunctionalGrain plainDefinition |> ignore
            siloBuilder.AddFunctionalGrain selectiveDefinition |> ignore

            siloBuilder.Services.AddSingleton<IGrainPropertiesProvider, ProbeReentrantPropertiesProvider>()
            |> ignore

type ProbeClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

[<Sealed>]
type ProbeFixture() =
    let cluster =
        let builder = TestClusterBuilder 1s
        builder.AddSiloBuilderConfigurator<ProbeSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<ProbeClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Cluster = cluster
    member _.Client = cluster.Client

    /// The published grain properties of one grain type on the primary silo.
    member _.PropertiesOf(grainTypeName: string) =
        let services = (cluster.Silos.[0] :?> InProcessSiloHandle).SiloHost.Services

        let manifest =
            services.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest

        match manifest.Grains.TryGetValue(GrainType.Create grainTypeName) with
        | true, properties -> properties.Properties |> Seq.map (fun p -> p.Key, p.Value) |> Map.ofSeq
        | _ -> Map.empty

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("ProbeC1")>]
type ProbeC1Collection() =
    interface ICollectionFixture<ProbeFixture>

[<Collection("ProbeC1")>]
type ProbeC1Tests(fixture: ProbeFixture) =

    /// Out-of-band: reads the in-process cell rather than calling the grain, so it works for
    /// the non-reentrant control too.
    let waitForGate (key: string) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 10.0

            while not (Gates.cell key).Entered && DateTime.UtcNow < deadline do
                do! Task.Delay 50

            return (Gates.cell key).Entered
        }

    [<Fact>]
    member _.``PROBE 1 the reentrant grain property is published``() =
        let published = fixture.PropertiesOf ReentrantGrainType
        let control = fixture.PropertiesOf PlainGrainType
        Assert.Equal<string>("true", published.["reentrant"])
        Assert.False(control.ContainsKey "reentrant")

    [<Fact>]
    member _.``PROBE 1 two overlapping calls interleave on one reentrant activation``() =
        task {
            let key = $"r-{Guid.NewGuid():N}"
            let api = reentrantRef fixture.Client key
            let parked = api.park 4000
            let! entered = waitForGate key
            Assert.True entered

            let! released = api.release ()
            Assert.Equal<string>("ok", released)

            let! outcome = parked
            Assert.Equal<string>("released", outcome)
        }

    [<Fact>]
    member _.``PROBE 1 CONTROL the same two calls do not interleave without the property``() =
        task {
            let key = $"p-{Guid.NewGuid():N}"
            let api = plainRef fixture.Client key
            let parked = api.park 2500
            let! entered = waitForGate key
            Assert.True entered

            let release = api.release ()
            let! outcome = parked
            Assert.Equal<string>("timeout", outcome)
            let! released = release
            Assert.Equal<string>("ok", released)
        }

    [<Fact>]
    member _.``PROBE 2 the may-interleave-predicate grain property is published``() =
        let published = fixture.PropertiesOf SelectiveGrainType
        let control = fixture.PropertiesOf PlainGrainType
        Assert.Equal<string>("MayInterleave", published.["may-interleave-predicate"])
        Assert.False(control.ContainsKey "may-interleave-predicate")

    [<Fact>]
    member _.``PROBE 2 the predicate sees OUR envelope and interleaves selectively``() =
        task {
            FunctionalInterleaveProbe.seen.Clear()

            FunctionalInterleaveProbe.predicate <-
                Some(fun metadata ->
                    metadata.GrainType = SelectiveGrainType && metadata.OperationId = "release")

            try
                let key = $"s-{Guid.NewGuid():N}"
                let api = selectiveRef fixture.Client key

                // (a) NOT permitted to interleave: 'blocked' must not reach the parked activation.
                let parked = api.park 2500
                let! entered = waitForGate key
                Assert.True entered

                let blocked = api.blocked ()
                let! outcome = parked
                Assert.Equal<string>("timeout", outcome)
                let! blockedReply = blocked
                Assert.Equal<string>("blocked-ran", blockedReply)

                // (b) permitted to interleave: 'release' reaches the parked activation.
                let key2 = $"s-{Guid.NewGuid():N}"
                let api2 = selectiveRef fixture.Client key2
                let parked2 = api2.park 4000
                let! entered2 = waitForGate key2
                Assert.True entered2

                let! released = api2.release ()
                Assert.Equal<string>("ok", released)
                let! outcome2 = parked2
                Assert.Equal<string>("released", outcome2)

                // The callback really saw OUR envelope, not some other invokable shape.
                let observed = FunctionalInterleaveProbe.seen |> Seq.toList
                Assert.Contains($"{SelectiveGrainType}/release=True", observed)
                Assert.Contains($"{SelectiveGrainType}/blocked=False", observed)
                // Orleans evaluates the BLOCKING request as well as the incoming one.
                Assert.Contains($"{SelectiveGrainType}/park=False", observed)
                Assert.DoesNotContain(observed, fun entry -> entry.StartsWith "non-envelope")
            finally
                FunctionalInterleaveProbe.predicate <- None
        }
