module Orleans.FSharp.Tests.ClientConfigTests

open Xunit
open Swensen.Unquote
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp.Runtime

/// <summary>Helper to check if a ClientClusteringMode is Localhost.</summary>
let isLocalhostClient =
    function
    | ClientClusteringMode.Localhost -> true
    | _ -> false

/// <summary>Helper to check if a ClientClusteringMode is StaticGateway.</summary>
let isStaticGateway =
    function
    | StaticGateway _ -> true
    | _ -> false

[<Fact>]
let ``clientConfig CE produces default config`` () =
    let config = clientConfig { () }
    test <@ config.ClusteringMode.IsNone @>
    test <@ config.StreamProviders |> Map.isEmpty @>
    test <@ config.CustomServices.Length = 0 @>

[<Fact>]
let ``clientConfig CE sets useLocalhostClustering`` () =
    let config = clientConfig { useLocalhostClustering }
    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusteringMode.Value |> isLocalhostClient @>

[<Fact>]
let ``clientConfig CE sets useStaticClustering`` () =
    let config =
        clientConfig {
            useStaticClustering [ "127.0.0.1:30000"; "127.0.0.1:30001" ]
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusteringMode.Value |> isStaticGateway @>

[<Fact>]
let ``clientConfig CE static clustering preserves endpoints`` () =
    let config =
        clientConfig {
            useStaticClustering [ "10.0.0.1:30000"; "10.0.0.2:30000" ]
        }

    match config.ClusteringMode.Value with
    | StaticGateway eps -> test <@ eps.Length = 2 @>
    | _ -> failwith "Expected StaticGateway"

[<Fact>]
let ``clientConfig CE adds memory streams`` () =
    let config = clientConfig { addMemoryStreams "StreamProvider" }
    test <@ config.StreamProviders |> Map.containsKey "StreamProvider" @>

    match config.StreamProviders.["StreamProvider"] with
    | MemoryStream -> ()
    | other -> failwith $"Expected MemoryStream, got {other}"

[<Fact>]
let ``clientConfig CE adds custom services`` () =
    let mutable called = false

    let config =
        clientConfig {
            configureServices (fun (_services: IServiceCollection) -> called <- true)
        }

    test <@ config.CustomServices.Length = 1 @>
    let services = ServiceCollection() :> IServiceCollection
    config.CustomServices |> List.iter (fun f -> f services)
    test <@ called @>

[<Fact>]
let ``clientConfig CE composes all options`` () =
    let config =
        clientConfig {
            useLocalhostClustering
            addMemoryStreams "StreamProvider"
            configureServices (fun _ -> ())
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StreamProviders |> Map.containsKey "StreamProvider" @>
    test <@ config.CustomServices.Length = 1 @>

[<Fact>]
let ``clientConfig CE later clustering overrides earlier`` () =
    let config =
        clientConfig {
            useLocalhostClustering
            useStaticClustering [ "127.0.0.1:30000" ]
        }

    test <@ config.ClusteringMode.Value |> isStaticGateway @>

[<Fact>]
let ``clientConfig CE multiple configureServices accumulate`` () =
    let mutable count = 0

    let config =
        clientConfig {
            configureServices (fun _ -> count <- count + 1)
            configureServices (fun _ -> count <- count + 1)
        }

    test <@ config.CustomServices.Length = 2 @>
    let services = ServiceCollection() :> IServiceCollection
    config.CustomServices |> List.iter (fun f -> f services)
    test <@ count = 2 @>

[<Fact>]
let ``ClientConfig.Default has empty values`` () =
    let config = ClientConfig.Default
    test <@ config.ClusteringMode.IsNone @>
    test <@ config.StreamProviders |> Map.isEmpty @>
    test <@ config.CustomServices.Length = 0 @>

[<Fact>]
let ``ClientConfig.validate detects missing clustering`` () =
    let config = clientConfig { addMemoryStreams "SP" }

    let errors = ClientConfig.validate config
    test <@ errors |> List.exists (fun e -> e.Contains("clustering")) @>

[<Fact>]
let ``ClientConfig.validate passes with clustering set`` () =
    let config = clientConfig { useLocalhostClustering }

    let errors = ClientConfig.validate config
    test <@ errors = [] @>
