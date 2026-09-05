# Legacy Frequently Asked Questions

> **Archived and unsupported.** This material is retained only to help migrate existing
> applications. There is no new Legacy release line, feature or compatibility work, or security
> fixes. New development must use the current functional API.

> This archived FAQ describes the original Orleans.FSharp authoring surface. Current answers live in [Frequently Asked Questions](../faq.md).

> **Historical scope.** The `grain { }` CE shown on this page describes the 4.1-and-earlier package
> shape. It is not a supported 5.0 authoring model. New code should use the functional grain runtime
> (`grainContract` / `grainFor` / `FunctionalGrain.ref` / `AddFunctionalGrain`). See
> [functional-grains.md](../functional-grains.md).

## What is Orleans.FSharp?

Orleans.FSharp is an idiomatic F# API layer for Microsoft Orleans, the virtual actor framework by Microsoft. It provides the functional grain runtime (`grainContract` / `grainFor` / `journaledGrainFor`) plus the `siloConfig { }` and `clientConfig { }` hosting computation expressions, so you define distributed actors in pure F# — no C# boilerplate needed. It supports Orleans 10 and is covered by unit and integration suites.

## How do I use Microsoft Orleans with F#?

Install the package and use the `grain {}` computation expression:

```bash
dotnet add package Orleans.FSharp
dotnet add package Orleans.FSharp.Runtime
dotnet add package Orleans.FSharp.Abstractions
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

See the [Getting Started](../getting-started.md) guide for a full walkthrough.

**Functional-runtime equivalent** (the current authoring model — same increment/decrement/value domain, a typed API record instead of a boxed message):

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      decrement: unit -> Task<int>
      value: unit -> Task<int> }

[<RequireQualifiedAccess>]
module CounterApi =
    let contract =
        grainContract<CounterActor, string, CounterApi> {
            grainType "counter"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

let counterDefinition =
    grainFor CounterApi.contract {
        defaultState (fun () -> 0)

        handle (_.increment) (fun _context state () -> task { let next = state + 1 in return next, next })
        handle (_.decrement) (fun _context state () -> task { let next = max 0 (state - 1) in return next, next })
        handle (_.value) (fun _context state () -> task { return state, state })
    }
```

Register with `siloBuilder.AddFunctionalGrain(counterDefinition)`, then call it as
`let api = CounterApi.ref factory "my-counter" in api.increment ()` — no boxed reply, no separate
handle type. See [Getting Started](../getting-started.md) for the complete functional-first walkthrough.

## How does Orleans.FSharp compare to using Microsoft Orleans from C#?

Orleans.FSharp provides the same functionality as the C# Microsoft Orleans API but with idiomatic F# syntax. Instead of inheriting from `Grain` base classes and writing imperative C#, you use computation expressions. Key differences:

| Feature | C# Orleans | Orleans.FSharp |
|---------|-----------|---------------|
| Grain definition | Class inheritance | `grainContract` + `grainFor` (current); `grain { }` CE (deprecated) |
| State management | Mutable properties | Immutable state returned from handlers |
| Configuration | Extension method chains | `siloConfig { }` CE |
| Type safety | Runtime errors | Compile-time constraints, typed API records |
| Testing | Manual mocking | TestingHost + GrainArbitrary + FsCheck |

Dispatch overhead is small and paid once per call: the repository's benchmark holds it below 5% of calling the handler function directly, which is unmeasurable next to network latency.

## What F# features does Orleans.FSharp support?

- **Discriminated unions as grain state** with automatic serialization
- **Computation expressions** for all grain, silo, and client configuration
- **Pattern matching** for message handling
- **Immutability by default** — state transitions return new state
- **Property-based testing** with FsCheck + GrainArbitrary
- **TaskSeq** for streaming (`IAsyncEnumerable`)
- **FsToolkit.ErrorHandling** for `taskResult {}` error handling

## Is Orleans.FSharp production-ready?

This archived authoring model is unsupported and should not be selected for a new production
deployment. Evaluate the current functional API and its explicit
[release and production boundaries](../release-status.md) instead.

## What Microsoft Orleans features are supported?

The archived material historically covered the following areas; this list is not a current support
guarantee:

- Grain lifecycle (activate, deactivate, timers, reminders)
- State persistence (memory, Redis, Azure, Cosmos, DynamoDB, ADO.NET)
- Streaming (memory, Event Hubs, Azure Queue, broadcast channels)
- Reentrancy, stateless workers, placement strategies
- Event sourcing (`journaledGrainFor` over Orleans' log-consistency providers; the classic package is archive-only and is not published in the 5.0 package set)
- Distributed ACID transactions (`transactional` + `transactionalStateFrom`)
- Observers, call filters, request context
- Grain directory, grain services, grain extensions
- TLS/mTLS, health checks, OpenTelemetry
- Kubernetes clustering, interface versioning

See the [API Reference](../api-reference.md) for the complete list of modules and functions.

## How do I get started?

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyApp
cd MyApp
dotnet build && dotnet test && dotnet run --project src/MyApp.Silo
```

This creates a complete solution with a counter grain, tests, and silo — ready in under 2 minutes. See the full [Getting Started](../getting-started.md) tutorial.

## What is the difference between Orleans.FSharp and Akkling?

Akkling is an F# API for Akka.NET (a port of JVM Akka). Orleans.FSharp wraps Microsoft Orleans. Key differences:

| | Orleans.FSharp | Akkling (Akka.NET) |
|---|---|---|
| Runtime | Microsoft Orleans (virtual actors) | Akka.NET (classic actors) |
| Actor model | Virtual — always addressable, auto-activated | Classic — explicit lifecycle management |
| State | Automatic persistence | Manual persistence |
| .NET version | .NET 10 | .NET 6+ |
| Clustering | Built-in (Redis, Azure, Kubernetes) | Akka.Cluster |
| Maintenance | Active (Orleans 10 compatible) | Community maintained |

## What NuGet packages does Orleans.FSharp 5.0 include?

| Package | Description |
|---------|-------------|
| `Orleans.FSharp` | Core: the functional grain runtime, observers, streaming, serialization, and the deprecated `grain { }` CE |
| `Orleans.FSharp.Runtime` | Silo and client hosting: `AddFunctionalGrain`, `siloConfig { }`, `clientConfig { }` |
| `Orleans.FSharp.Abstractions` | The fixed functional transport and its precompiled Orleans proxies (arrives transitively) |
| `Orleans.FSharp.Testing` | TestHarness, GrainMock, GrainArbitrary, log capture |
| `Orleans.FSharp.Analyzers` | The OF0001 analyzer with an `[<AllowAsync>]` opt-out |
| `Orleans.FSharp.Templates` | The `dotnet new orleans-fsharp` project template |

`Orleans.FSharp.EventSourcing` and `Orleans.FSharp.CodeGen` are retained only as archived source
projects for migration and repository fixtures. They are not published in the 5.0 package set,
and CodeGen does not provide a universal generator for ordinary grain definitions. Use
`journaledGrainFor` and the functional contract/definition operations in new or migrating code.

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
        "text": "Orleans.FSharp is an idiomatic F# API layer for Microsoft Orleans. Current development uses the functional runtime (grainContract / grainFor / AddFunctionalGrain). This archived FAQ describes an unsupported 4.1-and-earlier authoring model which receives no new release line or security fixes."
      }
    },
    {
      "@type": "Question",
      "name": "How do I use Microsoft Orleans with F#?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "Use the current functional runtime (grainContract / grainFor / AddFunctionalGrain). The older authoring model on this page is an unsupported migration archive and is not a newly released 5.0 API."
      }
    },
    {
      "@type": "Question",
      "name": "Is Orleans.FSharp production-ready?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "The archived authoring model is unsupported and should not be selected for a new production deployment. Evaluate the current functional API and its documented production boundaries instead."
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
        "text": "This archived page records areas covered by the 4.1-and-earlier material; it is not a current support guarantee. Use the current functional API reference and release-status page for verified behavior."
      }
    }
  ]
}
</script>
