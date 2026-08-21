---
title: "Orleans.FSharp vs Alternatives — F# Actor Frameworks Compared"
description: Comparison of Orleans.FSharp, raw C# Microsoft Orleans, Akkling (Akka.NET), and Proto.Actor for F# distributed systems
---

**Choosing an actor framework for F# distributed systems?** This page compares Orleans.FSharp with the main alternatives: using Microsoft Orleans directly from C#/F#, Akkling (F# API for Akka.NET), and Proto.Actor.

## Quick comparison

| | Orleans.FSharp | C# Orleans (from F#) | Akkling (Akka.NET) | Proto.Actor |
|---|---|---|---|---|
| **Actor model** | Virtual actors | Virtual actors | Classic actors | Virtual + classic |
| **F# API** | Functional runtime (`grainContract`/`grainFor`, current) + native CEs (`siloConfig {}`; `grain {}` deprecated) | Manual interop (class inheritance) | Native CEs (`actorOf`, `spawnAnonymous`) | None (C# API) |
| **State persistence** | Typed facets (`usePersistentState`) | Automatic (attribute) | Manual | Manual |
| **Type safety** | Compile-time checked API records, DU state | Runtime errors | Typed messages | Runtime errors |
| **Clustering** | Built-in (Redis, Azure, Kubernetes) | Built-in | Akka.Cluster | Built-in |
| **.NET version** | .NET 10 | .NET 10 | .NET 6+ | .NET 6+ |
| **Testing** | GrainArbitrary + FsCheck | Manual mocking | TestKit | Manual mocking |
| **Backed by** | Community (MIT) | Microsoft | Community | Community |
| **Maintenance** | Active | Active | Maintenance mode | Active |

## Orleans.FSharp vs C# Microsoft Orleans (used from F#)

You can use Microsoft Orleans directly from F# — but you end up writing C#-style code in F# syntax: class inheritance, mutable state, imperative patterns. Orleans.FSharp replaces that with immutable state, pattern matching, and computation expressions instead.

### What changes

| Aspect | C# Orleans from F# | Orleans.FSharp |
|--------|-------------------|---------------|
| Grain definition | Hand-written interface + `inherit Grain()` class | `contract<string, CounterApi> { ... }` + `grainFor` |
| State transitions | Mutable fields / `this.State` | Pure handlers returning `newState, reply` |
| Client proxies | C# source generator (needs a C# shim project) | Precompiled in the package — nothing to generate |
| Configuration | `builder.UseOrleans(fun siloBuilder -> ...)` | `siloConfig { useLocalhostClustering; addMemoryStorage "Default" }` |
| Serialization | Manual `[<GenerateSerializer>]` on classes | Same attribute, but on DUs — the natural F# choice |
| Testing | Write C#-style mocks | `GrainArbitrary.forCommands<'Cmd>()` + FsCheck |

### Code comparison

**C# Orleans from F# (class inheritance):**

```fsharp
type ICounterGrain =
    inherit IGrainWithStringKey
    abstract Increment: unit -> Task<int>
    abstract Value: unit -> Task<int>

// ...plus a C# shim project in the solution, because Orleans'
// proxy source generator does not run on F# projects.
type CounterGrain() =
    inherit Grain()
    let mutable count = 0

    interface ICounterGrain with
        member _.Increment() =
            count <- count + 1
            Task.FromResult count

        member _.Value() = Task.FromResult count
```

**Orleans.FSharp (functional grain runtime):**

```fsharp
type CounterApi =
    { increment: unit -> Task<int>
      value: unit -> Task<int> }

let counterContract =
    contract<string, CounterApi> {
        grainType "counter"
        version 1
        stringKey
        readOnly (_.value)
    }

let counter =
    grainFor counterContract {
        defaultState (fun () -> 0)
        handle      (_.increment) (fun _ctx n () -> task { return n + 1, n + 1 })
        handleQuery (_.value)     (fun _ctx n () -> task { return n })
    }
```

Same two operations on both sides. The functional version is immutable, the compiler checks every
handler against `CounterApi`'s field types, and sealing the definition verifies each operation has
exactly one handler — with no proxy-generation step anywhere.

## Orleans.FSharp vs Akkling (Akka.NET)

Akkling provides an idiomatic F# API for Akka.NET — a port of the JVM Akka actor framework. The fundamental difference is the actor model: Microsoft Orleans uses **virtual actors** (always addressable, auto-activated), while Akka.NET uses **classic actors** (explicit lifecycle management).

### Key differences

| Aspect | Orleans.FSharp | Akkling (Akka.NET) |
|--------|---------------|-------------------|
| Actor lifecycle | Virtual — always exists, activated on demand | Explicit — must spawn, supervise, and restart |
| State persistence | `usePersistentState` facets | Manual `Akka.Persistence` integration |
| Failure handling | Automatic reactivation on another silo | Supervision trees (manual configuration) |
| Location transparency | Built-in grain directory | Akka.Cluster + shard regions |
| Stream processing | `Stream.getStream` + `Stream.publish` | Akka.Streams |
| Concurrency model | Single-threaded turns (with optional reentrancy) | Mailbox processing |

### When to choose Akkling

- You need fine-grained actor supervision hierarchies
- Your team already has Akka/Akka.NET experience
- You want the Akka.Streams API for complex stream processing

### When to choose Orleans.FSharp

- You want virtual actors — no lifecycle management overhead
- You need automatic state persistence without boilerplate
- You want property-based testing with auto-generated command sequences
- You are targeting .NET 10
- You want built-in Kubernetes clustering support

## Orleans.FSharp vs Proto.Actor

Proto.Actor is a cross-platform actor framework supporting both virtual and classic actor models. It does not have an F# API — you use the C# API directly.

### Key differences

| Aspect | Orleans.FSharp | Proto.Actor |
|--------|---------------|-------------|
| F# API | Native computation expressions | C# API only |
| Virtual actors | Yes (Microsoft Orleans) | Yes (Proto.Cluster) |
| Serialization | F# DUs with `[<GenerateSerializer>]` | Protobuf (code generation) |
| State persistence | `usePersistentState` facets | Manual provider integration |
| Ecosystem | Microsoft Orleans ecosystem (Azure, Dashboard) | Standalone (gRPC-based) |
| Testing | GrainArbitrary + FsCheck | Manual |

### When to choose Proto.Actor

- You need cross-language support (Go, C#, Kotlin, Python)
- You want gRPC as the transport layer
- Your system is polyglot

### When to choose Orleans.FSharp

- You are building a pure F#/.NET distributed system
- You want idiomatic F# with computation expressions
- You need the Microsoft Orleans ecosystem (Azure integration, Dashboard, extensive providers)

## Feature matrix

| Feature | Orleans.FSharp | C# Orleans | Akkling | Proto.Actor |
|---------|---------------|-----------|---------|------------|
| F# computation expressions | Yes (137 operations across 8 builders) | No | Yes | No |
| DU state machines | Yes | No | Partial | No |
| Property-based testing | GrainArbitrary | No | No | No |
| Grain timers | `onTimer` keyword | `RegisterTimer` | Scheduler | Manual |
| Grain reminders | `onReminder` keyword | `IRemindable` | N/A | N/A |
| Event sourcing | `journaledGrainFor { }` | `JournaledGrain` | `Akka.Persistence` | Manual |
| Transactions | `transactional` + `transactionalStateFrom` | `[Transaction]` + `TransactionalState` | Saga pattern | Manual |
| Streaming | `Stream` module, `onStream` / `onBroadcast` | `IAsyncStream` | Akka.Streams | N/A |
| TLS/mTLS | `useTls` keyword | Manual config | Akka.Remote TLS | gRPC TLS |
| Kubernetes | `useKubernetesClustering` | `Kubernetes` package | Akka.Discovery | Kubernetes provider |
| Dashboard | `addDashboard` keyword | OrleansDashboard | Petabridge.Cmd | N/A |
| Health checks | `enableHealthChecks` keyword | Manual registration | N/A | gRPC health |
| OpenTelemetry | Orleans' own activity sources and meter | Manual registration | Phobos | Manual |

## Performance

Orleans.FSharp runs on the Orleans runtime unchanged; it adds a dispatch layer, not a second
transport.

- **Where the work happens**: a contract and a definition are sealed once, when the module that
  declares them initialises — not per call. An API shape is built once per record type and cached
  process-wide, and each operation's argument and reply closures are precomputed at that point.
- **Per call**: one dictionary lookup and one preclosed delegate call on top of the Orleans call
  itself. The repository's own dispatch benchmark holds that below 5% of calling the handler
  function directly, over 1,000,000 iterations.
- **Network latency**: dominates all real-world scenarios (microseconds to milliseconds).
- **C# facade callers** additionally pay `DispatchProxy`'s per-call boxing — see
  [Calling from C#](/orleans-fsharp/calling-from-csharp/).

## Recommendation summary

| Use case | Recommended |
|----------|------------|
| New F# distributed system | **Orleans.FSharp** |
| Existing C# Orleans codebase, adding F# | **Orleans.FSharp** (interop is seamless) |
| Existing Akka.NET codebase | **Akkling** (unless migrating to Orleans) |
| Polyglot system (Go + C# + Python) | **Proto.Actor** |
| Learning actor model with F# | **Orleans.FSharp** (simplest mental model) |

## Next steps

- [Getting Started](/orleans-fsharp/getting-started/) -- zero to working grain in 15 minutes
- [How To](/orleans-fsharp/how-to/) -- step-by-step distributed system tutorial
- [FAQ](/orleans-fsharp/faq/) -- common questions about Orleans.FSharp
- [Grain Definition](/orleans-fsharp/grain-definition/) -- complete `grain {}` CE reference
