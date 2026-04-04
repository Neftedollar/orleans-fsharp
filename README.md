# Orleans.FSharp

**Idiomatic F# for Microsoft Orleans -- computation expressions, not boilerplate**

[![CI](https://github.com/Neftedollar/orleans-fsharp/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/orleans-fsharp/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Orleans 10](https://img.shields.io/badge/Orleans-10.0.1-blue)](https://learn.microsoft.com/dotnet/orleans/)
[![F#](https://img.shields.io/badge/F%23-9%2B-378BBA)](https://fsharp.org/)
[![Tests](https://img.shields.io/badge/tests-1277-brightgreen)]()
[![NuGet](https://img.shields.io/nuget/v/Orleans.FSharp.svg)](https://www.nuget.org/packages/Orleans.FSharp)

---

## Why this exists

Orleans is a powerful virtual actor framework, but using it from F# means fighting C# idioms at every turn: mutable state bags, attribute-heavy classes, verbose DI wiring. Orleans.FSharp replaces all of that with computation expressions that let you define grains, configure silos, and wire streaming in natural F# style -- discriminated unions as state, pure handler functions, and declarative configuration. The full Orleans runtime does the heavy lifting underneath.

## Quick Start

```fsharp
open Orleans.FSharp
open Orleans.FSharp.Runtime

// 1. Define state and commands — plain F# types, no attributes needed
type CounterState = { Count: int }
type CounterCommand = Increment | Decrement | GetValue

// 2. Define the grain with a computation expression
let counter = grain {
    defaultState { Count = 0 }
    handle (fun state cmd -> task {
        match cmd with
        | Increment -> return { Count = state.Count + 1 }, box (state.Count + 1)
        | Decrement -> return { Count = state.Count - 1 }, box (state.Count - 1)
        | GetValue  -> return state, box state.Count
    })
    persist "Default"
}

// 3. Configure the silo — clean F#, no C# extension method chains
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    useJsonFallbackSerialization  // enables clean types without Orleans attributes
}
```

## Feature Showcase

### `grain { }` -- Grain Definition

| Keyword | Description |
|---|---|
| `defaultState` | Set the initial state value |
| `handle` | Register a `state -> msg -> Task<state * obj>` handler |
| `handleState` | Simpler: `state -> msg -> Task<state>` — result IS the new state |
| `handleTyped` | Typed result without manual boxing: `state -> msg -> Task<state * 'R>` |
| `handleWithContext` | Handler with `GrainContext` for grain-to-grain calls and DI |
| `handleStateWithContext` | `GrainContext` + state-only result |
| `handleTypedWithContext` | `GrainContext` + typed result |
| `handleWithServices` | Alias for `handleWithContext` emphasizing DI access |
| `handleStateWithServices` | Services + state-only result |
| `handleTypedWithServices` | Services + typed result |
| `handleCancellable` | Handler with `CancellationToken` support |
| `handleStateCancellable` | State-only result + cancellation |
| `handleTypedCancellable` | Typed result + cancellation |
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

### Universal Grain Pattern — zero C# stubs

Call any registered F# grain without defining a per-grain C# interface:

```fsharp
// Silo startup — register your grain definition
siloBuilder.Services.AddFSharpGrain<PingState, PingCommand>(pingGrain) |> ignore

// Client / handler — string, GUID, or int key
let handle = FSharpGrain.ref<PingState, PingCommand> factory "ping-1"
let! state  = handle |> FSharpGrain.send Ping          // returns Task<PingState>
do! handle  |> FSharpGrain.post Ping                   // fire-and-forget Task

// ask returns a type you choose — useful when the handler returns something other than the state
let! count  = handle |> FSharpGrain.ask<PingState, PingCommand, int> GetCount

// GUID and integer keys
let h = FSharpGrain.refGuid<S, M> factory (Guid.NewGuid())
let! s = h |> FSharpGrain.sendGuid MyCommand
let! r = h |> FSharpGrain.askGuid<S, M, string> QueryCmd

let h = FSharpGrain.refInt<S, M> factory 42L
do! h |> FSharpGrain.postInt MyCommand
```

The universal pattern works with any F# discriminated union as the command type — including cases with fields (`Append of string`) and nullary cases in mixed DUs. No CodeGen project is required; Orleans discovers the grains through `Orleans.FSharp.Abstractions`.

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

Silo-side proxy generation (required for Orleans to locate your grains):

```bash
dotnet add package Orleans.FSharp.Abstractions
```

Optional packages:

```bash
dotnet add package Orleans.FSharp.EventSourcing  # Event sourcing
dotnet add package Orleans.FSharp.Testing         # Test harness + FsCheck
```

> **Why `Orleans.FSharp.Abstractions`?** Orleans source generators only run on C# projects.
> `Abstractions` is a tiny C# shim (no code to write) that lets the Orleans runtime generate
> the proxy classes for `IFSharpGrain`. Reference it from your silo project — that's it.

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
| [Serialization](docs/serialization.md) | 3 modes: F# Binary, JSON, Orleans Native |
| [Streaming](docs/streaming.md) | Publish, subscribe, TaskSeq, broadcast |
| [Event Sourcing](docs/event-sourcing.md) | `eventSourcedGrain { }` CE guide |
| [Testing](docs/testing.md) | TestHarness, FsCheck, GrainMock |
| [Analyzers](docs/analyzers.md) | OF0001: async {} detection, AllowAsync opt-out |
| [Security](docs/security.md) | TLS, mTLS, filters, secrets |
| [Advanced](docs/advanced.md) | Transactions, telemetry, shutdown, migration |
| [API Reference](docs/api-reference.md) | All public modules, types, functions |

## Package Structure

| Package | Description |
|---|---|
| `Orleans.FSharp` | Core: grain CE, GrainRef, streaming, logging, reminders, timers, observers, serialization |
| `Orleans.FSharp.Runtime` | Silo hosting, client config, grain discovery |
| `Orleans.FSharp.Abstractions` | C# shim — Orleans proxy generation for `IFSharpGrain` (reference from silo) |
| `Orleans.FSharp.EventSourcing` | Event-sourced grain CE |
| `Orleans.FSharp.CodeGen` | Optional: per-grain C# code generation for custom grain interfaces (legacy pattern) |
| `Orleans.FSharp.Testing` | Test harness, GrainArbitrary, GrainMock, log capture |
| `Orleans.FSharp.Analyzers` | F# analyzer: OF0001 warns on `async { }` usage; `[<AllowAsync>]` opt-out |
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
