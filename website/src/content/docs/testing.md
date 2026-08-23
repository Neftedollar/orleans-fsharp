---
title: "Testing"
description: "TestHarness, GrainMock, GrainArbitrary, FsCheck, and log capture."
---

# Testing

**Guide to testing Orleans.FSharp grains.**

> **Current API.** This guide covers `grainContract`, `grainFor`, and `journaledGrainFor`. Tests for earlier authoring models live under [Legacy Testing](/orleans-fsharp/legacy/testing/).

## What you'll learn

- How to call context-free handler functions directly
- How to test a functional grain with a real `TestingHost` cluster
- How to use `TestHarness` for integration tests
- How to generate property-test data with `GrainArbitrary` and FsCheck
- How to test journal folds and snapshot behavior
- How to capture and assert on log entries

## Installation

```bash
dotnet add package Orleans.FSharp.Testing
dotnet add package FsCheck
dotnet add package FsCheck.Xunit
dotnet add package xunit
dotnet add package Microsoft.Orleans.TestingHost
```

---


## Testing a Functional Grain

A handler that does not inspect `FunctionalGrainContext` remains an ordinary function and can be
called directly. Full activation behavior uses a lightweight `TestingHost` cluster, driven through
`FunctionalGrain.ref` exactly as production code would, because application code cannot fabricate
the runtime-owned context. `counterDefinition` and `CounterApi` below are the same
`grainContract` / `grainFor` pair shown in [Getting Started](/orleans-fsharp/getting-started/):

```fsharp
open System
open System.Threading.Tasks
open Orleans
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      value: unit -> Task<int> }

[<RequireQualifiedAccess>]
module CounterApi =
    let contract =
        grainContract<CounterActor, string, CounterApi> {
            grainType "testing.counter"
            version 1
            stringKey
            readOnly (_.value)
        }

    let ref = FunctionalGrain.ref contract

let counterDefinition =
    grainFor CounterApi.contract {
        defaultState (fun () -> 0)
        handle (_.increment) (fun _ count () ->
            task {
                let next = count + 1
                return next, next
            })
        handleQuery (_.value) (fun _ count () -> task { return count })
    }

type CounterSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.AddFunctionalGrain(counterDefinition) |> ignore

type CounterClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

type CounterClusterFixture() =
    let cluster =
        let builder = TestClusterBuilder(1s)
        builder.AddSiloBuilderConfigurator<CounterSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<CounterClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster

    member _.Client = cluster.Client

    interface IDisposable with
        member _.Dispose() = cluster.StopAllSilos()

type CounterFunctionalTests(fixture: CounterClusterFixture) =
    interface IClassFixture<CounterClusterFixture>

    [<Fact>]
    member _.``increment round-trips through a real activation``() =
        task {
            let api = CounterApi.ref fixture.Client "test-counter"
            let! c1 = api.increment ()
            let! c2 = api.increment ()
            Assert.Equal(1, c1)
            Assert.Equal(2, c2)
        }
```

`GrainArbitrary.forState<'State>()` works directly because a functional grain's state is an
ordinary F# type, DU or otherwise. `GrainArbitrary.forCommands<'Command>()` is less direct: it
targets one command discriminated union, while a functional API
record is several independently typed operations rather than one sum type. A property test over a
functional grain instead drives the API record's operations directly in a loop (or the raw handler
functions, for the context-free case above) rather than folding over a generated `'Command list`.

---

## TestHarness

`TestHarness` wraps an Orleans `TestCluster` with integrated log capture. It provides a real silo for integration tests.

### Create a test cluster

```fsharp
open Orleans.FSharp.Testing

let createHarness () = TestHarness.createTestCluster()
```

This starts a single-silo cluster with:
- In-memory grain storage (named "Default")
- In-memory streams (named "StreamProvider")
- PubSubStore for streams
- A log capturing factory

### Create with custom configuration

```fsharp
open Orleans.FSharp.Runtime

let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    addMemoryStorage "Archive"
    addMemoryReminderService
}

let createCustomHarness () = TestHarness.createTestClusterWith config
```

### Capture logs

```fsharp
open Microsoft.Extensions.Logging
open Xunit

let assertNoWarnings harness =
    let logs = TestHarness.captureLogs harness

    for entry in logs do
        printfn "[%A] %s" entry.Level entry.Template

    logs
    |> List.filter (fun entry -> entry.Level = LogLevel.Warning)
    |> Assert.Empty
```

### Reset and dispose

```fsharp
let resetAndDispose harness =
    task {
        do! TestHarness.reset harness
        do! TestHarness.dispose harness
    }
```

## GrainArbitrary

`GrainArbitrary` auto-generates FsCheck `Arbitrary` instances for F# discriminated unions by inspecting the DU structure at runtime. No manual generator code needed.

### Generate state values

```fsharp
open Orleans.FSharp.Testing

type CounterState =
    | Zero
    | Count of int

type CounterCommand =
    | Increment
    | Decrement
    | GetValue

let arb = GrainArbitrary.forState<CounterState>()

// Use in a property test
let gen = arb |> FsCheck.FSharp.Arb.toGen
let sample = gen |> FsCheck.FSharp.Gen.sampleWithSize 10 5
// Produces: [Zero; Count 42; Count -7; Zero; Count 1]
```

### Generate command sequences

```fsharp
let arb = GrainArbitrary.forCommands<CounterCommand>()

// Produces non-empty lists like:
// [Increment; Decrement; GetValue; Increment]
// [GetValue]
// [Decrement; Increment; Increment]
```

`GrainArbitrary` handles:
- Fieldless DU cases (e.g., `Zero`, `Increment`)
- Single-field cases (e.g., `Count of int`)
- Multi-field cases (e.g., `Transfer of amount: decimal * account: string`)
- Nested DUs, records, options, lists
- Falls back to FsCheck default generators for primitive types

---

## FsCheckHelpers

The `FsCheckHelpers` module provides property test utilities.

### State machine property

Verify that an invariant holds for any sequence of commands:

```fsharp
open Orleans.FSharp.Testing

let counterInvariant state =
    match state with
    | Zero -> true
    | Count n -> n > 0

let applyCommand state cmd =
    match state, cmd with
    | Zero, Increment -> Count 1
    | Zero, Decrement -> Zero
    | Count n, Increment -> Count(n + 1)
    | Count n, Decrement when n > 1 -> Count(n - 1)
    | Count _, Decrement -> Zero
    | s, GetValue -> s

[<Property>]
let ``counter is never negative`` () =
    let arb = GrainArbitrary.forCommands<CounterCommand>()
    Prop.forAll arb (fun commands ->
        FsCheckHelpers.stateMachineProperty
            Zero applyCommand counterInvariant commands)
```

### Command sequence arbitrary

Generate random command sequences using default FsCheck arbitraries:

```fsharp
let arb = FsCheckHelpers.commandSequenceArb<CounterCommand>()
```

---

## Log Capture

The `LogCapture` module provides in-memory log capture for test assertions.

### Create a capturing factory

```fsharp
let logFactory = LogCapture.create()
let logger = (logFactory :> ILoggerFactory).CreateLogger("Test")

logger.LogInformation("Hello {Name}", "World")

let entries = LogCapture.captureLogs logFactory
Assert.Single(entries) |> ignore
Assert.Equal("Hello {Name}", entries.[0].Template)
Assert.Equal("World", entries.[0].Properties.["Name"] :?> string)
```

### CapturedLogEntry

Each entry contains:

| Field | Type | Description |
|---|---|---|
| `Level` | `LogLevel` | Information, Warning, Error, Debug, etc. |
| `Template` | `string` | The structured log template |
| `Properties` | `Map<string, obj>` | Template argument values |
| `Timestamp` | `DateTimeOffset` | When the entry was captured |
| `Exception` | `exn option` | Associated exception, if any |

---

## Testing a journaled grain

Keep the event fold as a named pure function and test it directly. Use a `TestingHost` cluster for confirmation, provider, snapshot, timer, reminder, stream, and broadcast behavior.

```fsharp
let applyAccount state event =
    match event with
    | Deposited amount -> { state with balance = state.balance + amount }
    | Withdrawn amount -> { state with balance = state.balance - amount }

[<Fact>]
let ``apply folds a deposit`` () =
    let initial = { balance = 10m }
    Assert.Equal({ balance = 15m }, applyAccount initial (Deposited 5m))
```

The integration fixture registers the definition with `AddFunctionalJournaledGrain`, configures one
of Orleans' log-consistency providers, then calls the typed API via `FunctionalGrain.ref`. Snapshot
policy tests should use a custom storage adapter and assert the stored version and state, not only
the reply.

## Legacy tests

Tests for the original APIs are retained in [Legacy Testing](/orleans-fsharp/legacy/testing/).

## Next steps

- [Functional Grain Runtime](/orleans-fsharp/functional-grains/) -- the current authoring model
- [Legacy API](/orleans-fsharp/legacy/) -- maintenance tests for earlier authoring models
- [Event Sourcing](/orleans-fsharp/event-sourcing/) -- testing event-sourced grains
- [Advanced](/orleans-fsharp/advanced/) -- transactions, serialization, and more
