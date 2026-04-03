# Getting Started

**Zero to working grain in 15 minutes.**

## What you'll learn

- How to define a grain with plain F# types — no attributes, no C# stubs
- How to configure and start a silo
- How to call your grain with the universal `FSharpGrain.ref` pattern
- How to write a property test with FsCheck

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
dotnet add package Orleans.FSharp.Abstractions   # C# shim — enables Orleans proxy generation
dotnet add package Microsoft.Orleans.Server
```

## Step 2: Define state and commands

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

## Step 3: Define the grain

Use the `grain { }` computation expression:

```fsharp
let counter =
    grain {
        defaultState { Count = 0 }

        handle (fun state cmd ->
            task {
                match cmd with
                | Increment -> return { Count = state.Count + 1 }, box (state.Count + 1)
                | Decrement -> return { Count = state.Count - 1 }, box (state.Count - 1)
                | GetValue  -> return state, box state.Count
            })

        persist "Default"  // name of the storage provider
    }
```

The handler receives the current state and a command, and returns `(newState, boxedResult)`. The `persist` keyword names the storage provider used for durable state.

## Step 4: Configure the silo

```fsharp
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}
```

`useLocalhostClustering` runs a single-silo cluster — perfect for local development. `addMemoryStorage "Default"` wires in-memory state storage (data is cleared on restart; swap for Redis or Azure in production).

## Step 5: Register the grain and start the host

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

    // Send commands
    let count = handle |> FSharpGrain.send GetValue |> Async.AwaitTask |> Async.RunSynchronously
    printfn "Count = %A" count

    printfn "Silo running. Press Enter to stop."
    System.Console.ReadLine() |> ignore
    host.StopAsync().GetAwaiter().GetResult()
    0
```

`FSharpGrain.ref` returns a zero-allocation struct handle (`FSharpGrainHandle<CounterState, CounterCommand>`). Piping commands through `FSharpGrain.send` (returns state) or `FSharpGrain.post` (fire-and-forget) keeps call sites clean.

## Step 6: Key types at a glance

| Name | Purpose |
|---|---|
| `grain { }` | Computation expression to define grain behavior |
| `siloConfig { }` | Computation expression to configure the silo |
| `FSharpGrain.ref` | Create a string-keyed typed grain handle |
| `FSharpGrain.refGuid` | Create a GUID-keyed typed grain handle |
| `FSharpGrain.refInt` | Create an integer-keyed typed grain handle |
| `FSharpGrain.send` | Send command, return typed state |
| `FSharpGrain.post` | Fire-and-forget command |
| `AddFSharpGrain<S,M>` | Register a grain definition in DI |

## Step 7: GUID and integer keys

```fsharp
open System

// GUID-keyed grain
let guidHandle = FSharpGrain.refGuid<CounterState, CounterCommand> factory (Guid.NewGuid())
let! state = guidHandle |> FSharpGrain.sendGuid Increment

// Integer-keyed grain
let intHandle = FSharpGrain.refInt<CounterState, CounterCommand> factory 42L
do! intHandle |> FSharpGrain.postInt Increment
```

## Step 8: Model a state machine

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

## Step 9: Write a property test with FsCheck

```bash
dotnet add package Orleans.FSharp.Testing
dotnet add package FsCheck.Xunit
dotnet add package xunit
```

```fsharp
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Testing

// Pure transition function for testing (no Task overhead)
let applyCommand (state: CounterState) cmd =
    match cmd with
    | Increment -> { Count = state.Count + 1 }
    | Decrement -> { Count = state.Count - 1 }
    | GetValue  -> state

[<Property>]
let ``Count equals number of Increments minus Decrements`` (commands: CounterCommand list) =
    let final = List.fold applyCommand { Count = 0 } commands
    let net =
        commands |> List.sumBy (function
            | Increment ->  1
            | Decrement -> -1
            | GetValue  ->  0)
    final.Count = net
```

`GrainArbitrary.forCommands<'T>()` auto-generates random command sequences if you want them constrained:

```fsharp
[<Property>]
let ``Non-negative count is preserved after Increment`` (n: PositiveInt) =
    let arb = GrainArbitrary.forCommands<CounterCommand>()
    Prop.forAll arb (fun commands ->
        let final = List.fold applyCommand { Count = 0 } commands
        true // replace with your invariant
    )
```

## Step 10: Run it

```bash
dotnet build
dotnet run --project MyCounter.Silo
dotnet test
```

## What's next

| Guide | Description |
|---|---|
| [Grain Definition](grain-definition.md) | Complete `grain { }` CE reference — all 31 keywords |
| [Silo Configuration](silo-configuration.md) | Clustering, storage, streaming, security |
| [Serialization](serialization.md) | FSharpBinaryCodec, JSON fallback, Orleans native |
| [Streaming](streaming.md) | Publish, subscribe, TaskSeq, broadcast |
| [Event Sourcing](event-sourcing.md) | CQRS with `eventSourcedGrain { }` |
| [Testing](testing.md) | TestHarness, GrainMock, property tests |
| [API Reference](api-reference.md) | All public modules and functions |
