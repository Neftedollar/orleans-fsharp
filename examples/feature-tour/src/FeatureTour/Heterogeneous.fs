/// <summary>
/// Status-matrix row 10 (tour section 11) — heterogeneous cluster: a two-silo cluster where one
/// functional grain type is advertised by only one silo, driven from an external client so
/// placement and routing are real rather than assumed.
/// </summary>
namespace FeatureTour.Heterogeneous

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Hosting
open Orleans.Metadata
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp

/// <summary>Names shared by the cluster configurators and the driver.</summary>
[<RequireQualifiedAccess>]
module Cluster =
    /// <summary>The silo name <c>TestCluster</c> gives its first silo.</summary>
    [<Literal>]
    let PrimarySiloName = "Primary"

    /// <summary>Hosted by EVERY silo.</summary>
    [<Literal>]
    let EverywhereGrainType = "tour.everywhere"

    /// <summary>Hosted ONLY by silos that are not the primary.</summary>
    [<Literal>]
    let RegionalGrainType = "tour.regional"

type EverywhereActor = private EverywhereActor of unit
type RegionalActor = private RegionalActor of unit

[<NoEquality; NoComparison>]
type WhereApi =
    { /// Replies with the name of the silo whose activation ran the handler.
      whichSilo: unit -> Task<string> }

[<RequireQualifiedAccess>]
module WhereApi =

    /// <summary>
    /// One API record, two brands, two grain types — the same shape the runtime's own
    /// integration fixture uses for its heterogeneous arm.
    /// </summary>
    let everywhere =
        grainContract<EverywhereActor, string, WhereApi> {
            grainType Cluster.EverywhereGrainType
            version 1
            stringKey
        }

    let regional =
        grainContract<RegionalActor, string, WhereApi> {
            grainType Cluster.RegionalGrainType
            version 1
            stringKey
        }

    let everywhereRef = FunctionalGrain.ref everywhere
    let regionalRef = FunctionalGrain.ref regional

[<RequireQualifiedAccess>]
module WhereDefinition =

    /// The activation reports its own silo: ILocalSiloDetails is an ordinary silo service, so a
    /// functional handler reaches it through context.services like anything else.
    let private handler _actorName =
        fun (context: FunctionalGrainContext<'Actor, string>) state () ->
            task {
                let details = context.services.GetRequiredService<ILocalSiloDetails>()
                return state, details.Name
            }

    let everywhere =
        grainFor WhereApi.everywhere {
            defaultState (fun () -> ())
            handle (_.whichSilo) (handler "everywhere")
        }

    let regional =
        grainFor WhereApi.regional {
            defaultState (fun () -> ())
            handle (_.whichSilo) (handler "regional")
        }

/// <summary>Registers the everywhere grain on every silo, the regional grain on non-primaries.</summary>
type TourSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain WhereDefinition.everywhere |> ignore

            if siloBuilder.Configuration.["Orleans:Name"] <> Cluster.PrimarySiloName then
                siloBuilder.AddFunctionalGrain WhereDefinition.regional |> ignore

/// <summary>Installs the fixed functional transport on the external client.</summary>
type TourClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

/// <summary>What one heterogeneous run observed.</summary>
type ClusterObservation =
    { siloNames: string list
      everywherePlacements: (string * string) list
      regionalPlacements: (string * string) list
      regionalHostSilos: string list }

[<RequireQualifiedAccess>]
module HeterogeneousRun =

    /// <summary>
    /// Placement only spreads once every silo's cluster manifest carries every other silo's
    /// grain manifest; until then a silo believes it is the only host of a grain type.
    /// </summary>
    let private waitForManifestPropagation (cluster: TestCluster) =
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        let propagated () =
            cluster.Silos
            |> Seq.forall (fun handle ->
                let services = (handle :?> InProcessSiloHandle).SiloHost.Services
                let current = services.GetRequiredService<IClusterManifestProvider>().Current

                current.Silos.Count = cluster.Silos.Count
                && current.Silos
                   |> Seq.forall (fun pair ->
                       pair.Value.Grains.ContainsKey(GrainType.Create Cluster.EverywhereGrainType)))

        while not (propagated ()) && DateTime.UtcNow < deadline do
            Thread.Sleep 200

        propagated ()

    /// <summary>
    /// Deploy a two-silo cluster, drive both grain types from the external client, and report
    /// which silo actually ran each call.
    /// </summary>
    let run () : Task<ClusterObservation> =
        task {
            let builder = TestClusterBuilder 2s
            // Silos advertise different definitions, so the homogeneity shortcut must be off:
            // with it on, Orleans assumes every silo hosts every grain type.
            builder.Options.AssumeHomogenousSilosForTesting <- false
            builder.AddSiloBuilderConfigurator<TourSiloConfigurator>() |> ignore
            builder.AddClientBuilderConfigurator<TourClientConfigurator>() |> ignore

            let cluster = builder.Build()
            cluster.Deploy()
            do! cluster.WaitForLivenessToStabilizeAsync()
            waitForManifestPropagation cluster |> ignore

            try
                let client = cluster.Client
                let keys = [ for index in 1..8 -> $"key-{index}" ]

                let! everywherePlacements =
                    keys
                    |> List.map (fun key ->
                        task {
                            let! silo = (WhereApi.everywhereRef client key).whichSilo ()
                            return key, silo
                        })
                    |> Task.WhenAll

                let! regionalPlacements =
                    keys
                    |> List.map (fun key ->
                        task {
                            let! silo = (WhereApi.regionalRef client key).whichSilo ()
                            return key, silo
                        })
                    |> Task.WhenAll

                let regionalHosts =
                    cluster.Silos
                    |> Seq.filter (fun handle ->
                        let services = (handle :?> InProcessSiloHandle).SiloHost.Services

                        services
                            .GetRequiredService<IClusterManifestProvider>()
                            .LocalGrainManifest.Grains.ContainsKey(GrainType.Create Cluster.RegionalGrainType))
                    |> Seq.map _.Name
                    |> List.ofSeq

                return
                    { siloNames = cluster.Silos |> Seq.map _.Name |> List.ofSeq
                      everywherePlacements = List.ofArray everywherePlacements
                      regionalPlacements = List.ofArray regionalPlacements
                      regionalHostSilos = regionalHosts }
            finally
                cluster.StopAllSilos()
                cluster.Dispose()
        }
