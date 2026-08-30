# Orleans.FSharp

<p align="center">
  <img src="https://raw.githubusercontent.com/Neftedollar/orleans-fsharp/main/website/public/orleans-fsharp-logo.svg" alt="Orleans.FSharp logo" width="156" height="156" />
</p>

**Idiomatic F# for Microsoft Orleans -- computation expressions, not boilerplate**

[![CI](https://github.com/Neftedollar/orleans-fsharp/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/orleans-fsharp/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Orleans 10](https://img.shields.io/badge/Orleans-10.1.0%20%E2%80%93%2010.3.1-blue)](https://learn.microsoft.com/dotnet/orleans/)
[![F#](https://img.shields.io/badge/F%23-9%2B-378BBA)](https://fsharp.org/)
[![Tests](https://img.shields.io/badge/tests-CI%20verified-brightgreen)]()
[![NuGet](https://img.shields.io/nuget/v/Orleans.FSharp.svg)](https://www.nuget.org/packages/Orleans.FSharp)

---

## Why this exists

Orleans is a powerful virtual actor framework, but using it from F# can pull application code toward C# idioms: mutable state bags, attribute-heavy classes, and interface-plus-codegen ceremony. Orleans.FSharp adds a functional authoring path: a grain's public surface is a plain F# record of functions, behavior uses explicit state transitions, and silos are configured with computation expressions. Orleans still provides clustering, activation, storage, streams, placement, and transactions underneath.

## Quick Start

The grain's public surface is a plain F# record of functions — no interface, no attributes,
no code generation:

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

// 1. The API record IS the grain's surface — and its type identity
type CounterApi =
    { increment: unit -> Task<int>
      value: unit -> Task<int> }

// 2. The contract: stable wire identity — grain type, version, key encoding
let counterContract =
    contract<string, CounterApi> {
        grainType "counter"
        version 1
        stringKey
        readOnly (_.value)
    }

// 3. The definition: pure state -> reply handlers; storage is written only when you say so
let counter =
    grainFor counterContract {
        defaultState (fun () -> 0)
        handle      (_.increment) (fun _ctx n () -> task { return n + 1, n + 1 })
        handleQuery (_.value)     (fun _ctx n () -> task { return n })
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
let api = FunctionalGrain.ref counterContract factory "my-counter"
let! n = api.increment ()
```

Need two grain types over one API record, or a record type you can swap without moving the
grain's identity? Declare a dedicated brand type and use the full form,
`grainContract<'Actor, 'Key, 'Api>` — see
[the actor brand](docs/functional-grains.md#why-the-actor-brand).

### Typed grain ids

The key type is your domain's, not Orleans'. A unit of measure plus a mapped key codec makes the
grain key an `int64<UserId>` all the way to the call site, so handing it an order id is a compile
error and costs nothing at run time:

```fsharp
[<Measure>] type UserId

let userId (raw: int64) : int64<UserId> = raw * 1L<UserId>
let rawId (id: int64<UserId>) : int64 = int64 id

let userContract =
    contract<int64<UserId>, UserApi> {
        grainType "user"
        int64KeyMapped rawId userId
    }

// int64<UserId> is what this reference takes; 42L<OrderId> is error FS0001, not a 3am incident
let user = FunctionalGrain.ref userContract factory (userId 42L)
```

The same holds for any domain key type — `stringKeyMapped` / `guidKeyMapped` and the compound
forms. See [`examples/typesafe-ids`](examples/typesafe-ids) for the full example and
[key-codec identity rules](docs/functional-grains.md#key-codec-identity-rules) for what a codec has
to guarantee.

> **Older authoring models.** The original `grain { }` CE (shown further below) still compiles
> and runs, but its public surface (`grain { }`, `GrainDefinition`, the old `GrainContext`,
> `[<FSharpGrain>]`, `AddFSharpGrain`, `FSharpGrain.*`, `Timers`, `Reminder`) carries
> `[<Obsolete>]` -- a **warning, not an error** -- and the classic `eventSourcedGrain { }` is
> superseded by the functional `journaledGrainFor`. See
> [Functional Grain Runtime](docs/functional-grains.md) for the before/after mapping of every
> deprecated entry point. `siloConfig { }` and `clientConfig { }` are current and unaffected.

## Feature Showcase

### Functional grain runtime — the current authoring model

The supported functional surface is exposed as contract or definition operations — no attributes,
no codegen:

| Where | Operations |
|---|---|
| `contract<'Key, 'Api> { }` / `grainContract<'Actor, 'Key, 'Api> { }` | `grainType` (optional for ephemeral grains), `version`, key codecs (`stringKey` / `guidKey` / `int64Key`, compound + mapped forms), per-operation `readOnly` / `oneWay` / `alwaysInterleave` / `operationId` / `sinceVersion` / `transactional`, whole-grain `reentrant` / `mayInterleave`, `acceptsVersions` |
| `grainFor { }` | `defaultState` / `initialState` / `stateFrom`, per-element codecs and schema upcasters, `usePersistentState`, `transactionalStateFrom`, `onActivate` / `onDeactivate` / `onLifecycle`, `onTimer` / `onReminder`, `onStream` / `onBroadcast` (implicit subscriptions), all six stock placement strategies, `migrationParticipant`, `collectionAge`, `handle` / `handleQuery` / `handleStream` |
| `journaledGrainFor { }` | event sourcing over Orleans' log-consistency providers: `initialEventState`, pure `apply`, independent `stateSchema` / `eventSchema` upcasters, event-returning handlers, activation migration participants, and typed CustomStorage snapshots — see [Event Sourcing](docs/event-sourcing.md) |
| API field shapes | `'Arg -> Task<'Reply>` and `'Arg -> IAsyncEnumerable<'Item>` ([streaming replies](docs/streaming-replies.md)) |
| From C# | a typed facade over any contract: awaited calls and `await foreach` — [Calling from C#](docs/calling-from-csharp.md) |

Contract versions are native Orleans interface versions, so Orleans routing participates in mixed
N/N+1 deployments. The repository tests separate versioned silo executables and rollback. Durable
state/events use independent schema envelopes and typed upcasters; transport compatibility does
not imply storage compatibility.

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
> `grainType`, `collectionAge`, `statelessWorker`, `placement`, and
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
| `useFSharpBinarySerialization` | F# binary codec for F# types |
| `useFSharpJsonSerialization` | F# JSON as the primary generalized codec |
| `useFSharpSerialization` | Explicit codec policy, including binary then JSON for unsupported types |
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

The repository template already uses the current functional API. The published template package
4.1.0 still scaffolds the Legacy model, so install the current template from a source checkout
until the next template release:

```bash
git clone https://github.com/Neftedollar/orleans-fsharp.git
dotnet new install ./orleans-fsharp/templates
dotnet new orleans-fsharp -n MyApp
```

## Upgrading to 4.0

4.0 is the **functional-era major**. The functional grain
runtime (`grainContract` / `grainFor` / `journaledGrainFor`) is the recommended authoring model,
with first-class operations for its supported Orleans surface — transactions, event sourcing over Orleans'
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
| [Recipes](docs/how-to.md) | Task-oriented paths for persistence, streaming, upgrades, and hosting |
| [Examples](docs/examples.md) | Runnable projects mapped to the features they prove |
| [Functional Grain Runtime](docs/functional-runtime.md) | Short path through contracts, state, delivery, placement, and transactions |
| [Functional Runtime Reference](docs/functional-grains.md) | Exhaustive builder operations, invariants, and edge cases |
| [Silo Configuration](docs/silo-configuration.md) | Complete `siloConfig { }` CE reference |
| [Client Configuration](docs/client-configuration.md) | `clientConfig { }` CE reference |
| [Serialization](docs/serialization.md) | Transport, native interop, durable formats, and schema evolution |
| [Streaming](docs/streaming.md) | Publish, subscribe, TaskSeq, broadcast |
| [Event Sourcing](docs/event-sourcing.md) | `journaledGrainFor { }` — state as the fold of an event journal |
| [Server-Streaming Replies](docs/streaming-replies.md) | `'Arg -> IAsyncEnumerable<'Item>` — items delivered as they are produced, over Orleans' async-enumerable grain extension |
| [Testing](docs/testing.md) | Pure handlers, TestingHost, FsCheck, rolling updates, and durable fixtures |
| [Analyzers](docs/analyzers.md) | OF0001: async {} detection, AllowAsync opt-out |
| [Security](docs/security.md) | TLS, mTLS, filters, secrets |
| [Additional APIs](docs/advanced.md) | OpenTelemetry, shutdown, scripting, Kubernetes, and shared utilities |
| [Resilience](docs/resilience.md) | Polly v8 retry, circuit-breaker, and timeout patterns |
| [Calling from C#](docs/calling-from-csharp.md) | Bind a hand-written C# interface to a functional grain contract |
| [Orleans Compatibility](docs/compatibility.md) | Tested Orleans range and newly released capabilities |
| [API Reference](docs/api-reference.md) | All public modules, types, functions |

Documentation for the original authoring model is isolated under
[Legacy API](docs/legacy/index.md), including its migration guide and examples.

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
