# Orleans.FSharp

**Idiomatic F# for Microsoft Orleans -- computation expressions, not boilerplate**

[![Build](https://github.com/orleans-fsharp/orleans-fsharp/actions/workflows/ci.yml/badge.svg)](https://github.com/orleans-fsharp/orleans-fsharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Orleans.FSharp.svg)](https://www.nuget.org/packages/Orleans.FSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Why this exists

Orleans is a powerful virtual actor framework, but using it from F# means fighting C# idioms at every turn: mutable state bags, attribute-heavy classes, verbose DI wiring. Orleans.FSharp replaces all of that with computation expressions that let you define grains, configure silos, and wire streaming in natural F# style -- discriminated unions as state, pure handler functions, and declarative configuration. The full Orleans runtime does the heavy lifting underneath.

## Quick Start

```fsharp
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime

// 1. Define grain state and commands as DUs
[<GenerateSerializer>]
type CounterState = | [<Id(0u)>] Zero | [<Id(1u)>] Count of int

[<GenerateSerializer>]
type CounterCommand = | [<Id(0u)>] Increment | [<Id(1u)>] GetValue

// 2. Define the grain with a CE
let counter = grain {
    defaultState Zero
    handle (fun state cmd -> task {
        match state, cmd with
        | Zero, Increment -> return Count 1, box 1
        | Count n, Increment -> return Count(n + 1), box(n + 1)
        | _, GetValue ->
            let v = match state with Zero -> 0 | Count n -> n
            return state, box v
    })
    persist "Default"
}

// 3. Configure and start the silo
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}
```

## Feature Showcase

### `grain { }` -- Grain Definition

| Keyword | Description |
|---|---|
| `defaultState` | Set the initial state value |
| `handle` | Register a `state -> msg -> Task<state * obj>` handler |
| `handleWithContext` | Handler with `GrainContext` for grain-to-grain calls and DI |
| `handleWithServices` | Alias for `handleWithContext` emphasizing DI access |
| `handleCancellable` | Handler with `CancellationToken` support |
| `handleWithContextCancellable` | Context + cancellation |
| `handleWithServicesCancellable` | Services + cancellation |
| `persist` | Name the storage provider for state persistence |
| `additionalState` | Declare a named secondary persistent state |
| `onActivate` | Hook that runs on grain activation |
| `onDeactivate` | Hook that runs on grain deactivation |
| `onReminder` | Register a named reminder handler |
| `onTimer` | Register a declarative timer with dueTime + period |
| `onLifecycleStage` | Hook into grain lifecycle stages |
| `reentrant` | Allow concurrent message processing |
| `interleave` | Mark a method as always interleaved |
| `readOnly` | Mark a method as read-only (interleaved for reads) |
| `mayInterleave` | Custom reentrancy predicate |
| `statelessWorker` | Allow multiple activations per silo |
| `maxActivations` | Cap local worker count |
| `oneWay` | Mark a method as fire-and-forget |
| `grainType` | Set a custom grain type name |
| `deactivationTimeout` | Per-grain idle timeout |
| `implicitStreamSubscription` | Auto-subscribe to a stream namespace |
| `preferLocalPlacement` | Place grain on the calling silo |
| `randomPlacement` | Random silo placement |
| `hashBasedPlacement` | Consistent-hash placement |
| `activationCountPlacement` | Fewest-activations placement |
| `resourceOptimizedPlacement` | Resource-aware placement |
| `siloRolePlacement` | Role-based silo targeting |
| `customPlacement` | Custom placement strategy type |

### `siloConfig { }` -- Silo Configuration

| Keyword | Description |
|---|---|
| `useLocalhostClustering` | Local dev clustering |
| `addRedisClustering` | Redis-based clustering |
| `addAzureTableClustering` | Azure Table clustering |
| `addAdoNetClustering` | ADO.NET clustering (Postgres, SQL Server) |
| `addMemoryStorage` | In-memory grain storage |
| `addRedisStorage` | Redis grain storage |
| `addAzureBlobStorage` | Azure Blob grain storage |
| `addAzureTableStorage` | Azure Table grain storage |
| `addAdoNetStorage` | ADO.NET grain storage |
| `addCosmosStorage` | Cosmos DB grain storage |
| `addDynamoDbStorage` | DynamoDB grain storage |
| `addCustomStorage` | Custom storage provider |
| `addMemoryStreams` | In-memory stream provider |
| `addPersistentStreams` | Durable stream provider |
| `addBroadcastChannel` | Broadcast channel provider |
| `addMemoryReminderService` | In-memory reminders |
| `addRedisReminderService` | Redis reminders |
| `addCustomReminderService` | Custom reminder service |
| `useSerilog` | Wire Serilog as logging provider |
| `configureServices` | Register custom DI services |
| `addIncomingFilter` | Incoming grain call filter |
| `addOutgoingFilter` | Outgoing grain call filter |
| `addGrainService` | Register a GrainService type |
| `addStartupTask` | Run a task when the silo starts |
| `enableHealthChecks` | Register health check endpoints |
| `useTls` / `useTlsWithCertificate` | TLS encryption |
| `useMutualTls` / `useMutualTlsWithCertificate` | Mutual TLS |
| `addDashboard` / `addDashboardWithOptions` | Orleans Dashboard |
| `useGrainVersioning` | Grain interface versioning |
| `clusterId` / `serviceId` / `siloName` | Cluster identity |
| `siloPort` / `gatewayPort` / `advertisedIpAddress` | Endpoints |
| `grainCollectionAge` | Global idle deactivation timeout |

### `clientConfig { }` -- Client Configuration

| Keyword | Description |
|---|---|
| `useLocalhostClustering` | Local dev clustering |
| `useStaticClustering` | Static gateway endpoints |
| `addMemoryStreams` | In-memory stream provider |
| `configureServices` | Register custom DI services |
| `useTls` / `useTlsWithCertificate` | TLS encryption |
| `useMutualTls` | Mutual TLS |
| `clusterId` / `serviceId` | Cluster identity |
| `gatewayListRefreshPeriod` | Gateway refresh interval |
| `preferredGatewayIndex` | Preferred gateway |

### `eventSourcedGrain { }` -- Event Sourcing

| Keyword | Description |
|---|---|
| `defaultState` | Initial state before any events |
| `apply` | Pure event fold: `state -> event -> state` |
| `handle` | Command handler: `state -> command -> event list` |
| `logConsistencyProvider` | Orleans log consistency provider name |

## Installation

```bash
dotnet add package Orleans.FSharp
dotnet add package Orleans.FSharp.Runtime
```

Optional packages:

```bash
dotnet add package Orleans.FSharp.EventSourcing  # Event sourcing
dotnet add package Orleans.FSharp.Testing         # Test harness + FsCheck
dotnet add package Orleans.FSharp.Analyzers       # Compile-time checks
```

## Project Template

Scaffold a new project in seconds:

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyApp
```

## Documentation

| Guide | Description |
|---|---|
| [Getting Started](docs/getting-started.md) | Zero to working grain in 15 minutes |
| [Grain Definition](docs/grain-definition.md) | Complete `grain { }` CE reference |
| [Silo Configuration](docs/silo-configuration.md) | Complete `siloConfig { }` CE reference |
| [Client Configuration](docs/client-configuration.md) | `clientConfig { }` CE reference |
| [Streaming](docs/streaming.md) | Publish, subscribe, TaskSeq, broadcast |
| [Event Sourcing](docs/event-sourcing.md) | `eventSourcedGrain { }` CE guide |
| [Testing](docs/testing.md) | TestHarness, FsCheck, GrainMock |
| [Security](docs/security.md) | TLS, mTLS, filters, secrets |
| [Advanced](docs/advanced.md) | Transactions, telemetry, shutdown, migration |
| [Korat Integration](docs/korat-integration.md) | Using Orleans.FSharp with Korat |
| [API Reference](docs/api-reference.md) | All public modules, types, functions |

## Package Structure

| Package | Description |
|---|---|
| `Orleans.FSharp` | Core: grain CE, GrainRef, streaming, logging, reminders, timers, observers, serialization |
| `Orleans.FSharp.Runtime` | Silo hosting, client config, grain discovery |
| `Orleans.FSharp.EventSourcing` | Event-sourced grain CE |
| `Orleans.FSharp.CodeGen` | C# code generation for F# grain types |
| `Orleans.FSharp.Testing` | Test harness, GrainArbitrary, GrainMock, log capture |
| `Orleans.FSharp.Analyzers` | Roslyn analyzer for compile-time checks |
| `Orleans.FSharp.Templates` | `dotnet new` project template |

## Security

### Connection Strings

Never inline connection strings containing passwords or secrets in source code. Load them from configuration or environment variables at runtime.

**Recommended:** Use `IConfiguration` or environment variables:

```fsharp
let connStr = Environment.GetEnvironmentVariable("REDIS_CONNECTION")

let config = siloConfig {
    useLocalhostClustering
    addRedisStorage "Default" connStr
}
```

**Avoid:** Hardcoding secrets in source files:

```fsharp
// DO NOT do this -- secrets will leak into version control
addRedisStorage "Default" "redis://user:password@host:6379"
```

### TLS Certificates

When using `useTls` or `useMutualTls`, always use valid certificates from a trusted certificate authority in production. Do not disable certificate validation in production environments.

## Contributing

Contributions are welcome! Please open an issue or pull request on [GitHub](https://github.com/orleans-fsharp/orleans-fsharp).

## License

This project is licensed under the [MIT License](LICENSE).
