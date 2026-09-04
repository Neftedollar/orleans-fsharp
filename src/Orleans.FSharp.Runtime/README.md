# Orleans.FSharp.Runtime

F# configuration and hosting support for Orleans silos, clients, and functional grain definitions.

## Quick example

```fsharp
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Orleans.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime

type HealthActor = private HealthActor of unit

[<NoEquality; NoComparison>]
type HealthApi = { ping: unit -> Task<string> }

let healthContract =
    grainContract<HealthActor, string, HealthApi> {
        grainType "health"
        version 1
        stringKey
        readOnly (_.ping)
    }

let healthDefinition =
    grainFor healthContract {
        defaultState (fun () -> ())
        handleQuery (_.ping) (fun _ () () -> task { return "pong" })
    }

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        addMemoryStreams "StreamProvider"
        addMemoryReminderService
        enableHealthChecks
    }

let builder = HostApplicationBuilder()
SiloConfig.applyToHost config builder

builder.UseOrleans(fun siloBuilder ->
    siloBuilder.AddFunctionalGrain(healthDefinition) |> ignore)
|> ignore

let host = builder.Build()
host.Start()
```

`healthDefinition` is the sealed value returned by `grainFor`. Use
`AddFunctionalJournaledGrain` for `journaledGrainFor` definitions. A client-only process applies
`clientConfig { }` and calls `AddFunctionalGrainClient()` on its `IClientBuilder`.

## Configuration surface

| Category | Current operations |
|---|---|
| Clustering | `useLocalhostClustering`, `addRedisClustering`, `addAzureTableClustering`, `addAdoNetClustering` |
| Storage | `addMemoryStorage`, `addRedisStorage`, `addAzureBlobStorage`, `addAzureTableStorage`, `addAdoNetStorage`, `addCosmosStorage`, `addDynamoDbStorage`, `addCustomStorage` |
| Streaming | `addMemoryStreams`, `addPersistentStreams`, `addBroadcastChannel` |
| Reminders | `addMemoryReminderService`, `addRedisReminderService`, `addCustomReminderService` |
| Security | `useTls`, `useTlsWithCertificate`, `useMutualTls`, `useMutualTlsWithCertificate` |
| Operations | `useSerilog`, `addDashboard`, `addDashboardWithOptions`, `enableHealthChecks`, filters, services, and startup tasks |

`addDashboardWithOptions` takes `counterUpdateIntervalMs`, `historyLength`, and `hideTrace`, in
that order. The optional `Microsoft.Orleans.Dashboard` package must be referenced by the host.

See [Silo configuration](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/silo-configuration.md),
[Client configuration](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/client-configuration.md),
and [Dashboard](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/dashboard.md).

## Requirements

- .NET 10+
- `Orleans.FSharp` (transitive)

## Legacy archive

Some compatibility internals remain temporarily for migration, but the old authoring model is
unsupported and receives no 5.x feature, compatibility, or security work. Its documentation is
isolated under [docs/legacy](https://github.com/Neftedollar/orleans-fsharp/tree/main/docs/legacy).

## License

MIT
