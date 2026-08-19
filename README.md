# Orleans.FSharp

<p align="center">
  <img src="https://raw.githubusercontent.com/Neftedollar/orleans-fsharp/main/website/public/orleans-fsharp-logo.svg" alt="Orleans.FSharp logo" width="156" height="156" />
</p>

**Idiomatic F# for Microsoft Orleans -- computation expressions, not boilerplate**

[![CI](https://github.com/Neftedollar/orleans-fsharp/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/orleans-fsharp/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Orleans 10](https://img.shields.io/badge/Orleans-10.1.0%20%E2%80%93%2010.2.2-blue)](https://learn.microsoft.com/dotnet/orleans/)
[![F#](https://img.shields.io/badge/F%23-9%2B-378BBA)](https://fsharp.org/)
[![Tests](https://img.shields.io/badge/tests-2500%2B-brightgreen)]()
[![NuGet](https://img.shields.io/nuget/v/Orleans.FSharp.svg)](https://www.nuget.org/packages/Orleans.FSharp)

---

## Why this exists

Orleans is a powerful virtual actor framework, but using it from F# means fighting C# idioms at every turn: mutable state bags, attribute-heavy classes, interface-plus-codegen ceremony. Orleans.FSharp replaces all of that: a grain's public surface is a plain F# record of functions, its behavior is pure `state -> reply` handlers, and silos are configured with computation expressions -- discriminated unions as state, explicit storage writes, no code generation anywhere. The full Orleans runtime does the heavy lifting underneath.

## Quick Start

The grain's public surface is a plain F# record of functions — no interface, no attributes,
no code generation:

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

// 1. The API record IS the grain's surface, and here also its type identity (the brand)
type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      value: unit -> Task<int> }

// 2. The contract: stable wire identity — grain type, version, key encoding
let contract =
    grainContract<CounterActor, string, CounterApi> () {
        grainType "counter"
        version 1
        stringKey
        readOnly (_.value)
    }

// 3. The definition: pure state -> reply handlers; storage is written only when you say so
let counter =
    grainFor contract {
        defaultState (fun () -> 0)
        handle (_.increment) (fun _ctx n () -> task { return n + 1, n + 1 })
        handle (_.value)     (fun _ctx n () -> task { return n, n })
    }
```

Host it and call it — the call site is the record itself:

```fsharp
// Silo: siloConfig { } for hosting, AddFunctionalGrain for the definition
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}

builder.UseOrleans(fun siloBuilder ->
    siloBuilder.AddFunctionalGrain(counter) |> ignore)

// Client or another grain — no proxy interface, no cast
let api = FunctionalGrain.ref contract factory "my-counter"
let! n = api.increment ()
```

Prefer an even shorter contract? `contract<'Key, 'Api>` uses the API record itself as the brand —
see [the short form](docs/functional-grains.md#the-short-form-the-api-record-as-its-own-brand).

> **Older authoring models.** The original `grain { }` CE (shown further below) still compiles
> and runs, but its public surface (`grain { }`, `GrainDefinition`, the old `GrainContext`,
> `[<FSharpGrain>]`, `AddFSharpGrain`, `FSharpGrain.*`, `Timers`, `Reminder`) carries
> `[<Obsolete>]` -- a **warning, not an error** -- and the classic `eventSourcedGrain { }` is
> superseded by the functional `journaledGrainFor`. See
> [Functional Grain Runtime](docs/functional-grains.md) for the before/after mapping of every
> deprecated entry point. `siloConfig { }` and `clientConfig { }` are current and unaffected.

## Feature Showcase

### Functional grain runtime — the current authoring model

Everything Orleans offers, as contract or definition operations — no attributes, no codegen:

| Where | Operations |
|---|---|
| `grainContract { }` | `grainType` (optional for ephemeral grains), `version`, key codecs (`stringKey` / `guidKey` / `int64Key`, compound + mapped forms), per-operation `readOnly` / `oneWay` / `alwaysInterleave` / `operationId` / `sinceVersion` / `transactional`, whole-grain `reentrant` / `mayInterleave`, `acceptsVersions` |
| `grainFor { }` | `defaultState` / `initialState` / `stateFrom`, `usePersistentState`, `transactionalStateFrom`, `onActivate` / `onDeactivate` / `onLifecycle`, `onTimer` / `onReminder`, `onStream` / `onBroadcast` (implicit subscriptions), `statelessWorker` / `placement`, `collectionAge`, `handle` / `handleStream` |
| `journaledGrainFor { }` | event sourcing over Orleans' own log-consistency providers: `initialEventState`, pure `apply` fold, handlers that raise events — see [Event Sourcing](docs/event-sourcing.md) |
| API field shapes | `'Arg -> Task<'Reply>` and `'Arg -> IAsyncEnumerable<'Item>` ([streaming replies](docs/streaming-replies.md)) |
| From C# | a typed facade over any contract: awaited calls and `await foreach` — [Calling from C#](docs/calling-from-csharp.md) |

### `grain { }` -- Grain Definition *(deprecated -- see [Functional Grain Runtime](docs/functional-grains.md))*

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
| `interleaveMessage` | Allow a message type to interleave: `interleaveMessage typeof<Query>` |

> **Per-grain Orleans attributes — use the C# CodeGen path.** `[Reentrant]`,
> `[StatelessWorker]`, `[MayInterleave]`, `[ReadOnly]`, `[OneWay]`, placement strategies,
> `[ImplicitStreamSubscription]`, and `[GrainType]` are applied through the per-grain
> `Orleans.FSharp.CodeGen` path, where each grain compiles to its own C# class/method that
> carries the real Orleans attribute. They are **not** `grain { }` CE keywords: the universal
> grain pattern shares a single `FSharpGrainImpl` class and one handler method, so per-grain
> class/method attributes cannot be expressed there. The one reentrancy lever that fits the
> universal pattern is `interleaveMessage typeof<'Msg>`.
>
> **This caveat is about the deprecated `grain { }` model only.** On the
> [functional grain runtime](docs/functional-grains.md) every one of those concepts is a
> first-class `grainContract` / `grainFor` operation — `readOnly`, `oneWay`, `alwaysInterleave`,
> `grainType`, `collectionAge`, `statelessWorker`, `placement`, and (spec 004 item 1)
> `onStream` / `onBroadcast` for implicit stream and broadcast-channel subscriptions. No C# and
> no code generation.

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

### Universal Grain Pattern *(deprecated — `FSharpGrain.*` carries `[<Obsolete>]`; the functional runtime is the codegen-free path)*

Call any registered F# grain without defining a per-grain C# interface:

```fsharp
// Silo startup — register your grain definition
siloBuilder.Services.AddFSharpGrain<PingState, PingCommand>(pingGrain) |> ignore

// Client / handler — string, GUID, or int key
let handle = FSharpGrain.ref<PingState, PingCommand> factory "ping-1"
let! state  = handle |> FSharpGrain.send Ping          // returns Task<PingState>
do! handle  |> FSharpGrain.post Ping                   // true one-way: fire-and-forget, no round-trip

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

### `eventSourcedGrain { }` -- Event Sourcing *(classic model; superseded by [`journaledGrainFor`](docs/event-sourcing.md))*

| Keyword | Description |
|---|---|
| `defaultState` | Initial state before any events |
| `apply` | Pure event fold: `state -> event -> state` |
| `handle` | Command handler: `state -> command -> event list` |
| `logConsistencyProvider` | Orleans log consistency provider name |

## Installation

```bash
dotnet add package Orleans.FSharp          # contracts, definitions, the functional runtime surface
dotnet add package Orleans.FSharp.Runtime  # silo/client hosting: AddFunctionalGrain, siloConfig { }
```

That is the whole functional-runtime setup: `Orleans.FSharp.Abstractions` (the fixed transport —
request envelopes, protocol tokens, and Orleans proxies precompiled once inside the package) comes
in transitively, and there is nothing to generate in your projects.

Optional packages:

```bash
dotnet add package Orleans.FSharp.Testing         # Test harness + FsCheck
dotnet add package Orleans.FSharp.EventSourcing   # the classic eventSourcedGrain { } model only —
                                                  # functional journaledGrainFor ships in the core package
```

## Project Template

Scaffold a new project in seconds:

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyApp
```

## Upgrading to 4.0

4.0 is the **functional-era major**: specs 003 and 004 in one release. The functional grain
runtime (`grainContract` / `grainFor` / `journaledGrainFor`) is the recommended authoring model,
with full Orleans parity as first-class operations — transactions, event sourcing over Orleans'
log-consistency providers, implicit stream subscriptions, `IAsyncEnumerable` streaming replies,
reentrancy policies, version-tolerant contracts, placement, lifecycle hooks, and a typed C#
facade. Everything you had keeps compiling: the old `grain { }` / `FSharpGrain.*` surface is
`[<Obsolete>]` **warnings**, each message naming its replacement. The placeholder
`Orleans.FSharp.EventSourcing.Marten` package (which never contained a Marten integration) was
removed and delisted. Details in the [CHANGELOG](CHANGELOG.md).

## Upgrading to 3.0

3.0 is a **breaking major**. The Universal Grain Pattern (`AddFSharpGrain` +
`FSharpGrain.ref`/`send`/`ask`/`post`) became the canonical path within the `grain { }` model --
note that the whole `grain { }` model is now itself deprecated in favour of the
[functional grain runtime](docs/functional-grains.md), though it keeps working. The non-functional
`grain { }` CE keywords that were deprecated in 2.x have been **removed**: `reentrant`,
`statelessWorker`, `maxActivations`, the old string-based `mayInterleave`, `interleave`,
`oneWay`, `readOnly`, `grainType`, `deactivationTimeout`, `implicitStreamSubscription`, and the
placement operations (`preferLocalPlacement`, `randomPlacement`, `hashBasedPlacement`,
`activationCountPlacement`, `resourceOptimizedPlacement`, `siloRolePlacement`,
`customPlacement`). To apply the equivalent Orleans attributes per grain, use the
`Orleans.FSharp.CodeGen` path. To allow a message type to interleave under the universal
pattern, use `interleaveMessage typeof<'Msg>`. `FSharpGrain.post` is now a **true one-way**
(fire-and-forget) call. See the [CHANGELOG](CHANGELOG.md) for the full breaking-change list.

## Documentation

| Guide | Description |
|---|---|
| [Getting Started](docs/getting-started.md) | Zero to working grain in 15 minutes |
| [Grain Definition](docs/grain-definition.md) | Complete `grain { }` CE reference (deprecated authoring model) |
| [Functional Grain Runtime](docs/functional-grains.md) | User-authored API records: contracts, key codecs, delivery semantics, immutable state |
| [Silo Configuration](docs/silo-configuration.md) | Complete `siloConfig { }` CE reference |
| [Client Configuration](docs/client-configuration.md) | `clientConfig { }` CE reference |
| [Serialization](docs/serialization.md) | 3 modes: F# Binary, JSON, Orleans Native |
| [Streaming](docs/streaming.md) | Publish, subscribe, TaskSeq, broadcast |
| [Event Sourcing](docs/event-sourcing.md) | `journaledGrainFor { }` — a grain whose state is the fold of an event journal (and the deprecated `eventSourcedGrain { }` CE) |
| [Server-Streaming Replies](docs/streaming-replies.md) | `'Arg -> IAsyncEnumerable<'Item>` — items delivered as they are produced, over Orleans' async-enumerable grain extension |
| [Testing](docs/testing.md) | TestHarness, FsCheck, GrainMock |
| [Analyzers](docs/analyzers.md) | OF0001: async {} detection, AllowAsync opt-out |
| [Security](docs/security.md) | TLS, mTLS, filters, secrets |
| [Advanced](docs/advanced.md) | Transactions, OpenTelemetry, shutdown, migration |
| [Resilience](docs/resilience.md) | Polly v8 retry, circuit-breaker, and timeout patterns |
| [Calling from C#](docs/calling-from-csharp.md) | Bind a hand-written C# interface to a functional grain contract |
| [Redis Example](docs/redis-example.md) | End-to-end shopping cart with Redis storage/clustering |
| [API Reference](docs/api-reference.md) | All public modules, types, functions |

## Package Structure

| Package | Description |
|---|---|
| `Orleans.FSharp` | Core: the functional grain runtime (`grainContract`/`grainFor`/`journaledGrainFor`), observers, streaming, logging, serialization — plus the deprecated `grain { }` CE |
| `Orleans.FSharp.Runtime` | Silo hosting, client config, grain discovery |
| `Orleans.FSharp.Abstractions` | The fixed functional transport: envelopes, protocol tokens, precompiled Orleans proxies (arrives transitively) |
| `Orleans.FSharp.EventSourcing` | The classic `eventSourcedGrain { }` model (functional `journaledGrainFor` lives in core) |
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

Contributions are welcome! Please open an issue or pull request on [GitHub](https://github.com/Neftedollar/orleans-fsharp).

## License

This project is licensed under the [MIT License](LICENSE).
