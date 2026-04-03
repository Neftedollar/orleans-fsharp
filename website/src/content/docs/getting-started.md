---
title: Getting Started
description: Zero to working grain in 15 minutes — create a project, define a grain, configure a silo, and write a property test
---

**Zero to working grain in 15 minutes.**

## What you'll learn

- How to create a new Orleans.FSharp project
- How to define a grain with discriminated union state
- How to configure and start a silo
- How to call your grain
- How to write a property test with FsCheck

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A code editor (VS Code with Ionide, Rider, or Visual Studio)

## Step 1: Create the project

The fastest way to start is with the project template:

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyCounter
cd MyCounter
```

Or create a project from scratch:

```bash
mkdir MyCounter && cd MyCounter
dotnet new console -lang F# -n MyCounter.Silo
cd MyCounter.Silo
dotnet add package Orleans.FSharp
dotnet add package Orleans.FSharp.Runtime
dotnet add package Orleans.FSharp.CodeGen
dotnet add package Microsoft.Orleans.Server
```

## Step 2: Define the grain state

Open `Program.fs` (or the main file in your project) and define your state as a discriminated union. Orleans.FSharp uses DUs as first-class grain state -- no mutable POCO classes needed.

```fsharp
open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime

[<GenerateSerializer>]
type CounterState =
    | [<Id(0u)>] Zero
    | [<Id(1u)>] Count of int
```

The `[<GenerateSerializer>]` and `[<Id(n)>]` attributes tell the Orleans serializer how to handle the DU cases.

## Step 3: Define the command type

Commands represent the messages your grain handles:

```fsharp
[<GenerateSerializer>]
type CounterCommand =
    | [<Id(0u)>] Increment
    | [<Id(1u)>] Decrement
    | [<Id(2u)>] GetValue
```

## Step 4: Define the grain

Use the `grain { }` computation expression to declaratively define grain behavior:

```fsharp
let counter =
    grain {
        defaultState Zero

        handle (fun state cmd ->
            task {
                match state, cmd with
                | Zero, Increment ->
                    return Count 1, box 1
                | Zero, Decrement ->
                    return Zero, box 0
                | Count n, Increment ->
                    return Count(n + 1), box(n + 1)
                | Count n, Decrement when n > 1 ->
                    return Count(n - 1), box(n - 1)
                | Count _, Decrement ->
                    return Zero, box 0
                | _, GetValue ->
                    let v = match state with Zero -> 0 | Count n -> n
                    return state, box v
            })

        persist "Default"
    }
```

The handler function takes the current state and a command, and returns a tuple of the new state and a boxed result. The `persist` keyword names the storage provider for state persistence.

## Step 5: Configure the silo

Use the `siloConfig { }` computation expression to configure the Orleans silo:

```fsharp
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}
```

For local development, `useLocalhostClustering` runs a single-silo cluster. `addMemoryStorage "Default"` provides in-memory grain storage (data is lost on restart -- swap to Redis or Azure for production).

## Step 6: Start the silo and call the grain

Wire the silo into a .NET host and make a grain call:

```fsharp
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection

[<EntryPoint>]
let main _ =
    let builder = HostApplicationBuilder()
    SiloConfig.applyToHost config builder

    let host = builder.Build()
    host.Start()

    let client = host.Services.GetRequiredService<IClusterClient>()
    let grainRef = GrainRef.ofString<ICounterGrain> client counter "my-counter"

    // Make grain calls here via the generated interface
    printfn "Silo running. Press Enter to stop."
    Console.ReadLine() |> ignore

    host.StopAsync().GetAwaiter().GetResult()
    0
```

The `Orleans.FSharp.CodeGen` package generates the C# grain class and interface from your grain definition at build time. The generated interface follows the naming convention `I{GrainName}Grain`.

## Step 7: Add a DU state machine

A common pattern is to model grain state as a state machine with DU cases representing each state:

```fsharp
[<GenerateSerializer>]
type OrderState =
    | [<Id(0u)>] Created
    | [<Id(1u)>] Confirmed of confirmedAt: DateTime
    | [<Id(2u)>] Shipped of trackingNumber: string
    | [<Id(3u)>] Delivered

[<GenerateSerializer>]
type OrderCommand =
    | [<Id(0u)>] Confirm
    | [<Id(1u)>] Ship of trackingNumber: string
    | [<Id(2u)>] MarkDelivered
    | [<Id(3u)>] GetStatus

let order =
    grain {
        defaultState Created

        handle (fun state cmd ->
            task {
                match state, cmd with
                | Created, Confirm ->
                    let newState = Confirmed(DateTime.UtcNow)
                    return newState, box "confirmed"
                | Confirmed _, Ship tracking ->
                    let newState = Shipped tracking
                    return newState, box tracking
                | Shipped _, MarkDelivered ->
                    return Delivered, box "delivered"
                | _, GetStatus ->
                    let status =
                        match state with
                        | Created -> "created"
                        | Confirmed _ -> "confirmed"
                        | Shipped t -> $"shipped ({t})"
                        | Delivered -> "delivered"
                    return state, box status
                | _ ->
                    return state, box "invalid transition"
            })

        persist "Default"
    }
```

The F# compiler ensures you handle all state/command combinations, catching illegal transitions at compile time.

## Step 8: Write a property test with FsCheck

Add test packages:

```bash
dotnet add package Orleans.FSharp.Testing
dotnet add package FsCheck
dotnet add package FsCheck.Xunit
dotnet add package xunit
```

Write a property test that verifies your state machine invariants hold for any sequence of commands:

```fsharp
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Testing

// The counter is never negative
let counterInvariant state =
    match state with
    | Zero -> true
    | Count n -> n > 0

// Pure state transition (no Task needed for testing)
let applyCommand state cmd =
    match state, cmd with
    | Zero, Increment -> Count 1
    | Zero, Decrement -> Zero
    | Count n, Increment -> Count(n + 1)
    | Count n, Decrement when n > 1 -> Count(n - 1)
    | Count _, Decrement -> Zero
    | s, GetValue -> s

[<Property>]
let ``counter is never negative for any command sequence`` () =
    let arb = GrainArbitrary.forCommands<CounterCommand>()
    Prop.forAll arb (fun commands ->
        FsCheckHelpers.stateMachineProperty Zero applyCommand counterInvariant commands)
```

`GrainArbitrary.forCommands` auto-generates random command sequences by inspecting the DU structure at runtime -- no manual Arbitrary instances needed.

## Step 9: Run it

```bash
dotnet build
dotnet run --project MyCounter.Silo
```

For tests:

```bash
dotnet test
```

## Next steps

- [Grain Definition](/orleans-fsharp/guides/grain-definition/) -- complete `grain { }` CE reference with all 30+ keywords
- [Silo Configuration](/orleans-fsharp/guides/silo-configuration/) -- every clustering, storage, and streaming option
- [Streaming](/orleans-fsharp/guides/streaming/) -- publish, subscribe, and consume streams
- [Event Sourcing](/orleans-fsharp/guides/event-sourcing/) -- CQRS with `eventSourcedGrain { }`
- [Testing](/orleans-fsharp/guides/testing/) -- TestHarness, GrainMock, property tests
