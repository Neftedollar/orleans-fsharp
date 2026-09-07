# Orleans.FSharp

<p align="center">
  <img src="https://raw.githubusercontent.com/Neftedollar/orleans-fsharp/main/website/public/orleans-fsharp-logo.svg" alt="Orleans.FSharp logo" width="156" height="156" />
</p>

**Functional Orleans actors with typed F# APIs and explicit state transitions.**

[![CI](https://github.com/Neftedollar/orleans-fsharp/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/orleans-fsharp/actions)
[![NuGet](https://img.shields.io/nuget/v/Orleans.FSharp.svg)](https://www.nuget.org/packages/Orleans.FSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Orleans 10](https://img.shields.io/badge/Orleans-10.1.0%20%E2%80%93%2010.3.1-blue)](https://learn.microsoft.com/dotnet/orleans/)
[![F#](https://img.shields.io/badge/F%23-9%2B-378BBA)](https://fsharp.org/)

Orleans.FSharp keeps the Microsoft Orleans runtime and gives it an F#-first authoring model:
grain APIs are records of functions, behavior is expressed as state transitions, and Orleans
continues to own routing, activation, clustering, persistence, streams, and transactions.

[Documentation](https://neftedollar.com/orleans-fsharp/) ·
[Getting started](docs/getting-started.md) ·
[Examples](docs/examples.md) ·
[Release status](docs/release-status.md)

This README documents **Orleans.FSharp 5.0.1**, the current stable release. Applications upgrading
from 4.x should review the [breaking changes](CHANGELOG.md#500---2026-09-07) and
[Legacy migration guide](docs/legacy/migration.md).

## Why Orleans.FSharp?

- **F# APIs stay F#.** A grain surface is a typed record of functions using records,
  discriminated unions, options, results, and domain-specific keys.
- **State changes are visible.** Handlers return the next state or emitted events instead of
  mutating an actor object implicitly.
- **Orleans semantics remain native.** Versioning, placement, transactions, streams, reminders,
  persistence, and log-consistency providers still use Orleans underneath.
- **No application-specific bridge project.** Functional references use a fixed transport shipped
  with the library; there is no grain interface or generated proxy to maintain per actor.
- **C# can call the same actor.** A typed C# facade binds to the same contract without changing the
  F# domain model.

## Quick start

Define the public API, its stable Orleans contract, and the behavior in one place:

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      value: unit -> Task<int> }

[<RequireQualifiedAccess>]
module Counter =
    let contract =
        grainContract<CounterActor, string, CounterApi> {
            grainType "counter"
            version 1
            stringKey
            readOnly (_.value)
        }

    let definition =
        grainFor contract {
            defaultState (fun () -> 0)

            handle (_.increment) (fun _ state () ->
                task {
                    let next = state + 1
                    return next, next
                })

            handleQuery (_.value) (fun _ state () -> task { return state })
        }

    let ref = FunctionalGrain.ref contract
```

Register the definition in a silo, then call the returned API record:

```fsharp
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime

let addCounter (builder: HostApplicationBuilder) =
    builder.UseOrleans(fun silo ->
        silo.AddFunctionalGrain(Counter.definition) |> ignore)
    |> ignore

let callCounter (grainFactory: Orleans.IGrainFactory) =
    task {
        let counter = Counter.ref grainFactory "counter-42"
        let! next = counter.increment ()
        let! current = counter.value ()
        return next, current
    }
```

`counter` is a typed client facade. Calling one of its fields still crosses Orleans routing and
activation boundaries; it is not an in-process function call.

For a complete host, persistence, and tests, continue with
[Getting started](docs/getting-started.md).

## Install

Start a working solution with the 5.0.1 template:

```bash
dotnet new install Orleans.FSharp.Templates@5.0.1
dotnet new orleans-fsharp -n MyApp
cd MyApp
dotnet build
dotnet test
dotnet run --project src/MyApp.Silo
```

The 5.0.1 template includes the startup correction. If you already generated an application with
the 5.0.0 template, apply the [one-line source correction](docs/release-status.md#the-500-project-template-needs-a-one-line-contract-correction);
updating packages does not rewrite existing application files.

Or add the packages to an existing application:

```bash
dotnet add package Orleans.FSharp --version 5.0.1
dotnet add package Orleans.FSharp.Runtime --version 5.0.1
```

Add testing support when needed:

```bash
dotnet add package Orleans.FSharp.Testing --version 5.0.1
```

`Orleans.FSharp.Abstractions`, which contains the fixed transport and precompiled Orleans
proxies, arrives transitively. Applications do not reference it directly.

To run the repository examples:

```bash
git clone --branch v5.0.1 https://github.com/Neftedollar/orleans-fsharp.git
cd orleans-fsharp
dotnet build Orleans.FSharp.slnx
dotnet run --project examples/feature-tour/src/FeatureTour
```

## The four building blocks

| Building block | Responsibility |
|---|---|
| API record | The operations callers can invoke |
| `grainContract` | Actor identity, key encoding, contract version, and call policies |
| `grainFor` / `journaledGrainFor` | State, handlers, persistence, lifecycle, and event application |
| `FunctionalGrain.ref` | A typed API record bound to an Orleans `IGrainFactory` and grain key |

## What is supported

| Area | Highlights | Guide |
|---|---|---|
| Contracts | Typed keys, operation policies, version routing, C# facades | [Contracts and keys](docs/functional-grains/contracts.md) |
| State | Ephemeral, persistent, transactional, per-state codecs and upcasters | [State and lifecycle](docs/functional-grains/state-and-lifecycle.md) |
| Communication | Calls, observers, streams, broadcast channels, server-streaming replies | [Calls and delivery](docs/functional-grains/delivery-and-streaming.md) |
| Scheduling | Activation hooks, lifecycle stages, timers, and reminders | [State and lifecycle](docs/functional-grains/state-and-lifecycle.md) |
| Distribution | Stateless workers, Orleans placement strategies, activation migration | [Placement and transactions](docs/functional-grains/placement-and-transactions.md) |
| Event sourcing | Orleans log-consistency providers, pure event folds, schema evolution, snapshots | [Event sourcing](docs/event-sourcing.md) |
| Evolution | Rolling upgrades, versioned transport, state and event upcasters | [Compatibility](docs/compatibility.md) |
| Operations | Dashboard, health checks, security, testing, and observability | [Documentation](https://neftedollar.com/orleans-fsharp/) |

The [complete functional runtime reference](docs/functional-grains.md) lists every builder
operation, invariant, and known boundary.

## Examples worth opening first

| Example | Start here when you need… |
|---|---|
| [Feature Tour](examples/feature-tour) | A live matrix of the supported Orleans surface |
| [Chat Room](examples/chat-room) | Persistence, observers, one-way calls, and a C# caller |
| [Bank Account](examples/bank-account) | Event sourcing and replay with `journaledGrainFor` |
| [Order Processing](examples/order-processing) | Domain-oriented F# with DUs, typed failures, and durable workflow state |

See [all runnable examples](docs/examples.md), including transactions, Dashboard, SignalR,
Fable, and type-safe IDs.

## Documentation map

| Goal | Read |
|---|---|
| Build a first actor | [Getting started](docs/getting-started.md) |
| Understand the model | [Functional runtime overview](docs/functional-runtime.md) |
| Copy a focused recipe | [How-to guides](docs/how-to.md) |
| Configure a host or client | [Silo configuration](docs/silo-configuration.md) · [Client configuration](docs/client-configuration.md) |
| Choose serialization and persistence formats | [Serialization](docs/serialization.md) |
| Add streams or streaming replies | [Streams](docs/streaming.md) · [Server-streaming replies](docs/streaming-replies.md) |
| Test deployment compatibility | [Testing](docs/testing.md) · [Release status](docs/release-status.md) |
| Look up an API | [API reference](docs/api-reference.md) |

## Packages

| Package | Purpose |
|---|---|
| `Orleans.FSharp` | Contracts, functional definitions, references, streaming, and serialization |
| `Orleans.FSharp.Runtime` | Silo/client configuration and functional grain registration |
| `Orleans.FSharp.Testing` | Test helpers, FsCheck support, and log capture |
| `Orleans.FSharp.Analyzers` | F#-specific correctness diagnostics |
| `Orleans.FSharp.Templates` | The matching `dotnet new` project template |

## Migrating an existing application

Version-by-version upgrade notes do not live in this README:

- [Migrate from Orleans.FSharp 4.x and earlier](docs/legacy/migration.md)
- [Archived documentation for the earlier authoring model](docs/legacy/index.md)
- [Release-by-release changes](CHANGELOG.md)

The Legacy archive is retained for migration only and does not receive new features,
compatibility work, or security fixes.

## Project

- Security policy and deployment guidance: [Security](docs/security.md) and [SECURITY.md](SECURITY.md)
- Contributions: issues and pull requests are welcome on [GitHub](https://github.com/Neftedollar/orleans-fsharp)
- License: [MIT](LICENSE)
