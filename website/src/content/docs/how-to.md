---
title: "How To: Functional Orleans Application"
description: "Build a typed Orleans application with the current Orleans.FSharp API."
---

# How To: Build a Functional Orleans Application

This tutorial builds a small typed counter with the current Orleans.FSharp API.

## 1. Create the project

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyDistributedApp
cd MyDistributedApp
```

The template creates an F# application and tests. You do not need to write a C# proxy interface or a source-generation bridge.

## 2. Define the typed API

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      value: unit -> Task<int> }

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

The actor brand keeps unrelated contracts distinct. `grainType` is the durable wire identity, and `stringKey` defines how application keys map to Orleans grain keys.

## 3. Define behavior

```fsharp
let counterDefinition =
    grainFor CounterApi.contract {
        defaultState (fun () -> 0)

        handle (_.increment) (fun _ctx count () ->
            task {
                let next = count + 1
                return next, next
            })

        handleQuery (_.value) (fun _ctx count () ->
            task { return count })
    }
```

Each record field has one handler. State transitions are explicit values; `handleQuery` returns a reply without replacing state.

## 4. Configure and register the silo

```fsharp
open Microsoft.Extensions.Hosting
open Orleans.Hosting
open Orleans.FSharp.Runtime

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
    }

let builder = HostApplicationBuilder()
SiloConfig.applyToHost config builder

builder.UseOrleans(fun siloBuilder ->
    siloBuilder.AddFunctionalGrain(counterDefinition) |> ignore)
|> ignore

let host = builder.Build()
do! host.StartAsync()
```

A client-only process calls `AddFunctionalGrainClient()` on its Orleans client builder instead.

## 5. Call the actor

```fsharp
open Microsoft.Extensions.DependencyInjection
open Orleans

let factory = host.Services.GetRequiredService<IGrainFactory>()
let counter = CounterApi.ref factory "visits"

let! first = counter.increment ()
let! current = counter.value ()
```

The call site is the API record itself. There is no boxed command or untyped reply.

## 6. Test through a real activation

Register the same definition in an Orleans `TestingHost` fixture, obtain `CounterApi.ref fixture.Client "test"`, and assert replies through the public API. Keep context-free handler functions named separately when you also want fast pure unit tests.

See [Testing](/orleans-fsharp/testing/) for a complete fixture.

## 7. Add production capabilities

- Durable state: create a `PersistentState` descriptor and attach it with `stateFrom`.
- Event sourcing: replace `grainFor` with `journaledGrainFor` and provide a pure `apply` fold.
- Streams and broadcasts: add `onStream` or `onBroadcast` to the definition.
- Timers and reminders: add `onTimer` or `onReminder`.
- Dashboard: add the package, `addDashboard`, and map the dashboard endpoint.

## Next steps

- [Functional Grain Runtime](/orleans-fsharp/functional-grains/)
- [Event Sourcing](/orleans-fsharp/event-sourcing/)
- [Dashboard](/orleans-fsharp/dashboard/)
- [Silo Configuration](/orleans-fsharp/silo-configuration/)
- [Legacy API](/orleans-fsharp/legacy/)
