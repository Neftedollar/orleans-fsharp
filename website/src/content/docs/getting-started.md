---
title: "Getting Started"
description: "Zero to working grain in 15 minutes."
---

# Getting Started

**Zero to working grain in 15 minutes.**

> **Current API.** This guide uses `grainContract` / `grainFor`, typed API records, and
> `FunctionalGrain.ref`. Earlier authoring models are isolated in the unsupported
> [Legacy archive](/orleans-fsharp/legacy/).

> **Version scope.** This guide describes Orleans.FSharp 5.0.1, the current published stable
> release; see [Release and Production Status](/orleans-fsharp/release-status/).

## What you'll learn

- How to define a grain contract and API record with plain F# types — no C# interfaces to write
- How to configure and start a silo
- How to call your grain through a typed API record with `FunctionalGrain.ref`
- How explicit key codecs keep contract identity stable

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A code editor (VS Code + [Ionide](https://ionide.io), Rider, or Visual Studio)

## Step 1: Create the project

Install the published Orleans.FSharp 5.0.1 template, which uses the current functional API:

```bash
dotnet new install Orleans.FSharp.Templates@5.0.1
dotnet new orleans-fsharp -n MyCounter
cd MyCounter
```

Already generated with 5.0.0? See the
[one-line startup correction](/orleans-fsharp/release-status/#the-500-project-template-needs-a-one-line-contract-correction);
updating the template package does not rewrite existing source.

If you are contributing to Orleans.FSharp or validating changes from `main`, clone the repository
and replace the package-install command above with the optional checkout install:

```bash
git clone https://github.com/Neftedollar/orleans-fsharp.git
dotnet new install ./orleans-fsharp/templates
```

Or from scratch:

```bash
mkdir MyCounter && cd MyCounter
dotnet new console -lang F# -n MyCounter.Silo
cd MyCounter.Silo
dotnet add package Orleans.FSharp --version 5.0.1
dotnet add package Orleans.FSharp.Runtime --version 5.0.1
dotnet add package Microsoft.Orleans.Server --version 10.3.1
```

`Orleans.FSharp.Abstractions` -- the C# assembly the functional runtime's pre-generated proxies live
in -- comes in transitively through `Orleans.FSharp`; you do not add it, or write a bridge project of
your own, to call a functional grain.

## Step 2: Define the contract and API record

A **contract** gives your grain a stable wire identity (a `grainType` string and a key codec); the
**API record** is a plain F# record of functions describing what you can call. No `[<GenerateSerializer>]`
or `[<Id>]` attributes needed anywhere — the built-in `FSharpBinaryCodec` handles serialization
automatically.

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      value: unit -> Task<int> }

[<RequireQualifiedAccess>]
module CounterApi =
    let contract =
        grainContract<CounterActor, string, CounterApi> {
            grainType "counter"
            version 1
            stringKey
            readOnly (_.value)
        }

    let ref = FunctionalGrain.ref contract
```

`CounterActor` is a phantom brand type -- it never gets constructed, it only ties the contract, the
API record, and every `FunctionalGrain.ref` call site to the same grain identity at compile time.
Every field of `CounterApi` is one callable **operation**; its wire ID defaults to the field name.

## Step 3: Define the grain

`grainFor { }` attaches state and handlers to the contract. Each handler receives the invocation
context, the current state, and the exact argument, and returns `(newState, reply)`:

```fsharp
module Definition =
    let counterDefinition =
        grainFor CounterApi.contract {
            defaultState (fun () -> 0)

            handle
                (_.increment)
                (fun _context state () ->
                    task {
                        let next = state + 1
                        return next, next
                    })

            handleQuery (_.value) (fun _context state () -> task { return state })
        }
```

This counter's state is ephemeral (no `stateFrom`) -- it lives only as long as the activation does.
For durable state, attach `addMemoryStorage "provider-name"` on the silo plus `stateFrom` on the
definition; see the persistence model in [functional-grains.md](/orleans-fsharp/functional-grains/).

## Step 4: Configure the silo

```fsharp
open Orleans.FSharp.Runtime

let config = siloConfig {
    useLocalhostClustering
}
```

`useLocalhostClustering` runs a single-silo cluster — perfect for local development. `siloConfig { }`
configures hosting independently from the functional grain definitions.

## Step 5: Register the grain and start the host

```fsharp
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp
open Orleans.FSharp.Runtime

[<EntryPoint>]
let main _ =
    let builder = HostApplicationBuilder()
    SiloConfig.applyToHost config builder

    // AddFunctionalGrain is enough for a colocated process: the same IGrainFactory that hosts
    // the definition also binds its own functional references. A genuinely separate
    // client-only process would call `clientBuilder.AddFunctionalGrainClient()` instead.
    builder.UseOrleans(fun siloBuilder ->
        siloBuilder.AddFunctionalGrain(Definition.counterDefinition) |> ignore)
    |> ignore

    let host = builder.Build()
    host.Start()

    let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()

    // Bind a typed API record — no generated interface required.
    let api = CounterApi.ref factory "my-counter"

    let count1 = (api.increment ()).GetAwaiter().GetResult()
    printfn "Count after increment = %d" count1

    let count2 = (api.value ()).GetAwaiter().GetResult()
    printfn "Current count = %d" count2

    printfn "Silo running. Press Enter to stop."
    System.Console.ReadLine() |> ignore
    host.StopAsync().GetAwaiter().GetResult()
    0
```

`api` is a plain `CounterApi` value -- calling `api.increment ()` calls the operation directly, with
no intermediate handle type and no boxed reply to unwrap.

## Step 6: Key types at a glance

| Name | Purpose |
|---|---|
| `grainContract<'Actor,'Key,'Api> { }` | Computation expression defining the contract: identity, key codec, per-operation policies |
| `grainFor contract { }` | Computation expression defining state, handlers, persistence, lifecycle hooks, timers, reminders |
| `FunctionalGrain.ref` | Bind a typed API record: `IGrainFactory -> 'Key -> 'Api` |
| `FunctionalGrain.rawRef` | Bind the typed `FunctionalGrainRef` wrapper (`key`, `api`, `call`, `callCancellable`, `stream`, `streamCancellable`) |
| `AddFunctionalGrain` | Register a `grainFor` definition on the silo builder |
| `AddFunctionalGrainClient` | Register the client-side transport on a client-only process |
| `siloConfig { }` | Computation expression to configure the silo |

## Step 7: Test it

A functional definition keeps your handler as an ordinary function value. A handler that ignores
`context` is therefore directly callable in a unit test. A handler that reads `context` (services,
persistent state, grain factory) needs a real activation, since `FunctionalGrainContext`'s
constructor is internal. See [Testing](/orleans-fsharp/testing/) for both patterns, including the full
TestingHost-backed integration-test recipe.

## Step 8: Run it

```bash
dotnet build
dotnet run --project MyCounter.Silo
dotnet test
```

## What's next

| Guide | Description |
|---|---|
| [Functional Grain Runtime](/orleans-fsharp/functional-runtime/) | The short path through the current authoring model |
| [Release and Production Status](/orleans-fsharp/release-status/) | Orleans.FSharp 5.0.1 stable release and production boundaries |
| [Functional Runtime Reference](/orleans-fsharp/functional-grains/) | Complete builder operations, invariants, and edge cases |
| [Examples](/orleans-fsharp/examples/) | Runnable projects mapped to features and use cases |
| [Silo Configuration](/orleans-fsharp/silo-configuration/) | Clustering, storage, streaming, security |
| [Serialization](/orleans-fsharp/serialization/) | FSharpBinaryCodec, F# JSON, Orleans native |
| [Streaming](/orleans-fsharp/streaming/) | Publish, subscribe, TaskSeq, broadcast |
| [Event Sourcing](/orleans-fsharp/event-sourcing/) | `journaledGrainFor { }` — state as the fold of an event journal, including snapshots |
| [Dashboard](/orleans-fsharp/dashboard/) | Run Orleans Dashboard and inspect functional actor activations |
| [Testing](/orleans-fsharp/testing/) | TestingHost integration tests, pure handlers, FsCheck, and log capture |
| [API Reference](/orleans-fsharp/api-reference/) | All public modules and functions |

Maintaining an existing application on an earlier authoring model? The unsupported material is
isolated in the [Legacy archive](/orleans-fsharp/legacy/).
