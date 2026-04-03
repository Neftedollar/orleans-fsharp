# Orleans.FSharp v2 Quickstart Guide

## Getting Started

```bash
dotnet new orleans-fsharp -n MyApp
cd MyApp
dotnet build
dotnet test
```

## Grain Definition with `grain { }` CE

```fsharp
open Orleans.FSharp

type CounterState = Zero | Count of int

type CounterCommand = Increment | Decrement | GetValue

let counter =
    grain {
        defaultState Zero

        handle (fun state cmd ->
            task {
                match state, cmd with
                | Zero, Increment -> return Count 1, box 1
                | Count n, Increment -> return Count(n + 1), box (n + 1)
                | Count n, Decrement when n > 1 -> return Count(n - 1), box (n - 1)
                | Count _, Decrement | Zero, Decrement -> return Zero, box 0
                | _, GetValue -> return state, box (CounterGrainDef.stateValue state)
            })

        persist "Default"
    }
```

## Silo Configuration with `siloConfig { }` CE

```fsharp
open Orleans.FSharp.Runtime

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        addMemoryStreams "StreamProvider"
        addMemoryReminderService
        useSerilog
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder
```

## Reminders

Register persistent reminders that survive grain deactivation and cluster restarts.

### CE keyword

```fsharp
let myGrain =
    grain {
        defaultState MyState.Initial

        handle (fun state cmd -> task { ... })

        onReminder "daily-check" (fun state reminderName tickStatus ->
            task {
                // Handle reminder tick
                return { state with LastChecked = DateTime.UtcNow }
            })

        persist "Default"
    }
```

### Programmatic API

```fsharp
open Orleans.FSharp

// Register a reminder (from within a grain)
let! reminder = Reminder.register grain "cleanup" (TimeSpan.FromMinutes 1.0) (TimeSpan.FromHours 1.0)

// Get an existing reminder
let! maybeReminder = Reminder.get grain "cleanup"

// Unregister a reminder
do! Reminder.unregister grain "cleanup"
```

## Observers (Pub/Sub)

Enable real-time notifications from grains to external subscribers.

```fsharp
open Orleans.FSharp

// Client-side: subscribe to observer
use subscription = Observer.subscribe<IChatObserver> grainFactory myObserverImpl

// Grain-side: manage subscriptions
let observerManager = FSharpObserverManager<IChatObserver>(TimeSpan.FromMinutes 5.0)

// Subscribe
observerManager.Subscribe(observerRef)

// Notify all subscribers
do! observerManager.Notify(fun observer ->
    task { do! observer.ReceiveMessage("Hello") })
```

## Reentrancy

Control concurrent message processing with CE keywords.

```fsharp
// Full reentrancy: all messages processed concurrently
let reentrantGrain =
    grain {
        defaultState initialState
        handle myHandler
        reentrant
    }

// Selective interleaving: specific methods run concurrently
let selectiveGrain =
    grain {
        defaultState initialState
        handle myHandler
        interleave "GetStatus"
    }
```

## Stateless Workers

Load-balanced stateless grains with multiple activations per silo.

```fsharp
let processor =
    grain {
        defaultState ()
        handle (fun _state cmd -> task { ... })
        statelessWorker
        maxActivations 4
    }
```

Note: Stateless workers cannot use `persist` -- an error is raised if both are specified.

## Event Sourcing

Event-sourced grains with the `eventSourcedGrain { }` CE.

```fsharp
open Orleans.FSharp.EventSourcing

type AccountState = { Balance: decimal }
type AccountEvent = Deposited of decimal | Withdrawn of decimal

let bankAccount =
    eventSourcedGrain {
        defaultState { Balance = 0m }

        apply (fun state event ->
            match event with
            | Deposited amount -> { state with Balance = state.Balance + amount }
            | Withdrawn amount -> { state with Balance = state.Balance - amount })

        handle (fun state cmd ->
            match cmd with
            | Deposit amount -> [ Deposited amount ]
            | Withdraw amount when state.Balance >= amount -> [ Withdrawn amount ]
            | Withdraw _ -> [])
    }
```

## Analyzer

The F# analyzer detects `async { }` usage and emits warning OF0001.

Configure in your .fsproj:
```xml
<PropertyGroup>
    <OtherFlags>--analyzers:path/to/Orleans.FSharp.Analyzers.dll</OtherFlags>
</PropertyGroup>
```

Suppress for specific functions with `[<AllowAsync>]`.

## Scripting (.fsx)

Interactive grain prototyping in F# scripts:

```fsharp
#r "nuget: Orleans.FSharp"
open Orleans.FSharp

let silo = Scripting.quickStart() |> Async.AwaitTask |> Async.RunSynchronously
let grain = Scripting.getGrain<IMyGrain> silo 1L
// ... interact with grain ...
Scripting.shutdown silo |> Async.AwaitTask |> Async.RunSynchronously
```

## FsCheck Property Testing with GrainArbitrary

Auto-generate FsCheck Arbitrary instances for grain state and command DUs:

```fsharp
open Orleans.FSharp.Testing

// Generate arbitrary state values
let stateArb = GrainArbitrary.forState<CounterState> ()

// Generate arbitrary command sequences
let cmdArb = GrainArbitrary.forCommands<CounterCommand> ()

// Use with FsCheck property testing
let prop = Prop.forAll cmdArb (fun cmds ->
    FsCheckHelpers.stateMachineProperty Zero apply invariant cmds)
Check.One(Config.QuickThrowOnFailure, prop)
```

## Project Structure

A typical Orleans.FSharp project:

```
src/
  MyApp.Grains/         -- F# grain definitions (DU states, commands, grain { } CE)
  MyApp.Silo/           -- Host program with siloConfig { } CE
  MyApp.CodeGen/        -- C# project for Orleans source generators
tests/
  MyApp.Tests/          -- Property-based + unit tests
```

The C# CodeGen project is required because Orleans source generators only support C#.
The grain behavior is defined entirely in F# -- the C# project just bridges the gap.
