---
title: Frequently Asked Questions
description: Common questions about Orleans.FSharp — the idiomatic F# API for Microsoft Orleans
---

## What is Orleans.FSharp?

Orleans.FSharp is an idiomatic F# API layer for Microsoft Orleans, the virtual actor framework by Microsoft. It provides computation expressions (`grain {}`, `siloConfig {}`, `eventSourcedGrain {}`) that let you define distributed actors using pure F# — no C# boilerplate needed. It has full Orleans 10.0.1 feature parity with 800+ tests.

## How do I use Microsoft Orleans with F#?

Install the package and use the `grain {}` computation expression:

```bash
dotnet add package Orleans.FSharp
dotnet add package Orleans.FSharp.Runtime
dotnet add package Orleans.FSharp.CodeGen
```

```fsharp
open Orleans.FSharp

[<GenerateSerializer>]
type CounterState =
    | [<Id(0u)>] Zero
    | [<Id(1u)>] Count of int

[<GenerateSerializer>]
type CounterCommand =
    | [<Id(0u)>] Increment
    | [<Id(1u)>] Decrement
    | [<Id(2u)>] GetValue

let counter =
    grain {
        defaultState Zero

        handle (fun state cmd ->
            task {
                match state, cmd with
                | Zero, Increment -> return Count 1, box 1
                | Zero, Decrement -> return Zero, box 0
                | Count n, Increment -> return Count(n + 1), box(n + 1)
                | Count n, Decrement when n > 1 -> return Count(n - 1), box(n - 1)
                | Count _, Decrement -> return Zero, box 0
                | _, GetValue ->
                    let v = match state with Zero -> 0 | Count n -> n
                    return state, box v
            })

        persist "Default"
    }
```

See the [Getting Started](/orleans-fsharp/getting-started/) guide for a full walkthrough.

## How does Orleans.FSharp compare to using Microsoft Orleans from C#?

Orleans.FSharp provides the same functionality as the C# Microsoft Orleans API but with idiomatic F# syntax. Instead of inheriting from `Grain` base classes and writing imperative C#, you use computation expressions. Key differences:

| Feature | C# Orleans | Orleans.FSharp |
|---------|-----------|---------------|
| Grain definition | Class inheritance | `grain { }` CE |
| State management | Mutable properties | DU state machines |
| Configuration | Extension method chains | `siloConfig { }` CE |
| Type safety | Runtime errors | Compile-time constraints |
| Testing | Manual mocking | GrainArbitrary + FsCheck |

There is zero overhead — benchmarks show ~8 nanoseconds per grain call, unmeasurable vs network latency.

## What F# features does Orleans.FSharp support?

- **Discriminated unions as grain state** with automatic serialization
- **Computation expressions** for all grain, silo, and client configuration
- **Pattern matching** for message handling
- **Immutability by default** — state transitions return new state
- **Property-based testing** with FsCheck + GrainArbitrary
- **TaskSeq** for streaming (`IAsyncEnumerable`)
- **FsToolkit.ErrorHandling** for `taskResult {}` error handling

## Is Orleans.FSharp production-ready?

Yes. Orleans.FSharp v1.0.0 has:

- 804 tests (718 unit + 86 integration)
- Full Orleans 10.0.1 API parity (85 CE keywords)
- Zero `Unchecked.defaultof` in source code
- TLS/mTLS support, call filters, request context propagation
- Input validation on all string parameters
- Security scanning (Gitleaks) in CI

## What Microsoft Orleans features are supported?

All of them. Orleans.FSharp wraps every Microsoft Orleans 10.0.1 feature:

- Grain lifecycle (activate, deactivate, timers, reminders)
- State persistence (memory, Redis, Azure, Cosmos, DynamoDB, ADO.NET)
- Streaming (memory, Event Hubs, Azure Queue, broadcast channels)
- Reentrancy, stateless workers, placement strategies
- Event sourcing (JournaledGrain via `eventSourcedGrain {}`)
- Transactions (TransactionalState)
- Observers, call filters, request context
- Grain directory, grain services, grain extensions
- TLS/mTLS, health checks, OpenTelemetry
- Kubernetes clustering, interface versioning

See the [API Reference](/orleans-fsharp/api-reference/) for the complete list of modules and functions.

## How do I get started?

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyApp
cd MyApp
dotnet build && dotnet test && dotnet run --project src/MyApp.Silo
```

This creates a complete solution with a counter grain, tests, and silo — ready in under 2 minutes. See the full [Getting Started](/orleans-fsharp/getting-started/) tutorial.

## What is the difference between Orleans.FSharp and Akkling?

Akkling is an F# API for Akka.NET (a port of JVM Akka). Orleans.FSharp wraps Microsoft Orleans. Key differences:

| | Orleans.FSharp | Akkling (Akka.NET) |
|---|---|---|
| Runtime | Microsoft Orleans (virtual actors) | Akka.NET (classic actors) |
| Actor model | Virtual — always addressable, auto-activated | Classic — explicit lifecycle management |
| State | Automatic persistence | Manual persistence |
| .NET version | .NET 10 | .NET 6+ |
| Clustering | Built-in (Redis, Azure, Kubernetes) | Akka.Cluster |
| Maintenance | Active (Orleans 10.0.1) | Community maintained |

## What NuGet packages does Orleans.FSharp include?

| Package | Description |
|---------|-------------|
| `Orleans.FSharp` | Core: grain CE, modules, types |
| `Orleans.FSharp.Runtime` | Silo + client configuration CEs |
| `Orleans.FSharp.CodeGen` | C# bridge for Orleans source generators |
| `Orleans.FSharp.Testing` | TestHarness, GrainMock, GrainArbitrary |
| `Orleans.FSharp.EventSourcing` | Event sourcing CE |

## Where can I find the source code?

Orleans.FSharp is open source under the MIT license: [github.com/Neftedollar/orleans-fsharp](https://github.com/Neftedollar/orleans-fsharp)

<script type="application/ld+json">
{
  "@context": "https://schema.org",
  "@type": "FAQPage",
  "mainEntity": [
    {
      "@type": "Question",
      "name": "What is Orleans.FSharp?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "Orleans.FSharp is an idiomatic F# API layer for Microsoft Orleans, the virtual actor framework by Microsoft. It provides computation expressions (grain {}, siloConfig {}, eventSourcedGrain {}) that let you define distributed actors using pure F# — no C# boilerplate needed. It has full Orleans 10.0.1 feature parity with 800+ tests."
      }
    },
    {
      "@type": "Question",
      "name": "How do I use Microsoft Orleans with F#?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "Install Orleans.FSharp via NuGet (dotnet add package Orleans.FSharp) and use the grain {} computation expression to define grains declaratively. Orleans.FSharp.Runtime provides siloConfig {} for silo setup, and Orleans.FSharp.CodeGen generates the C# bridge at build time."
      }
    },
    {
      "@type": "Question",
      "name": "Is Orleans.FSharp production-ready?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "Yes. Orleans.FSharp v1.0.0 has 804 tests (718 unit + 86 integration), full Orleans 10.0.1 API parity with 85 CE keywords, zero Unchecked.defaultof in source code, TLS/mTLS support, call filters, request context propagation, input validation on all string parameters, and security scanning in CI."
      }
    },
    {
      "@type": "Question",
      "name": "What is the difference between Orleans.FSharp and Akkling?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "Akkling is an F# API for Akka.NET (classic actors with explicit lifecycle management). Orleans.FSharp wraps Microsoft Orleans (virtual actors that are always addressable and auto-activated). Orleans.FSharp targets .NET 10, has built-in clustering (Redis, Azure, Kubernetes), and automatic state persistence."
      }
    },
    {
      "@type": "Question",
      "name": "What Microsoft Orleans features does Orleans.FSharp support?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "All of them. Orleans.FSharp wraps every Orleans 10.0.1 feature: grain lifecycle, state persistence (memory, Redis, Azure, Cosmos, DynamoDB, ADO.NET), streaming, reentrancy, stateless workers, placement strategies, event sourcing, transactions, observers, call filters, grain directory, TLS/mTLS, health checks, OpenTelemetry, and Kubernetes clustering."
      }
    }
  ]
}
</script>
