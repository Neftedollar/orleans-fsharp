---
title: Testing
description: Guide to testing Orleans.FSharp grains — TestHarness, GrainMock, GrainArbitrary, FsCheck property tests, and log capture
---

**Guide to testing Orleans.FSharp grains.**

## What you'll learn

- How to use TestHarness for integration tests
- How to use GrainMock for isolated unit tests
- How to write property tests with GrainArbitrary and FsCheck
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

## TestHarness

`TestHarness` wraps an Orleans `TestCluster` with integrated log capture. It provides a real silo for integration tests.

### Create a test cluster

```fsharp
open Orleans.FSharp.Testing

let! harness = TestHarness.createTestCluster()
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

let! harness = TestHarness.createTestClusterWith config
```

### Get grain references

```fsharp
let counterRef =
    TestHarness.getGrainByString<ICounterGrain> harness "test-counter"

let orderRef =
    TestHarness.getGrainByInt64<IOrderGrain> harness 42L

let sessionRef =
    TestHarness.getGrainByGuid<ISessionGrain> harness (Guid.NewGuid())
```

### Make grain calls

```fsharp
let! result = GrainRef.invoke counterRef (fun g -> g.Increment())
Assert.Equal(1, result)

let! value = GrainRef.invoke counterRef (fun g -> g.GetValue())
Assert.Equal(1, value)
```

### Capture logs

```fsharp
let logs = TestHarness.captureLogs harness

for entry in logs do
    printfn "[%A] %s" entry.Level entry.Template

// Assert on specific log entries
let warnings =
    logs |> List.filter (fun e -> e.Level = LogLevel.Warning)
Assert.Empty(warnings)
```

### Reset and dispose

```fsharp
// Clear captured logs between tests
do! TestHarness.reset harness

// Dispose after all tests
do! TestHarness.dispose harness
```

### Full integration test

```fsharp
open Xunit
open Orleans.FSharp
open Orleans.FSharp.Testing

type CounterTests() =
    let mutable harness = Unchecked.defaultof<TestHarness>

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task { harness <- TestHarness.createTestCluster().GetAwaiter().GetResult() }

        member _.DisposeAsync() =
            TestHarness.dispose harness

    [<Fact>]
    member _.``increment increases counter`` () =
        task {
            let ref = TestHarness.getGrainByString<ICounterGrain> harness "test-1"
            let! v1 = GrainRef.invoke ref (fun g -> g.Increment())
            let! v2 = GrainRef.invoke ref (fun g -> g.Increment())
            Assert.Equal(1, v1)
            Assert.Equal(2, v2)
        }
```

---

## GrainMock

`GrainMock` provides a mock `IGrainFactory` for unit testing grain interactions without starting a real silo.

### Create a mock factory

```fsharp
open Orleans.FSharp.Testing

let factory =
    GrainMock.create()
    |> GrainMock.withGrain<ICounterGrain> "counter-1" mockCounterImpl
    |> GrainMock.withGrain<IOrderGrain> "order-42" mockOrderImpl
```

### Use in a GrainContext

```fsharp
let ctx : GrainContext =
    {
        GrainFactory = factory :> IGrainFactory
        ServiceProvider = serviceProvider
        States = Map.empty
        DeactivateOnIdle = None
        DelayDeactivation = None
        GrainId = None
        PrimaryKey = None
    }

// Now test a handleWithContext handler
let handler = GrainDefinition.getContextHandler myGrainDefinition
let! newState, result = handler ctx initialState myCommand
```

### Register mocks for different key types

```fsharp
let factory =
    GrainMock.create()
    |> GrainMock.withGrain<ISessionGrain> (Guid.Parse("...")) mockSession
    |> GrainMock.withGrain<IOrderGrain> 42L mockOrder
    |> GrainMock.withGrain<IChatGrain> "room-1" mockChat
```

The key is converted to a string internally for lookup matching.

---

## GrainArbitrary

`GrainArbitrary` auto-generates FsCheck `Arbitrary` instances for F# discriminated unions by inspecting the DU structure at runtime. No manual generator code needed.

### Generate state values

```fsharp
open Orleans.FSharp.Testing

let arb = GrainArbitrary.forState<CounterState>()

// Use in a property test
let gen = arb |> Arb.toGen
let sample = gen |> Gen.sample 10 5
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

## Testing Event-Sourced Grains

Event-sourced grains are especially testable because `apply` and `handle` are pure functions:

```fsharp
open Orleans.FSharp.EventSourcing

[<Fact>]
let ``deposit produces Deposited event`` () =
    let state = { Balance = 100m; TransactionCount = 0 }
    let events = bankAccount.Handle state (Deposit 50m)
    Assert.Equal([ Deposited 50m ], events)

[<Fact>]
let ``apply Deposited increases balance`` () =
    let state = { Balance = 100m; TransactionCount = 0 }
    let newState = bankAccount.Apply state (Deposited 50m)
    Assert.Equal(150m, newState.Balance)

[<Property>]
let ``replaying events produces the same state as fold`` () =
    let arb = GrainArbitrary.forCommands<BankAccountCommand>()
    Prop.forAll arb (fun commands ->
        let mutable state = { Balance = 0m; TransactionCount = 0 }
        let mutable allEvents = []
        for cmd in commands do
            let events = bankAccount.Handle state cmd
            allEvents <- allEvents @ events
            state <- events |> List.fold bankAccount.Apply state

        let replayed =
            EventSourcedGrainDefinition.foldEvents bankAccount
                { Balance = 0m; TransactionCount = 0 }
                allEvents

        state = replayed)
```

---

## Testing Handlers Directly

You can test grain handlers without a silo by calling the definition directly:

```fsharp
let handler = GrainDefinition.getHandler counter

let! newState, result = handler Zero Increment
Assert.Equal(Count 1, newState)
Assert.Equal(box 1, result)
```

For context-aware handlers:

```fsharp
let handler = GrainDefinition.getContextHandler myGrain

let ctx = { ... }  // Build a GrainContext with mocks
let! newState, result = handler ctx initialState myCommand
```

---

## Complete Test Suite Example

```fsharp
open Xunit
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp
open Orleans.FSharp.Testing

module CounterTests =

    // Unit test: handler logic
    [<Fact>]
    let ``increment from zero produces Count 1`` () =
        task {
            let handler = GrainDefinition.getHandler counter
            let! state, result = handler Zero Increment
            Assert.Equal(Count 1, state)
            Assert.Equal(box 1, result)
        }

    // Unit test: get value
    [<Fact>]
    let ``get value returns current count`` () =
        task {
            let handler = GrainDefinition.getHandler counter
            let! state, result = handler (Count 5) GetValue
            Assert.Equal(Count 5, state)
            Assert.Equal(box 5, result)
        }

    // Property test: invariant
    [<Property>]
    let ``counter is never negative`` () =
        let arb = GrainArbitrary.forCommands<CounterCommand>()
        Prop.forAll arb (fun commands ->
            let applySync state cmd =
                match state, cmd with
                | Zero, Increment -> Count 1
                | Zero, Decrement -> Zero
                | Count n, Increment -> Count(n + 1)
                | Count n, Decrement when n > 1 -> Count(n - 1)
                | Count _, Decrement -> Zero
                | s, GetValue -> s

            FsCheckHelpers.stateMachineProperty Zero applySync
                (fun s -> match s with Zero -> true | Count n -> n > 0)
                commands)

    // Property test: round-trip
    [<Property>]
    let ``state round-trips through all command sequences`` () =
        let arb = GrainArbitrary.forState<CounterState>()
        Prop.forAll arb (fun state ->
            match state with
            | Zero -> true
            | Count n -> n <> 0)
```

## Next steps

- [Grain Definition](/orleans-fsharp/guides/grain-definition/) -- the grain definitions you are testing
- [Event Sourcing](/orleans-fsharp/guides/event-sourcing/) -- testing event-sourced grains
- [Advanced](/orleans-fsharp/guides/advanced/) -- transactions, serialization, and more
