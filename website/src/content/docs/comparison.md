---
title: "Orleans.FSharp vs Alternatives — F# Actor Frameworks Compared"
description: Comparison of Orleans.FSharp, raw C# Microsoft Orleans, Akkling (Akka.NET), and Proto.Actor for F# distributed systems
---

**Choosing an actor framework for F# distributed systems?** This page compares Orleans.FSharp with the main alternatives: using Microsoft Orleans directly from C#/F#, Akkling (F# API for Akka.NET), and Proto.Actor.

## Quick comparison

| | Orleans.FSharp | C# Orleans (from F#) | Akkling (Akka.NET) | Proto.Actor |
|---|---|---|---|---|
| **Actor model** | Virtual actors | Virtual actors | Classic actors | Virtual + classic |
| **F# API** | Native CEs (`grain {}`, `siloConfig {}`) | Manual interop (class inheritance) | Native CEs (`actorOf`, `spawnAnonymous`) | None (C# API) |
| **State persistence** | Automatic (CE keyword) | Automatic (attribute) | Manual | Manual |
| **Type safety** | DU state machines, compile-time checks | Runtime errors | Typed messages | Runtime errors |
| **Clustering** | Built-in (Redis, Azure, Kubernetes) | Built-in | Akka.Cluster | Built-in |
| **.NET version** | .NET 10 | .NET 10 | .NET 6+ | .NET 6+ |
| **Testing** | GrainArbitrary + FsCheck | Manual mocking | TestKit | Manual mocking |
| **Backed by** | Community (MIT) | Microsoft | Community | Community |
| **Maintenance** | Active | Active | Maintenance mode | Active |

## Orleans.FSharp vs C# Microsoft Orleans (used from F#)

You can use Microsoft Orleans directly from F# — but you end up writing C#-style code in F# syntax: class inheritance, mutable state, imperative patterns. Orleans.FSharp eliminates this friction entirely.

### What changes

| Aspect | C# Orleans from F# | Orleans.FSharp |
|--------|-------------------|---------------|
| Grain definition | `type MyGrain() = inherit Grain<MyState>()` | `grain { defaultState Zero; handle fn; persist "Default" }` |
| State transitions | Mutable `this.State` property | Pure functions returning new state |
| Configuration | `builder.UseOrleans(fun siloBuilder -> ...)` | `siloConfig { useLocalhostClustering; addMemoryStorage "Default" }` |
| Serialization | Manual `[<GenerateSerializer>]` on classes | Same attribute, but on DUs — the natural F# choice |
| Testing | Write C#-style mocks | `GrainArbitrary.forCommands<'Cmd>()` + FsCheck |

### Code comparison

**C# Orleans from F# (class inheritance):**

```fsharp
type CounterGrain() =
    inherit Grain<int>()

    override this.OnActivateAsync(ct) =
        this.State <- 0
        Task.CompletedTask

    member this.Increment() =
        this.State <- this.State + 1
        this.WriteStateAsync()

    member this.GetValue() =
        Task.FromResult(this.State)
```

**Orleans.FSharp (computation expression):**

```fsharp
let counter = grain {
    defaultState 0
    handle (fun state cmd ->
        task {
            match cmd with
            | Increment -> return state + 1, box(state + 1)
            | GetValue -> return state, box state
        })
    persist "Default"
}
```

The Orleans.FSharp version is shorter, immutable, and the F# compiler checks exhaustive pattern matching on commands.

## Orleans.FSharp vs Akkling (Akka.NET)

Akkling provides an idiomatic F# API for Akka.NET — a port of the JVM Akka actor framework. The fundamental difference is the actor model: Microsoft Orleans uses **virtual actors** (always addressable, auto-activated), while Akka.NET uses **classic actors** (explicit lifecycle management).

### Key differences

| Aspect | Orleans.FSharp | Akkling (Akka.NET) |
|--------|---------------|-------------------|
| Actor lifecycle | Virtual — always exists, activated on demand | Explicit — must spawn, supervise, and restart |
| State persistence | `persist "ProviderName"` keyword | Manual `Akka.Persistence` integration |
| Failure handling | Automatic reactivation on another silo | Supervision trees (manual configuration) |
| Location transparency | Built-in grain directory | Akka.Cluster + shard regions |
| Stream processing | `Stream.getRef` + `Stream.publish` | Akka.Streams |
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
| State persistence | `persist "ProviderName"` keyword | Manual provider integration |
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
| F# computation expressions | Yes (85 keywords) | No | Yes | No |
| DU state machines | Yes | No | Partial | No |
| Property-based testing | GrainArbitrary | No | No | No |
| Grain timers | `onTimer` keyword | `RegisterTimer` | Scheduler | Manual |
| Grain reminders | `onReminder` keyword | `IRemindable` | N/A | N/A |
| Event sourcing | `eventSourcedGrain {}` | `JournaledGrain` | `Akka.Persistence` | Manual |
| Transactions | `TransactionalState` wrapper | `TransactionalState` | Saga pattern | Manual |
| Streaming | `Stream` module | `IAsyncStream` | Akka.Streams | N/A |
| TLS/mTLS | `useTls` keyword | Manual config | Akka.Remote TLS | gRPC TLS |
| Kubernetes | `useKubernetesClustering` | `Kubernetes` package | Akka.Discovery | Kubernetes provider |
| Dashboard | `enableDashboard` keyword | OrleansDashboard | Petabridge.Cmd | N/A |
| Health checks | `enableHealthChecks` keyword | Manual registration | N/A | gRPC health |
| OpenTelemetry | `enableOpenTelemetry` keyword | Manual registration | Phobos | Manual |

## Performance

Orleans.FSharp adds no measurable overhead to Microsoft Orleans. The computation expressions are evaluated at build time by the code generator — at runtime, the generated code is identical to hand-written C# Orleans grains.

- **Grain call overhead**: ~8 nanoseconds (CE evaluation, not grain call latency)
- **Network latency**: Dominates all real-world scenarios (microseconds to milliseconds)
- **Memory**: Same as C# Orleans (no additional allocations per grain)

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
- [Grain Definition](/orleans-fsharp/guides/grain-definition/) -- complete `grain {}` CE reference
