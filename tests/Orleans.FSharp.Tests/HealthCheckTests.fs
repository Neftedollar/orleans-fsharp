module Orleans.FSharp.Tests.HealthCheckTests

open Xunit
open Swensen.Unquote
open System
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Runtime

[<Fact>]
let ``siloConfig CE default has health checks disabled`` () =
    let config = siloConfig { () }
    test <@ config.EnableHealthChecks = false @>

[<Fact>]
let ``siloConfig CE enables health checks`` () =
    let config = siloConfig { enableHealthChecks }
    test <@ config.EnableHealthChecks = true @>

[<Fact>]
let ``siloConfig CE health checks compose with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            enableHealthChecks
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.EnableHealthChecks = true @>

[<Fact>]
let ``SiloConfig.Default has health checks disabled`` () =
    let config = SiloConfig.Default
    test <@ config.EnableHealthChecks = false @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``enableHealthChecks is idempotent — toggling twice still yields true`` () =
    let config =
        siloConfig {
            enableHealthChecks
            enableHealthChecks
        }

    config.EnableHealthChecks = true

[<Property>]
let ``enableHealthChecks does not disturb any storage provider name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config =
            siloConfig {
                addMemoryStorage name.Get
                enableHealthChecks
            }

        config.EnableHealthChecks = true
        && config.StorageProviders |> Map.containsKey name.Get)
