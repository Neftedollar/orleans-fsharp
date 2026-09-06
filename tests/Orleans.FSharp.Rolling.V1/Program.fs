module Orleans.FSharp.Rolling.V1.Program

open System
open System.Net
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
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
        version 1
        stringKey
        readOnly (_.identify)
    }

let private definition =
    grainFor contract {
        defaultState (fun () -> ())
        handle (_.identify) (fun _ state () -> task { return state, "v1" })
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

    if arguments |> Array.contains "--store" then
        builder.UseOrleans(fun silo -> Orleans.FSharp.Rolling.Durable.configure (argument "--store" arguments) silo)
        |> ignore

    let host = builder.Build()

    task {
        do! host.StartAsync()
        Console.Out.WriteLine "READY v1"
        Console.Out.Flush()
        let mutable running = true
        while running do
            let! line = Console.In.ReadLineAsync()
            if isNull line || line = "stop" then running <- false
            else
                try
                    let! result =
                        Orleans.FSharp.Rolling.Durable.command
                            (host.Services.GetRequiredService<Orleans.IGrainFactory>()) line
                    Console.Out.WriteLine $"RESULT {result}"
                with error -> Console.Out.WriteLine $"ERROR {error}"
                Console.Out.Flush()
        do! host.StopAsync()
    }
    |> fun work -> work.GetAwaiter().GetResult()

    0
