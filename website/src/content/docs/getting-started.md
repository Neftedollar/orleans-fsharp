---
title: "Getting Started"
description: "Zero to working grain in 15 minutes."
---

# Getting Started

**Zero to working grain in 15 minutes.**

> **Note.** This guide teaches the **functional grain runtime** (`grainContract` / `grainFor` /
> `FunctionalGrain.ref` / `AddFunctionalGrain`) first -- it is the current grain authoring model. The
> original `grain { }` CE and universal `FSharpGrain.ref`/`send`/`ask` pattern still compile and run
> exactly as described, and are kept below under
> [Classic model (deprecated)](#classic-model-deprecated); their public surface now carries
> `[<Obsolete>]` (warning, not error). See [functional-grains.md](/orleans-fsharp/functional-grains/) for the
> complete guide to the current model.

## What you'll learn

- How to define a grain contract and API record with plain F# types — no C# interfaces to write
- How to configure and start a silo
- How to call your grain through a typed API record with `FunctionalGrain.ref`
- Where the classic `grain { }` CE walkthrough lives, if you are maintaining code on that model

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A code editor (VS Code + [Ionide](https://ionide.io), Rider, or Visual Studio)

## Step 1: Create the project

The fastest way to start is with the project template:

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyCounter
cd MyCounter
```

Or from scratch:

```bash
mkdir MyCounter && cd MyCounter
dotnet new console -lang F# -n MyCounter.Silo
cd MyCounter.Silo
dotnet add package Orleans.FSharp
dotnet add package Orleans.FSharp.Runtime
dotnet add package Microsoft.Orleans.Server
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
      decrement: unit -> Task<int>
      value: unit -> Task<int> }

[<RequireQualifiedAccess>]
module CounterApi =
    let contract =
        grainContract<CounterActor, string, CounterApi> () {
            grainType "counter"
            version 1
            stringKey
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

            handle
                (_.decrement)
                (fun _context state () ->
                    task {
                        let next = max 0 (state - 1)
                        return next, next
                    })

            handle (_.value) (fun _context state () -> task { return state, state })
        }
```

This counter's state is ephemeral (no `stateFrom`) -- it lives only as long as the activation does.
For durable state, attach `addMemoryStorage "provider-name"` on the silo plus `stateFrom` on the
definition; see the persistence model in [functional-grains.md](/orleans-fsharp/functional-grains/).

## Step 4: Configure the silo

```fsharp
let config = siloConfig {
    useLocalhostClustering
}
```

`useLocalhostClustering` runs a single-silo cluster — perfect for local development. `siloConfig { }`
is unaffected by the functional/classic split; it configures the silo either way.

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
| `grainContract<'Actor,'Key,'Api>() { }` | Computation expression defining the contract: identity, key codec, per-operation policies |
| `grainFor contract { }` | Computation expression defining state, handlers, persistence, lifecycle hooks, timers, reminders |
| `FunctionalGrain.ref` | Bind a typed API record: `IGrainFactory -> 'Key -> 'Api` |
| `FunctionalGrain.rawRef` | Bind the typed `FunctionalGrainRef` wrapper (`key`, `api`, `call`, `callCancellable`) |
| `AddFunctionalGrain` | Register a `grainFor` definition on the silo builder |
| `AddFunctionalGrainClient` | Register the client-side transport on a client-only process |
| `siloConfig { }` | Computation expression to configure the silo |

## Step 7: Test it

Unlike the classic `grain { }` CE, a `FunctionalGrainDefinition` exposes no handler-extraction
function comparable to `GrainDefinition.getHandler` -- but you already hold your own handler (the
plain function you passed to `handle`), so a handler that ignores `context` is directly callable in
a unit test. A handler that reads `context` (services, persistent state, grain factory) needs a real
activation, since `FunctionalGrainContext`'s constructor is internal. See [Testing](/orleans-fsharp/testing/) for
both patterns, including the full TestingHost-backed integration-test recipe.

## Step 8: Run it

```bash
dotnet build
dotnet run --project MyCounter.Silo
dotnet test
```

## Classic model (deprecated)

Everything below this heading is the original `grain { }` CE and universal `FSharpGrain.ref`/`send`/
`ask` pattern from earlier Orleans.FSharp releases. It still compiles and runs exactly as described;
its public surface now carries `[<Obsolete>]` (warning, not error). New code should use the
functional runtime above -- see [functional-grains.md](/orleans-fsharp/functional-grains/) for the complete
before/after mapping.

### Define state and commands

Define your state and commands as plain F# types. **No `[<GenerateSerializer>]` or `[<Id>]` attributes needed** — the built-in `FSharpBinaryCodec` handles serialization automatically.

```fsharp
open Orleans.FSharp
open Orleans.FSharp.Runtime

// Plain record — no attributes
type CounterState = { Count: int }

// Plain DU — no attributes
type CounterCommand =
    | Increment
    | Decrement
    | GetValue
```

### Define the grain

Use the `grain { }` computation expression. `handleTyped` is the most convenient handler variant — it auto-boxes the result so you never write `box` by hand:

```fsharp
let counter =
    grain {
        defaultState { Count = 0 }

        handleTyped (fun state cmd ->
            task {
                match cmd with
                | Increment -> return { Count = state.Count + 1 }, state.Count + 1
                | Decrement -> return { Count = state.Count - 1 }, state.Count - 1
                | GetValue  -> return state, state.Count
            })

        persist "Default"  // name of the storage provider
    }
```

The handler returns `(newState, result)` — the types are inferred, no `box` needed.
Use `handle` (manual `box`) when the return type varies per command case; use `handleState`
when you only care about state and don't need to return a separate result.
The `persist` keyword names the storage provider for durable state.

### Configure the silo

```fsharp
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}
```

`addMemoryStorage "Default"` wires in-memory state storage for the `persist "Default"` keyword above (data is cleared on restart; swap for Redis or Azure in production).

### Register the grain and start the host

```fsharp
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection

[<EntryPoint>]
let main _ =
    let builder = HostApplicationBuilder()

    // Register the grain definition with the universal dispatcher.
    // FSharpBinaryCodec is registered automatically — nothing else needed.
    builder.Services.AddFSharpGrain<CounterState, CounterCommand>(counter) |> ignore

    SiloConfig.applyToHost config builder

    let host = builder.Build()
    host.Start()

    let factory = host.Services.GetRequiredService<IGrainFactory>()

    // Get a typed handle — no generated interface required
    let handle = FSharpGrain.ref<CounterState, CounterCommand> factory "my-counter"

    // Send a command, get back the state
    let state = handle |> FSharpGrain.send Increment |> _.GetAwaiter().GetResult()
    printfn "Count after increment = %d" state.Count

    // ask returns a typed result (int here), not the state
    let count = handle |> FSharpGrain.ask<CounterState, CounterCommand, int> GetValue |> _.GetAwaiter().GetResult()
    printfn "Current count = %d" count

    printfn "Silo running. Press Enter to stop."
    System.Console.ReadLine() |> ignore
    host.StopAsync().GetAwaiter().GetResult()
    0
```

`FSharpGrain.ref` returns a zero-allocation struct handle (`FSharpGrainHandle<CounterState, CounterCommand>`). Piping commands through `FSharpGrain.send` (returns state) or `FSharpGrain.post` (fire-and-forget) keeps call sites clean.

### Key types at a glance

| Name | Purpose |
|---|---|
| `grain { }` | Computation expression to define grain behavior |
| `siloConfig { }` | Computation expression to configure the silo |
| `FSharpGrain.ref` | Create a string-keyed typed grain handle |
| `FSharpGrain.refGuid` | Create a GUID-keyed typed grain handle |
| `FSharpGrain.refInt` | Create an integer-keyed typed grain handle |
| `FSharpGrain.send` | Send command, return typed state (`Task<'State>`) |
| `FSharpGrain.ask` | Send command, return a different typed result (`Task<'R>`) |
| `FSharpGrain.post` | Fire-and-forget command |
| `AddFSharpGrain<S,M>` | Register a grain definition in DI |

### GUID and integer keys

```fsharp
open System

// GUID-keyed grain
let guidHandle = FSharpGrain.refGuid<CounterState, CounterCommand> factory (Guid.NewGuid())
let! state = guidHandle |> FSharpGrain.sendGuid Increment

// Integer-keyed grain
let intHandle = FSharpGrain.refInt<CounterState, CounterCommand> factory 42L
do! intHandle |> FSharpGrain.postInt Increment
```

### Model a state machine

A classic F# pattern is a DU state machine where the compiler enforces valid transitions:

```fsharp
type OrderState =
    | Created
    | Confirmed of confirmedAt: System.DateTime
    | Shipped   of trackingNumber: string
    | Delivered

type OrderCommand =
    | Confirm
    | Ship of trackingNumber: string
    | MarkDelivered
    | GetStatus

let order =
    grain {
        defaultState Created

        handle (fun state cmd ->
            task {
                match state, cmd with
                | Created,    Confirm            -> return Confirmed System.DateTime.UtcNow, box "confirmed"
                | Confirmed _, Ship tracking     -> return Shipped tracking, box tracking
                | Shipped _,  MarkDelivered      -> return Delivered, box "delivered"
                | _, GetStatus ->
                    let status =
                        match state with
                        | Created       -> "created"
                        | Confirmed _   -> "confirmed"
                        | Shipped t     -> $"shipped ({t})"
                        | Delivered     -> "delivered"
                    return state, box status
                | _ -> return state, box "invalid transition"
            })
    }
```

The F# compiler enforces exhaustive matching — illegal state/command pairs are compile errors.

### Write a property test with FsCheck

```bash
dotnet add package Orleans.FSharp.Testing
dotnet add package FsCheck.Xunit
dotnet add package xunit
```

```fsharp
open FsCheck.Xunit
open Orleans.FSharp

// Drive the grain handler directly — no silo, instant feedback.
let applyViaHandler (state: CounterState) cmd =
    let h = GrainDefinition.getHandler counter
    fst (h state cmd).GetAwaiter().GetResult()

[<Property>]
let ``Count equals net of Increments minus Decrements`` (commands: CounterCommand list) =
    let final = List.fold applyViaHandler { Count = 0 } commands
    let net =
        commands |> List.sumBy (function
            | Increment ->  1
            | Decrement -> -1
            | GetValue  ->  0)
    final.Count = net

[<Property>]
let ``GetValue never changes state`` (state: CounterState) =
    let h = GrainDefinition.getHandler counter
    let (ns, _) = (h state GetValue).GetAwaiter().GetResult()
    ns = state
```

### Run it

```bash
dotnet build
dotnet run --project MyCounter.Silo
dotnet test
```

## What's next

| Guide | Description |
|---|---|
| [Functional Grain Runtime](/orleans-fsharp/functional-grains/) | The complete guide to the current authoring model |
| [Grain Definition](/orleans-fsharp/grain-definition/) | Complete `grain { }` CE reference — all 31 keywords (deprecated model, kept for reference) |
| [Silo Configuration](/orleans-fsharp/silo-configuration/) | Clustering, storage, streaming, security |
| [Serialization](/orleans-fsharp/serialization/) | FSharpBinaryCodec, JSON fallback, Orleans native |
| [Streaming](/orleans-fsharp/streaming/) | Publish, subscribe, TaskSeq, broadcast |
| [Event Sourcing](/orleans-fsharp/event-sourcing/) | CQRS with `eventSourcedGrain { }` |
| [Testing](/orleans-fsharp/testing/) | TestHarness, GrainMock, property tests, and testing functional grains |
| [API Reference](/orleans-fsharp/api-reference/) | All public modules and functions |
