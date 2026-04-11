# Orleans.FSharp

> **Write Orleans grains in idiomatic F# — computation expressions, not boilerplate.**

[![CI](https://github.com/Neftedollar/orleans-fsharp/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/orleans-fsharp/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Orleans 10](https://img.shields.io/badge/Orleans-10.0.1-blue)](https://learn.microsoft.com/dotnet/orleans/)
[![F#](https://img.shields.io/badge/F%23-9%2B-378BBA)](https://fsharp.org/)
[![Tests](https://img.shields.io/badge/tests-1542-brightgreen)]()
[![NuGet](https://img.shields.io/nuget/v/Orleans.FSharp.svg)](https://www.nuget.org/packages/Orleans.FSharp)

---

## The Problem

Using Orleans from C# works great. Using it from F# means fighting C# idioms:

```fsharp
// ❌ C# Orleans style in F# — verbose, mutable, attribute-heavy
[<GenerateSerializer>]
type MyGrain() =
    inherit Grain()
    member val _state = 0 with get, set
    member this.Increment() =
        this._state <- this._state + 1
        Task.FromResult(this._state)
```

Orleans.FSharp replaces all of that with computation expressions, pure functions, and discriminated unions — **while the full Orleans runtime does the heavy lifting underneath**.

```fsharp
// ✅ Orleans.FSharp — pure, declarative, zero boilerplate
type State = { Count: int }
type Msg = Increment | GetCount

let counter = grain {
    defaultState { Count = 0 }
    handle (fun state msg -> task {
        match msg with
        | Increment -> return { Count = state.Count + 1 }, box (state.Count + 1)
        | GetCount  -> return state, box state.Count
    })
    persist "Default"
}
```

**1,542 tests. Zero C# grain stubs. One NuGet package.**

---

## Why It Matters

| | Traditional Orleans (C#) | Orleans.FSharp |
|---|---|---|
| **State** | Mutable class | Immutable record or DU |
| **Handler** | Imperative method | Pure function `state → msg → new state` |
| **Config** | Extension method chains | `siloConfig { }` CE |
| **Client** | `IGrainFactory.GetGrain<IFooGrain>(key)` | `FSharpGrain.ref<State, Msg> factory key` |
| **Serialization** | `[<GenerateSerializer>]` + `[<Id>]` on everything | Optional — `FSharpBinaryCodec` handles F# types automatically |
| **Test** | Mock the Orleans runtime | `TestHarness`, `MockGrainFactory`, FsCheck properties |

---

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                     Your F# Code                     │
│  ┌──────────┐  ┌─────────────┐  ┌─────────────────┐ │
│  │ grain { }│  │siloConfig {}│  │ FSharpGrain.ref │ │
│  │  CE      │  │  CE         │  │  typed handles  │ │
│  └────┬─────┘  └──────┬──────┘  └────────┬────────┘ │
│       │               │                   │          │
│  ┌────▼───────────────▼───────────────────▼────────┐ │
│  │           Orleans.FSharp Runtime                │ │
│  │  • Universal grain handler dispatch             │ │
│  │  • FSharpBinaryCodec (no attributes needed)     │ │
│  │  • Streaming, Reminders, Timers, Observers      │ │
│  │  • Transaction, Resilience, Filters              │ │
│  └───────────────────────┬─────────────────────────┘ │
│                          │                           │
│  ┌───────────────────────▼─────────────────────────┐ │
│  │         Orleans.FSharp.Abstractions              │ │
│  │     IFSharpGrain / IFSharpEventSourcedGrain      │ │
│  │          (C# shim for Orleans proxy gen)         │ │
│  └───────────────────────┬─────────────────────────┘ │
└──────────────────────────┼───────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────┐
│                Microsoft Orleans Runtime              │
│  Placement • Activation • Reminders • Transactions   │
│  Streaming • Storage • Serialization • Clustering     │
└──────────────────────────────────────────────────────┘
```

---

## Quick Start

### 1. Create a project

```bash
dotnet new console -n MyOrleansApp -lang F#
cd MyOrleansApp
dotnet add package Orleans.FSharp
dotnet add package Orleans.FSharp.Runtime
dotnet add package Orleans.FSharp.Abstractions
```

### 2. Define a grain

```fsharp
open Orleans.FSharp

// Plain F# types — no attributes, no codegen
type CounterState = { Count: int }
type CounterCommand = Increment | Decrement | GetCount

let counter = grain {
    defaultState { Count = 0 }
    handle (fun state cmd -> task {
        match cmd with
        | Increment -> return { Count = state.Count + 1 }, box (state.Count + 1)
        | Decrement -> return { Count = state.Count - 1 }, box (state.Count - 1)
        | GetCount  -> return state, box state.Count
    })
    persist "Default"
}
```

### 3. Configure and run the silo

```fsharp
open Microsoft.Extensions.Hosting
open Orleans.FSharp.Runtime

let host =
    Host.CreateDefaultBuilder()
        .ConfigureSilo(siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
        })
        .ConfigureServices(fun services ->
            services.AddFSharpGrain<CounterState, CounterCommand>(counter) |> ignore
        )
        .Build()

host.Run()
```

### 4. Call it from a client

```fsharp
open Orleans.FSharp

let grainRef = FSharpGrain.ref<CounterState, CounterCommand> clientFactory "my-counter"
let! newCount = grainRef |> FSharpGrain.ask GetCount   // → 1
```

> 📖 **Full getting-started guide**: [docs/getting-started.md](docs/getting-started.md)

---

## Features at a Glance

### `grain { }` — Declarative Grain Definition

```fsharp
let bank = grain {
    defaultState { Balance = 0m }

    // Multiple handler styles
    handle (fun state msg -> ...)                          // basic
    handleTyped (fun state msg -> task { return state, result })  // typed result
    handleWithContext (fun ctx state msg -> ...)           // with GrainContext
    handleCancellable (fun state msg ct -> ...)            // with cancellation

    // Lifecycle hooks
    onActivate (fun state -> task { return state })
    onDeactivate (fun state -> task { ... })

    // Reminders & timers
    onReminder "daily-report" (fun state name status -> task { return state })
    timer "heartbeat" (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 5.0)
          (fun state -> task { return state })

    // Persistence
    persist "Default"
    additionalState "audit" "Default" { EventCount = 0 }

    // Concurrency
    reentrant
    statelessWorker
    placement PreferLocalPlacement

    // Streaming
    implicitSubscription "orders" (fun state evt -> task { return state })
}
```

### `eventSourcedGrain { }` — Event Sourcing

```fsharp
type OrderEvent = OrderPlaced of string | OrderShipped
type OrderCommand = Place of string | Ship

let orderGrain = eventSourcedGrain {
    defaultState { Items = []; Shipped = false }
    apply (fun state event -> match event with
        | OrderPlaced items -> { state with Items = items }
        | OrderShipped      -> { state with Shipped = true })
    handle (fun state cmd -> match cmd with
        | Place items -> [ OrderPlaced items ]
        | Ship        -> if not state.Shipped then [ OrderShipped ] else [])
    logConsistencyProvider "LogStorage"
}
```

### `siloConfig { }` — Clean Infrastructure Setup

```fsharp
let config = siloConfig {
    useLocalhostClustering       // or addRedisClustering, addAzureTableClustering...
    clusterId "my-cluster"
    serviceId "my-service"

    addMemoryStorage "Default"   // or addRedisStorage, addPostgresStorage...
    addMemoryStreams "StreamProvider"
    useInMemoryReminderService

    addIncomingFilter  (fun ctx -> ...)
    addOutgoingFilter (fun ctx -> ...)

    enableHealthChecks
    addDashboard
}
```

### `FSharpGrain` — Typed Client API

```fsharp
// String key
let handle = FSharpGrain.ref<State, Msg> factory "order-123"
let! state = handle |> FSharpGrain.send PlaceOrder
let! result = handle |> FSharpGrain.ask<State, Msg, int> GetItemCount
do! handle |> FSharpGrain.post CancelOrder

// GUID key
let guidHandle = FSharpGrain.refGuid<State, Msg> factory (Guid.NewGuid())

// Integer key
let intHandle = FSharpGrain.refInt<State, Msg> factory 42L
```

---

## What You Get

| Feature | How |
|---|---|
| **State management** | Immutable records, DU state machines, event sourcing |
| **Persistence** | Memory, Redis, Azure Blob/Table, Postgres, Cosmos, DynamoDB |
| **Streaming** | Memory, EventHub, Azure Queue, broadcast channels |
| **Reminders** | In-memory, Redis, durable |
| **Timers** | Declarative within `grain { }` |
| **Transactions** | Orleans Transactions with typed state |
| **Resilience** | Polly retry, circuit-breaker, timeout pipelines |
| **Filters** | Incoming/outgoing grain call filters for cross-cutting concerns |
| **Observers** | FSharpObserverManager with subscription management |
| **Versioning** | Compatibility strategies, migration framework |
| **Serialization** | FSharpBinaryCodec (automatic), JSON fallback, Orleans native |
| **Testing** | TestHarness, MockGrainFactory, FsCheck generators, log capture |
| **Analyzers** | Compile-time checks for common mistakes |
| **Scripting** | Programmatic silo start/shutdown for F# scripts |

---

## Installation

```bash
# Core — grain CE, GrainRef, streaming, serialization
dotnet add package Orleans.FSharp

# Silo hosting, client config
dotnet add package Orleans.FSharp.Runtime

# Required: C# shim for Orleans proxy generation (no code to write)
dotnet add package Orleans.FSharp.Abstractions
```

**Optional packages:**

```bash
# Event sourcing
dotnet add package Orleans.FSharp.EventSourcing

# Testing utilities
dotnet add package Orleans.FSharp.Testing

# Project templates
dotnet new install Orleans.FSharp.Templates
```

> **Why `Abstractions`?** Orleans source generators only run on C# projects.
> `Orleans.FSharp.Abstractions` is a tiny C# package that defines `IFSharpGrain` —
> just reference it and you're done. No code to write.

---

## Documentation

| Guide | What you'll learn |
|---|---|
| [Getting Started](docs/getting-started.md) | Zero to working grain in 15 minutes |
| [Grain Definition](docs/grain-definition.md) | Complete `grain { }` CE reference |
| [Silo Configuration](docs/silo-configuration.md) | Storage, clustering, streaming setup |
| [Serialization](docs/serialization.md) | Binary codec, JSON fallback, schema evolution |
| [Event Sourcing](docs/event-sourcing.md) | `eventSourcedGrain { }` with snapshots |
| [Streaming](docs/streaming.md) | Publish, subscribe, TaskSeq integration |
| [Testing](docs/testing.md) | TestHarness, FsCheck, property-based testing |
| [Resilience](docs/resilience.md) | Retry, circuit-breaker, timeout patterns |
| [Redis Example](docs/redis-example.md) | Shopping cart with Redis — end-to-end |
| [Security](docs/security.md) | TLS, mTLS, secrets management |
| [API Reference](docs/api-reference.md) | All public types and modules |

---

## Project Structure

```
src/
├── Orleans.FSharp/              # Core: grain CE, GrainRef, streaming, logging
├── Orleans.FSharp.Runtime/      # Silo hosting, client configuration
├── Orleans.FSharp.Abstractions/  # C# shim for Orleans proxy generation
├── Orleans.FSharp.EventSourcing/ # Event-sourced grain CE
├── Orleans.FSharp.Testing/      # TestHarness, GrainMock, FsCheck
├── Orleans.FSharp.Analyzers/    # Compile-time F# checks
├── Orleans.FSharp.CodeGen/      # Optional: per-grain C# stubs (legacy)
├── Orleans.FSharp.Generator/    # Source generator for event-sourced grains
└── Orleans.FSharp.Sample/       # Reference implementations
tests/
├── Orleans.FSharp.Tests/         # 1,542 unit + property tests
└── Orleans.FSharp.Integration/   # End-to-end Orleans TestCluster tests
```

---

## Community & Support

- **💬 Issues & Questions**: [Open a GitHub Issue](https://github.com/Neftedollar/orleans-fsharp/issues)
- **📖 Documentation**: [docs/](docs/) folder — every guide is tested
- **🐛 Bug Reports**: Include reproduction steps — we fix fast
- **💡 Feature Requests**: We track community-voted priorities publicly
- **⭐ Star this repo** — it's free and it helps others find the project

---

## Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

- **Docs**: Every guide has an edit link at the bottom — fixes are the most valuable contributions
- **Samples**: Real-world grain definitions are better than abstract examples
- **Tests**: More integration coverage, more FsCheck properties
- **Benchmarks**: Performance regressions need catching

---

## License

[MIT](LICENSE) — use it in personal and commercial projects.
