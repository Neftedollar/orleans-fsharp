module Orleans.FSharp.Rolling.V2.Program

open System
open System.Net
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime

type RollingActor = RollingActor of unit

[<NoEquality; NoComparison>]
type RollingApi =
    { identify: unit -> Task<string> }

let private contract =
    grainContract<RollingActor, string, RollingApi> {
        grainType "rolling.functional.probe"
        version 2
        acceptsVersions (Orleans.FSharp.BackwardCompatible 1)
        stringKey
        readOnly (_.identify)
    }

let private definition =
    grainFor contract {
        defaultState (fun () -> ())
        handle (_.identify) (fun _ state () -> task { return state, "v2" })
    }

let private argument name (arguments: string[]) =
    arguments
    |> Array.tryFindIndex ((=) name)
    |> Option.bind (fun index -> arguments |> Array.tryItem (index + 1))
    |> Option.defaultWith (fun () -> invalidArg (nameof arguments) $"missing required argument '{name}'")

[<EntryPoint>]
let main arguments =
    let siloPort = argument "--silo-port" arguments |> Int32.Parse
    let gatewayPort = argument "--gateway-port" arguments |> Int32.Parse
    let primaryPort = argument "--primary-port" arguments |> Int32.Parse
    let serviceId = argument "--service-id" arguments
    let clusterId = argument "--cluster-id" arguments
    let siloName = argument "--silo-name" arguments
    let primaryEndpoint = IPEndPoint(IPAddress.Loopback, primaryPort)

    let config =
        { SiloConfig.Default with
            ClusteringMode =
                Some(
                    CustomClustering(fun silo ->
                        silo.UseLocalhostClustering(
                            siloPort = siloPort,
                            gatewayPort = gatewayPort,
                            primarySiloEndpoint = primaryEndpoint,
                            serviceId = serviceId,
                            clusterId = clusterId
                        ))
                )
            VersioningConfig =
                Some(
                    Orleans.FSharp.Versioning.CompatibilityStrategy.BackwardCompatible,
                    Orleans.FSharp.Versioning.VersionSelectorStrategy.AllCompatibleVersions
                )
            SiloName = Some siloName }

    let builder = Host.CreateApplicationBuilder()
    builder.Logging.ClearProviders() |> ignore
    SiloConfig.applyToHost config builder

    builder.UseOrleans(fun silo -> silo.AddFunctionalGrain(definition) |> ignore)
    |> ignore

    let host = builder.Build()

    task {
        do! host.StartAsync()
        Console.Out.WriteLine "READY v2"
        Console.Out.Flush()
        let! _ = Console.In.ReadLineAsync()
        do! host.StopAsync()
    }
    |> fun work -> work.GetAwaiter().GetResult()

    0
