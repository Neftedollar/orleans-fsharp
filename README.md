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

> **Release status.** The published stable line is **4.1** (latest: `4.1.0`). The `main` branch
> and this documentation track the **5.0 preview**, the next major, and can describe APIs or
> behavior not present in 4.1. See [Release and production status](docs/release-status.md) before
> choosing packages or deploying from source.

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

## Feature Showcase

### Functional grain runtime — the current authoring model

The supported functional surface is exposed as contract or definition operations — no attributes,
no codegen:

| Where | Operations |
|---|---|
| `contract<'Key, 'Api> { }` / `grainContract<'Actor, 'Key, 'Api> { }` | `grainType` (optional for ephemeral grains), `version`, key codecs (`stringKey` / `guidKey` / `int64Key`, compound + mapped forms), per-operation `readOnly` / `oneWay` / `alwaysInterleave` / `operationId` / `sinceVersion` / `transactional`, whole-grain `reentrant` / `mayInterleave`, `acceptsVersions` |
| `grainFor { }` | `defaultState` / `initialState` / `stateFrom`, per-element codecs and schema upcasters, `usePersistentState`, `transactionalStateFrom`, `onActivate` / `onDeactivate` / `onLifecycle`, `onTimer` / `onReminder`, `onStream` / `onBroadcast` (implicit subscriptions), `statelessWorker`, all six stock placement strategies, `migrationParticipant`, `collectionAge`, `handle` / `handleQuery` / `handleStream` |
| `journaledGrainFor { }` | event sourcing over Orleans' log-consistency providers: `initialEventState`, pure `apply`, independent `stateSchema` / `eventSchema` upcasters, event-returning handlers, activation migration participants, and typed CustomStorage snapshots — see [Event Sourcing](docs/event-sourcing.md) |
| API field shapes | `'Arg -> Task<'Reply>` and `'Arg -> IAsyncEnumerable<'Item>` ([streaming replies](docs/streaming-replies.md)) |
| From C# | a typed facade over any contract: awaited calls and `await foreach` — [Calling from C#](docs/calling-from-csharp.md) |

Contract versions are native Orleans interface versions, so Orleans routing participates in mixed
N/N+1 deployments. The repository tests separate versioned silo executables and rollback. Durable
state/events use independent schema envelopes and typed upcasters; transport compatibility does
not imply storage compatibility.

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
| `enableHealthChecks` | Register tagged Orleans silo liveness/readiness checks (`orleans` + `live`/`ready`); map HTTP endpoints separately |
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

## Installation

For the published stable line:

```bash
dotnet add package Orleans.FSharp --version 4.1.0
dotnet add package Orleans.FSharp.Runtime --version 4.1.0
```

The 5.0 preview currently exists on `main`; it is not a published stable package. That is the
whole functional-runtime setup: `Orleans.FSharp.Abstractions` (the fixed transport —
request envelopes, protocol tokens, and Orleans proxies precompiled once inside the package) comes
in transitively, and there is nothing to generate in your projects.

Optional packages:

```bash
dotnet add package Orleans.FSharp.Testing --version 4.1.0  # Test harness + FsCheck
```

## Project Template

The repository template tracks the 5.0 preview and uses the current functional API. The published
4.1.0 template is from the archived authoring model and is not a supported starting point. To try
the preview template, install it from a source checkout:

```bash
git clone https://github.com/Neftedollar/orleans-fsharp.git
dotnet new install ./orleans-fsharp/templates
dotnet new orleans-fsharp -n MyApp
```

## Documentation

| Guide | Description |
|---|---|
| [Getting Started](docs/getting-started.md) | Zero to working grain in 15 minutes |
| [Release and production status](docs/release-status.md) | Stable 4.1 versus main/5.0 preview, plus verified limitations |
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

The original authoring models are isolated in the [Legacy archive](docs/legacy/index.md). That
archive is unsupported and receives no new Legacy release line, features, compatibility work, or
security fixes; use it only to migrate an existing application.

## Package Structure

| Package | Description |
|---|---|
| `Orleans.FSharp` | Core functional grain runtime, observers, streaming, logging, and serialization |
| `Orleans.FSharp.Runtime` | Silo hosting, client config, grain discovery |
| `Orleans.FSharp.Abstractions` | The fixed functional transport: envelopes, protocol tokens, precompiled Orleans proxies (arrives transitively) |
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
