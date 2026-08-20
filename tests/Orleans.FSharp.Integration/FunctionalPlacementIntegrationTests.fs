namespace Orleans.FSharp.Integration

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

/// <summary>
/// Spec 004 Phase A, item 4 and 8a: a dedicated, isolated 2-silo cluster (not the large shared
/// <c>FunctionalClusterFixture</c>) proving the first-class placement operations and
/// <c>onLifecycle</c> against a real Orleans runtime rather than definition metadata alone.
/// </summary>
module FunctionalPlacementDomain =

    // ── statelessWorker: reproduces feature-tour §10's measurement through the first-class
    //    operation instead of the composed IGrainPropertiesProvider workaround ──────────────

    type PlacementWorkerActor = private PlacementWorkerActor of unit

    type PlacementWorkReport = { activation: string }

    [<NoEquality; NoComparison>]
    type PlacementWorkerApi =
        { /// Occupies the activation for the requested milliseconds, then reports which one it was.
          work: int -> Task<PlacementWorkReport> }

    [<RequireQualifiedAccess>]
    module PlacementWorkerApi =
        [<Literal>]
        let GrainType = "placement.worker"

        let contract =
            grainContract<PlacementWorkerActor, string, PlacementWorkerApi> {
                grainType GrainType
                version 1
                stringKey
            }

        let ref = FunctionalGrain.ref contract

    [<RequireQualifiedAccess>]
    module PlacementWorkerDefinition =
        let definition =
            grainFor PlacementWorkerApi.contract {
                // One id per activation, exactly like feature-tour's WorkerDefinition: distinct
                // ids across concurrent calls to ONE grain id is the observable signature of
                // stateless-worker placement.
                defaultState (fun () -> Guid.NewGuid().ToString "N")
                statelessWorker 4

                handle
                    (_.work)
                    (fun _context state milliseconds ->
                        task {
                            do! Task.Delay milliseconds
                            return state, { activation = state }
                        })
            }

    // ── placement PreferLocal: the new activation lands on the calling silo ─────────────────

    type PlacementPreferLocalActor = private PlacementPreferLocalActor of unit

    [<NoEquality; NoComparison>]
    type PlacementPreferLocalApi =
        { /// Reports the Orleans silo address this activation actually landed on.
          siloAddress: unit -> Task<string> }

    [<RequireQualifiedAccess>]
    module PlacementPreferLocalApi =
        [<Literal>]
        let GrainType = "placement.preferlocal"

        let contract =
            grainContract<PlacementPreferLocalActor, string, PlacementPreferLocalApi> {
                grainType GrainType
                version 1
                stringKey
            }

        let ref = FunctionalGrain.ref contract

    [<RequireQualifiedAccess>]
    module PlacementPreferLocalDefinition =
        let definition =
            grainFor PlacementPreferLocalApi.contract {
                defaultState (fun () -> ())
                placement PreferLocal

                handle
                    (_.siloAddress)
                    (fun context state () ->
                        task {
                            let details = context.services.GetRequiredService<ILocalSiloDetails>()
                            return state, string details.SiloAddress
                        })
            }

    /// <summary>
    /// Pinned to the non-primary silo only (see <c>PlacementSiloConfigurator</c>), so its own
    /// activation silo is known deterministically without inspecting placement after the fact.
    /// Calls the PreferLocal-placed grain from WITHIN its own handler -- a grain-to-grain call,
    /// which is what <c>PreferLocalPlacement</c>'s own doc means by "the local host": the silo
    /// that received the request causing the new activation, i.e. the caller's silo, not
    /// necessarily whichever gateway an external client happened to connect through.
    /// </summary>
    type PlacementCallerActor = private PlacementCallerActor of unit

    [<NoEquality; NoComparison>]
    type PlacementCallerApi =
        { /// Returns (this caller's own silo, the PreferLocal callee's silo).
          callPreferLocal: string -> Task<string * string> }

    [<RequireQualifiedAccess>]
    module PlacementCallerApi =
        [<Literal>]
        let GrainType = "placement.caller"

        let contract =
            grainContract<PlacementCallerActor, string, PlacementCallerApi> {
                grainType GrainType
                version 1
                stringKey
            }

        let ref = FunctionalGrain.ref contract

    [<RequireQualifiedAccess>]
    module PlacementCallerDefinition =
        let definition =
            grainFor PlacementCallerApi.contract {
                defaultState (fun () -> ())

                handle
                    (_.callPreferLocal)
                    (fun context state calleeKey ->
                        task {
                            let callerDetails = context.services.GetRequiredService<ILocalSiloDetails>()
                            let callerSilo = string callerDetails.SiloAddress
                            let callee = PlacementPreferLocalApi.ref context.grainFactory calleeKey
                            let! calleeSilo = callee.siloAddress ()
                            return state, (callerSilo, calleeSilo)
                        })
            }

    // ── onLifecycle ordering probe (spec 004 item 8a) ───────────────────────────────────────

    /// <summary>Records, per grain key, the order lifecycle-related callbacks actually fired in.
    /// A static probe, matching the established idiom (compare
    /// <c>FunctionalPhase5Fixture.Phase5Probe</c>) for observing activation-internal ordering
    /// that a purely state-based grain API cannot report on its own -- <c>onLifecycle</c> hooks
    /// are deliberately state-free (see <c>LifecycleHook</c>'s remarks).</summary>
    module LifecycleOrderProbe =
        let private order =
            ConcurrentDictionary<string, ConcurrentQueue<string>>(StringComparer.Ordinal)

        let record (grainKey: string) (label: string) =
            let queue = order.GetOrAdd(grainKey, (fun _ -> ConcurrentQueue<string>()))
            queue.Enqueue label

        let orderOf (grainKey: string) : string list =
            match order.TryGetValue grainKey with
            | true, queue -> queue |> List.ofSeq
            | false, _ -> []

    type PlacementLifecycleProbeActor = private PlacementLifecycleProbeActor of unit

    [<NoEquality; NoComparison>]
    type PlacementLifecycleProbeApi = { ping: unit -> Task<unit> }

    [<RequireQualifiedAccess>]
    module PlacementLifecycleProbeApi =
        [<Literal>]
        let GrainType = "placement.lifecycleprobe"

        let contract =
            grainContract<PlacementLifecycleProbeActor, string, PlacementLifecycleProbeApi> {
                grainType GrainType
                version 1
                stringKey
            }

        let ref = FunctionalGrain.ref contract

    [<RequireQualifiedAccess>]
    module PlacementLifecycleProbeDefinition =
        let definition =
            grainFor PlacementLifecycleProbeApi.contract {
                defaultState (fun () -> ())

                onLifecycle First (fun context ->
                    task { LifecycleOrderProbe.record context.key "First" })

                onLifecycle SetupState (fun context ->
                    task { LifecycleOrderProbe.record context.key "SetupState" })

                onActivate (fun context state ->
                    task {
                        LifecycleOrderProbe.record context.key "onActivate"
                        return state
                    })

                onLifecycle Last (fun context -> task { LifecycleOrderProbe.record context.key "Last" })

                handle (_.ping) (fun _ state () -> task { return state, () })
            }

    /// <summary>
    /// Research probe, not part of the public surface: subscribes directly at the RAW
    /// <c>GrainLifecycleStage.Activate</c> (2000) -- which <c>onLifecycle</c> itself refuses to
    /// accept -- for <c>PlacementLifecycleProbeActor</c> specifically, to find out empirically
    /// exactly where that numbered stage falls relative to <c>OnActivateAsync</c> (and therefore
    /// the functional runtime's own <c>onActivate</c>). Uses the same
    /// <c>IConfigureGrainContextProvider</c> / <c>ObservableLifecycle.Subscribe</c> seam
    /// <c>FunctionalPhase5Fixture.Phase5StopStageWitness</c> already proves works for observing a
    /// grain's lifecycle from outside the grain instance.
    /// </summary>
    [<Sealed>]
    type RawActivateStageWitness() =

        interface IConfigureGrainContextProvider with
            member this.TryGetConfigurator
                (
                    grainType: GrainType,
                    _properties: Orleans.Metadata.GrainProperties,
                    configurator: byref<IConfigureGrainContext>
                ) =
                if grainType.ToString() = PlacementLifecycleProbeApi.GrainType then
                    configurator <- this
                    true
                else
                    false

        interface IConfigureGrainContext with
            member _.Configure(context: IGrainContext) =
                context.ObservableLifecycle.Subscribe(
                    "Orleans.FSharp.Integration.RawActivateStageWitness",
                    GrainLifecycleStage.Activate,
                    Func<Threading.CancellationToken, Task>(fun _ ->
                        LifecycleOrderProbe.record (string context.GrainId.Key) "raw-Activate-stage"
                        Task.CompletedTask)
                )
                |> ignore

open FunctionalPlacementDomain

// ──────────────────────────────────────────────────────────────────────────────
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

type PlacementSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            let siloName = siloBuilder.Configuration.["Orleans:Name"]

            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddFunctionalGrain PlacementWorkerDefinition.definition |> ignore
            siloBuilder.AddFunctionalGrain PlacementPreferLocalDefinition.definition |> ignore
            siloBuilder.AddFunctionalGrain PlacementLifecycleProbeDefinition.definition |> ignore
            siloBuilder.Services.AddSingleton<IConfigureGrainContextProvider, RawActivateStageWitness>()
            |> ignore

            // Pinned to the non-primary silo only, so its activation silo is known
            // deterministically (mirrors FunctionalClusterFixture's own OtherActor pattern).
            if siloName <> "Primary" then
                siloBuilder.AddFunctionalGrain PlacementCallerDefinition.definition |> ignore

type PlacementClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

[<Sealed>]
type FunctionalPlacementFixture() =
    let cluster =
        let builder = TestClusterBuilder 2s
        builder.Options.AssumeHomogenousSilosForTesting <- false
        builder.AddSiloBuilderConfigurator<PlacementSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<PlacementClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        // Placement only spreads once every silo is Active in the membership view, and
        // PreferLocal specifically needs every silo's manifest to know about every grain type.
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    let waitForManifestPropagation () =
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        let propagated () =
            cluster.Silos
            |> Seq.forall (fun handle ->
                let services = (handle :?> InProcessSiloHandle).SiloHost.Services

                let current =
                    services.GetRequiredService<IClusterManifestProvider>().Current

                // PlacementPreferLocalApi is hosted unconditionally on every silo (unlike
                // PlacementCallerApi, which is pinned to the non-primary silo only), so its
                // presence on every per-silo manifest entry is a genuine propagation signal.
                current.Silos.Count = cluster.Silos.Count
                && current.Silos
                   |> Seq.forall (fun pair ->
                       pair.Value.Grains.ContainsKey(GrainType.Create PlacementPreferLocalApi.GrainType)))

        while not (propagated ()) && DateTime.UtcNow < deadline do
            Threading.Thread.Sleep 200

        if not (propagated ()) then
            failwith "cluster manifests did not propagate to every silo"

    do waitForManifestPropagation ()

    member _.Client = cluster.Client

    member _.SiloServices(siloName: string) =
        cluster.Silos
        |> Seq.pick (fun handle ->
            if handle.Name = siloName then
                Some (handle :?> InProcessSiloHandle).SiloHost.Services
            else
                None)

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("FunctionalPlacementCluster")>]
type FunctionalPlacementCollection() =
    interface ICollectionFixture<FunctionalPlacementFixture>

// ──────────────────────────────────────────────────────────────────────────────
// Tests
// ──────────────────────────────────────────────────────────────────────────────

[<Collection("FunctionalPlacementCluster")>]
type FunctionalPlacementIntegrationTests(fixture: FunctionalPlacementFixture) =

    /// <remarks>
    /// Reproduces feature-tour §10's measurement (8 concurrent calls -> 4 activations, composed
    /// through an application IGrainPropertiesProvider) through the first-class 'statelessWorker'
    /// operation instead. Asserts "more than one activation" rather than pinning the exact count
    /// -- the multiplexing signature the spec asks for -- and "at most the configured cap" so a
    /// regression that silently drops the cap (falling back to unbounded activation) would also
    /// be caught.
    /// </remarks>
    [<Fact>]
    member _.``statelessWorker multiplexes concurrent calls to one grain id across multiple activations``
        ()
        =
        task {
            let worker = PlacementWorkerApi.ref fixture.Client (Guid.NewGuid().ToString "N")

            let! reports = [| for _ in 1..8 -> worker.work 400 |] |> Task.WhenAll

            let distinctActivations =
                reports |> Array.map (fun report -> report.activation) |> Array.distinct

            Assert.True(
                distinctActivations.Length > 1,
                $"expected multiple activations from 8 concurrent calls under statelessWorker 4, got {distinctActivations.Length}"
            )

            Assert.True(
                distinctActivations.Length <= 4,
                $"statelessWorker 4 must never exceed 4 local activations, got {distinctActivations.Length}"
            )
        }

    [<Fact>]
    member _.``placement PreferLocal places the new activation on the calling silo``() =
        task {
            let caller = PlacementCallerApi.ref fixture.Client (Guid.NewGuid().ToString "N")
            let! callerSilo, calleeSilo = caller.callPreferLocal(Guid.NewGuid().ToString "N")

            Assert.Equal<string>(callerSilo, calleeSilo)
        }

    /// <remarks>
    /// <para>
    /// Spec item 8a: "document ordering relative to the spec-003 activation sequence (facets ->
    /// SetupState -> OnActivateAsync init -> onActivate -> reminders -> timers) ... add the
    /// ordering integration test (probe records firing order)." This is that test.
    /// </para>
    /// <para>
    /// <b>Corrects an assumption made before this test existed.</b> The obvious-looking guess --
    /// "First, SetupState, Activate, Last are just four points along the same activation
    /// timeline, so Last must run after OnActivateAsync since it's the last stage" -- is false,
    /// and this test is what caught it: the raw-Activate-stage marker below (subscribed directly
    /// at <c>GrainLifecycleStage.Activate</c>, bypassing <c>onLifecycle</c>'s own rejection of
    /// that stage, for research) proves the observed order is
    /// <c>First, SetupState, raw-Activate-stage, Last, onActivate</c> -- EVERY numbered
    /// <c>GrainLifecycleStage</c>, including <c>Last</c>, completes before <c>OnActivateAsync</c>
    /// runs at all. <c>OnActivateAsync</c> is not itself gated by any single lifecycle-stage
    /// number; Orleans runs it as a separate step strictly after the whole
    /// <c>ObservableLifecycle</c> "OnStart" stage sequence (First..Last) has completed. So there
    /// is no "post-state" stage among the four at all -- not even <c>Last</c> -- which is why
    /// <c>onLifecycle</c>'s hook shape carries no <c>'State</c> at any stage: the question "should
    /// the post-state stage get a state-carrying shape" does not arise, because none of the four
    /// stages are post-state.
    /// </para>
    /// </remarks>
    [<Fact>]
    member _.``onLifecycle hooks, and the raw Activate stage, all fire before onActivate``() =
        task {
            let key = Guid.NewGuid().ToString "N"
            let probe = PlacementLifecycleProbeApi.ref fixture.Client key
            do! probe.ping ()

            let order = LifecycleOrderProbe.orderOf key

            Assert.Equal<string list>(
                [ "First"; "SetupState"; "raw-Activate-stage"; "Last"; "onActivate" ],
                order
            )
        }
