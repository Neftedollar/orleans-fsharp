/// <summary>
/// The implicit-subscription fixtures for spec 004 item 1: one single-silo cluster and one
/// two-silo cluster, both hosting functional definitions that declare <c>onStream</c> and
/// <c>onBroadcast</c> hooks against real Orleans memory streams and a real broadcast-channel
/// provider.
/// </summary>
module Orleans.FSharp.Integration.FunctionalStreamFixture

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.BroadcastChannel
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Streams
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Providers and namespaces
// ──────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module StreamNames =
    /// <summary>The declared stream provider.</summary>
    [<Literal>]
    let Provider = "ImplicitStreams"

    /// <summary>A second stream provider, deliberately NOT named by any declaration.</summary>
    [<Literal>]
    let OtherProvider = "OtherStreams"

    /// <summary>The declared broadcast-channel provider.</summary>
    [<Literal>]
    let ChannelProvider = "ImplicitChannels"

    /// <summary>The declared string-item stream namespace.</summary>
    [<Literal>]
    let Items = "implicit.items"

    /// <summary>The declared int-item stream namespace, proving a second item type.</summary>
    [<Literal>]
    let Numbers = "implicit.numbers"

    /// <summary>A namespace no definition declares.</summary>
    [<Literal>]
    let Unsubscribed = "implicit.unsubscribed"

    /// <summary>The declared broadcast-channel namespace.</summary>
    [<Literal>]
    let Channel = "implicit.channel"

    /// <summary>The namespace whose hook throws until its attempt budget is spent.</summary>
    [<Literal>]
    let Poison = "implicit.poison"

[<RequireQualifiedAccess>]
module StreamGrainTypes =
    [<Literal>]
    let Sink = "functional.streamsink"

    [<Literal>]
    let Poison = "functional.streampoison"

    [<Literal>]
    let PrimarySiloName = "Primary"

// ──────────────────────────────────────────────────────────────────────────────
// Cross-activation observation
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// How many deliveries of each item the poison hook must refuse before it starts accepting.
/// Per item rather than global, so tests which arm it never interfere with one another however
/// xunit schedules them.
/// </summary>
[<Sealed>]
type FailureBudget() =
    let remaining = ConcurrentDictionary<string, int ref>()

    /// <summary>Refuse the next <paramref name="count"/> deliveries of one item.</summary>
    member _.Arm(item: string, count: int) =
        remaining.[item] <- ref count

    /// <summary>Consume one unit of that item's budget; true while the hook must still throw.</summary>
    member _.ShouldFail(item: string) =
        match remaining.TryGetValue item with
        | true, cell -> Interlocked.Decrement cell >= 0
        | _ -> false

/// <summary>
/// Silos of a <c>TestCluster</c> share one process, so a delivery can record what it observed
/// for a test to read back without calling the grain (which would itself activate it).
/// </summary>
[<RequireQualifiedAccess>]
module StreamProbe =
    /// <summary>Every delivery, keyed by "namespace|grainKey", in arrival order.</summary>
    let deliveries = ConcurrentQueue<string>()

    /// <summary>How many times the poison hook has been entered, per item.</summary>
    let attempts = ConcurrentDictionary<string, int>()

    /// <summary>How many further deliveries the poison hook must refuse.</summary>
    let poison = FailureBudget()

    /// <summary>The silo each delivery ran on, keyed by "namespace|grainKey".</summary>
    let silos = ConcurrentDictionary<string, string>()

    let reset () =
        deliveries.Clear()
        attempts.Clear()
        silos.Clear()

    let record (key: string) = deliveries.Enqueue key

    let count (key: string) =
        deliveries |> Seq.filter (fun entry -> entry = key) |> Seq.length

// ──────────────────────────────────────────────────────────────────────────────
// Contracts and definitions
// ──────────────────────────────────────────────────────────────────────────────

type SinkActor = private SinkActor of unit
type PoisonActor = private PoisonActor of unit

type SinkState =
    { streamItems: string list
      streamNumbers: int list
      channelDeliveries: string list }

[<NoEquality; NoComparison>]
type SinkApi =
    { items: unit -> Task<string list>
      numbers: unit -> Task<int list>
      channelItems: unit -> Task<string list>
      touch: unit -> Task<string>
      /// Ends this activation after the current turn, so a later delivery has to re-activate.
      goIdle: unit -> Task<unit> }

/// <summary>
/// A stateless-worker definition WITHOUT any implicit subscription, hosted on the same
/// streaming-enabled silo. It is the regression guard for the one hazard this feature could
/// have introduced cluster-wide: Orleans binds a StreamConsumerExtension to every activation
/// whose instance implements IStreamSubscriptionObserver, and BindExtension throws for a
/// stateless worker — so if the functional target implemented that interface unconditionally,
/// every stateless-worker functional grain on a streaming silo would fail to activate.
/// </summary>
type WorkerActor = private WorkerActor of unit

[<NoEquality; NoComparison>]
type WorkerApi = { work: unit -> Task<string> }

type PoisonState = { acceptedItems: string list }

[<NoEquality; NoComparison>]
type PoisonApi = { accepted: unit -> Task<string list> }

let sinkContract =
    grainContract<SinkActor, string, SinkApi> () {
        grainType StreamGrainTypes.Sink
        version 1
        stringKey

        readOnly (_.items)
        readOnly (_.numbers)
        readOnly (_.channelItems)
    }

let poisonContract =
    grainContract<PoisonActor, string, PoisonApi> () {
        grainType StreamGrainTypes.Poison
        version 1
        stringKey

        readOnly (_.accepted)
    }

/// <summary>The name of the silo an activation is running on, read from its own services.</summary>
let private siloOf (services: IServiceProvider) =
    services.GetRequiredService<ILocalSiloDetails>().Name

let sinkDefinition =
    grainFor sinkContract {
        defaultState (fun () ->
            { streamItems = []
              streamNumbers = []
              channelDeliveries = [] })

        onStream StreamNames.Provider StreamNames.Items (fun context state (item: string) ->
            task {
                let key = $"{StreamNames.Items}|{context.key}"
                StreamProbe.record key
                StreamProbe.silos.[key] <- siloOf context.services
                return
                    { state with
                        streamItems = state.streamItems @ [ item ] }
            })

        onStream StreamNames.Provider StreamNames.Numbers (fun context state (item: int) ->
            task {
                StreamProbe.record $"{StreamNames.Numbers}|{context.key}"
                return
                    { state with
                        streamNumbers = state.streamNumbers @ [ item ] }
            })

        onBroadcast StreamNames.ChannelProvider StreamNames.Channel (fun context state (item: string) ->
            task {
                let key = $"{StreamNames.Channel}|{context.key}"
                StreamProbe.record key
                StreamProbe.silos.[key] <- siloOf context.services

                return
                    { state with
                        channelDeliveries = state.channelDeliveries @ [ item ] }
            })

        handle (_.items) (fun _ state () -> task { return state, state.streamItems })
        handle (_.numbers) (fun _ state () -> task { return state, state.streamNumbers })
        handle (_.channelItems) (fun _ state () -> task { return state, state.channelDeliveries })
        handle (_.touch) (fun context state () -> task { return state, siloOf context.services })

        handle (_.goIdle) (fun context state () ->
            task {
                context.deactivateOnIdle ()
                return state, ()
            })
    }

let workerContract =
    grainContract<WorkerActor, string, WorkerApi> () {
        grainType "functional.streamworker"
        version 1
        stringKey
    }

let workerDefinition =
    grainFor workerContract {
        defaultState (fun () -> Guid.NewGuid().ToString "N")
        statelessWorker 2
        handle (_.work) (fun _ state () -> task { return state, state })
    }

/// <summary>
/// A definition whose delivery hook throws until <c>StreamProbe.poison</c>'s budget is spent,
/// so a test can observe what stock Orleans does with a failing implicit delivery.
/// </summary>
let poisonDefinition =
    grainFor poisonContract {
        defaultState (fun () -> { acceptedItems = [] })

        onStream StreamNames.Provider StreamNames.Poison (fun context state (item: string) ->
            task {
                StreamProbe.attempts.AddOrUpdate(item, 1, fun _ previous -> previous + 1) |> ignore

                if StreamProbe.poison.ShouldFail item then
                    raise (ApplicationException $"poison hook refused '{item}' on {context.key}")

                StreamProbe.record $"{StreamNames.Poison}|{context.key}"

                return
                    { state with
                        acceptedItems = state.acceptedItems @ [ item ] }
            })

        handle (_.accepted) (fun _ state () -> task { return state, state.acceptedItems })
    }

let sinkRef = FunctionalGrain.ref sinkContract
let poisonRef = FunctionalGrain.ref poisonContract
let workerRef = FunctionalGrain.ref workerContract

// ──────────────────────────────────────────────────────────────────────────────
// Cluster configuration
// ──────────────────────────────────────────────────────────────────────────────

type FunctionalStreamSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage "PubSubStore" |> ignore
            siloBuilder.AddMemoryStreams StreamNames.Provider |> ignore
            siloBuilder.AddMemoryStreams StreamNames.OtherProvider |> ignore
            siloBuilder.AddBroadcastChannel StreamNames.ChannelProvider |> ignore
            siloBuilder.AddFunctionalGrain sinkDefinition |> ignore
            siloBuilder.AddFunctionalGrain poisonDefinition |> ignore
            siloBuilder.AddFunctionalGrain workerDefinition |> ignore

type FunctionalStreamClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore
            clientBuilder.AddMemoryStreams StreamNames.Provider |> ignore
            clientBuilder.AddMemoryStreams StreamNames.OtherProvider |> ignore

/// <summary>Deploy a cluster of <paramref name="siloCount"/> silos with the fixture's shape.</summary>
let private deploy (siloCount: int16) =
    let builder = TestClusterBuilder siloCount
    builder.AddSiloBuilderConfigurator<FunctionalStreamSiloConfigurator>() |> ignore

    builder.AddClientBuilderConfigurator<FunctionalStreamClientConfigurator>()
    |> ignore

    let cluster = builder.Build()
    cluster.Deploy()
    cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
    cluster

/// <summary>Shared behaviour of both cluster shapes.</summary>
[<AbstractClass>]
type FunctionalStreamFixtureBase(siloCount: int16) =
    let cluster = deploy siloCount

    member _.Cluster = cluster
    member _.Client = cluster.Client
    member _.SiloCount = int siloCount

    /// <summary>The named stream provider of the external client.</summary>
    member _.StreamProvider(providerName: string) = cluster.Client.GetStreamProvider providerName

    /// <summary>The named broadcast-channel provider of the primary silo.</summary>
    member _.ChannelProvider() =
        cluster
            .GetSiloServiceProvider(cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IBroadcastChannelProvider> StreamNames.ChannelProvider

    /// <summary>Publish one item to the named provider, namespace, and key.</summary>
    member this.Publish<'Item>(providerName: string, streamNamespace: string, key: string, item: 'Item) =
        task {
            let stream =
                this
                    .StreamProvider(providerName)
                    .GetStream<'Item>(StreamId.Create(streamNamespace, key))

            do! stream.OnNextAsync item
        }

    /// <summary>Publish one item to the declared broadcast channel.</summary>
    member this.PublishChannel<'Item>(channelNamespace: string, key: string, item: 'Item) =
        task {
            let writer =
                this.ChannelProvider().GetChannelWriter<'Item>(ChannelId.Create(channelNamespace, key))

            do! writer.Publish item
        }

    /// <summary>Wait until <paramref name="predicate"/> holds, or fail after the deadline.</summary>
    member _.WaitFor(description: string, timeout: TimeSpan, predicate: unit -> bool) =
        task {
            let deadline = DateTime.UtcNow.Add timeout

            while not (predicate ()) && DateTime.UtcNow < deadline do
                do! Task.Delay 50

            if not (predicate ()) then
                failwith $"timed out after {timeout} waiting for {description}"
        }

    /// <summary>
    /// Block until every silo's cluster manifest carries every silo's grain manifest. Implicit
    /// subscriber resolution reads the CLUSTER manifest, so a silo that has not yet gossiped its
    /// neighbour's grain manifest cannot resolve a subscriber hosted only there.
    /// </summary>
    member _.WaitForManifestPropagation() =
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        let propagated () =
            cluster.Silos
            |> Seq.forall (fun handle ->
                let services = (handle :?> InProcessSiloHandle).SiloHost.Services
                let current = services.GetRequiredService<IClusterManifestProvider>().Current

                current.Silos.Count = cluster.Silos.Count
                && current.Silos
                   |> Seq.forall (fun pair -> pair.Value.Grains.ContainsKey(GrainType.Create StreamGrainTypes.Sink)))

        while not (propagated ()) && DateTime.UtcNow < deadline do
            Thread.Sleep 200

        if not (propagated ()) then
            failwith "cluster manifests did not propagate to every silo"

    /// <summary>Every silo's own view of the whole cluster manifest.</summary>
    member _.ClusterManifests =
        cluster.Silos
        |> Seq.map (fun handle ->
            handle.Name,
            (handle :?> InProcessSiloHandle)
                .SiloHost.Services.GetRequiredService<IClusterManifestProvider>()
                .Current)
        |> Seq.toList

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

/// <summary>One silo: the simplest shape in which implicit delivery must work.</summary>
[<Sealed>]
type FunctionalStreamSingleSiloFixture() =
    inherit FunctionalStreamFixtureBase(1s)

/// <summary>
/// Two silos: the pulling agents are spread across both, and Orleans places the implicitly
/// activated grain wherever it likes, so delivery has to cross a silo boundary to work.
/// </summary>
[<Sealed>]
type FunctionalStreamClusterFixture() as this =
    inherit FunctionalStreamFixtureBase(2s)

    do this.WaitForManifestPropagation()

[<CollectionDefinition("FunctionalStreamSingleSilo")>]
type FunctionalStreamSingleSiloCollection() =
    interface ICollectionFixture<FunctionalStreamSingleSiloFixture>

[<CollectionDefinition("FunctionalStreamCluster")>]
type FunctionalStreamClusterCollection() =
    interface ICollectionFixture<FunctionalStreamClusterFixture>
