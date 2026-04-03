module Orleans.FSharp.Tests.RedisReminderTests

open Xunit
open Swensen.Unquote
open Orleans.FSharp.Runtime

/// <summary>Helper to check if a ReminderProvider is RedisReminder.</summary>
let isRedisReminder =
    function
    | RedisReminder _ -> true
    | _ -> false

[<Fact>]
let ``siloConfig CE default has no reminder provider`` () =
    let config = siloConfig { () }
    test <@ config.ReminderProvider.IsNone @>

[<Fact>]
let ``siloConfig CE adds Redis reminder service`` () =
    let config = siloConfig { addRedisReminderService "localhost:6379" }
    test <@ config.ReminderProvider.IsSome @>
    test <@ config.ReminderProvider.Value |> isRedisReminder @>

[<Fact>]
let ``siloConfig CE Redis reminder stores connection string`` () =
    let config = siloConfig { addRedisReminderService "redis.example.com:6379" }

    match config.ReminderProvider.Value with
    | RedisReminder connStr -> test <@ connStr = "redis.example.com:6379" @>
    | other -> failwith $"Expected RedisReminder, got {other}"

[<Fact>]
let ``siloConfig CE Redis reminder overrides memory reminder`` () =
    let config =
        siloConfig {
            addMemoryReminderService
            addRedisReminderService "localhost:6379"
        }

    test <@ config.ReminderProvider.Value |> isRedisReminder @>

[<Fact>]
let ``siloConfig CE memory reminder overrides Redis reminder`` () =
    let config =
        siloConfig {
            addRedisReminderService "localhost:6379"
            addMemoryReminderService
        }

    test <@ config.ReminderProvider.Value |> (function MemoryReminder -> true | _ -> false) @>

[<Fact>]
let ``siloConfig CE Redis reminder composes with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            addRedisReminderService "localhost:6379"
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.ReminderProvider.IsSome @>
    test <@ config.ReminderProvider.Value |> isRedisReminder @>

[<Fact>]
let ``siloConfig CE Redis reminder composes with TLS and dashboard`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            useTls "my-cert"
            addDashboard
            addRedisReminderService "localhost:6379"
        }

    test <@ config.TlsConfig.IsSome @>
    test <@ config.DashboardConfig.IsSome @>
    test <@ config.ReminderProvider.Value |> isRedisReminder @>
