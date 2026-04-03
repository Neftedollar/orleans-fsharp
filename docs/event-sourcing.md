# Event Sourcing

**Guide to the `eventSourcedGrain { }` computation expression.**

## What you'll learn

- How to define event-sourced grains with pure functions
- The CQRS pattern: commands in, events out, state by folding
- How to replay events and test with FsCheck
- Log consistency providers

## Overview

Event-sourced grains separate state changes into a sequence of events. The `eventSourcedGrain { }` CE builds an `EventSourcedGrainDefinition<'State, 'Event, 'Command>` with three key functions:

1. **`apply`** -- a pure fold: `state -> event -> state`
2. **`handle`** -- a command handler: `state -> command -> event list`
3. **`defaultState`** -- the initial state before any events

The Orleans.FSharp.CodeGen package generates a C# `JournaledGrain` that delegates to these F# functions.

---

## Installation

```bash
dotnet add package Orleans.FSharp.EventSourcing
```

---

## Bank Account Example

### Define the types

```fsharp
open Orleans.FSharp.EventSourcing

[<GenerateSerializer>]
type BankAccountState =
    { Balance: decimal
      TransactionCount: int }

[<GenerateSerializer>]
type BankAccountEvent =
    | [<Id(0u)>] Deposited of amount: decimal
    | [<Id(1u)>] Withdrawn of amount: decimal
    | [<Id(2u)>] TransferSent of amount: decimal * toAccount: string
    | [<Id(3u)>] TransferReceived of amount: decimal * fromAccount: string

[<GenerateSerializer>]
type BankAccountCommand =
    | [<Id(0u)>] Deposit of amount: decimal
    | [<Id(1u)>] Withdraw of amount: decimal
    | [<Id(2u)>] Transfer of amount: decimal * toAccount: string
    | [<Id(3u)>] GetBalance
```

### Define the grain

```fsharp
let bankAccount =
    eventSourcedGrain {
        defaultState { Balance = 0m; TransactionCount = 0 }

        apply (fun state event ->
            match event with
            | Deposited amount ->
                { state with
                    Balance = state.Balance + amount
                    TransactionCount = state.TransactionCount + 1 }
            | Withdrawn amount ->
                { state with
                    Balance = state.Balance - amount
                    TransactionCount = state.TransactionCount + 1 }
            | TransferSent(amount, _) ->
                { state with
                    Balance = state.Balance - amount
                    TransactionCount = state.TransactionCount + 1 }
            | TransferReceived(amount, _) ->
                { state with
                    Balance = state.Balance + amount
                    TransactionCount = state.TransactionCount + 1 })

        handle (fun state cmd ->
            match cmd with
            | Deposit amount when amount > 0m ->
                [ Deposited amount ]
            | Withdraw amount when amount > 0m && state.Balance >= amount ->
                [ Withdrawn amount ]
            | Transfer(amount, toAccount) when amount > 0m && state.Balance >= amount ->
                [ TransferSent(amount, toAccount) ]
            | GetBalance ->
                []  // No events -- this is a query
            | _ ->
                [])  // Reject invalid commands silently

        logConsistencyProvider "LogStorage"
    }
```

Key points:

- **`apply` must be pure and deterministic.** No side effects, no I/O. The same event applied to the same state always produces the same result.
- **`handle` returns an empty list to reject a command** or signal a query (no state change).
- **`logConsistencyProvider`** names the Orleans log consistency provider. Common values: `"LogStorage"` (log-based), `"StateStorage"` (state snapshot-based).

---

## How it works

```
Command --> handle(state, cmd) --> Event list
                                       |
                                       v
                            apply(state, event) --> New State
                                       |
                                       v
                              Events persisted to log
```

1. A command arrives at the grain.
2. The `handle` function produces zero or more events.
3. Each event is applied to the current state via `apply`.
4. Events are persisted to the log consistency provider.
5. On recovery, all events are replayed through `apply` to rebuild state.

---

## Replaying Events

Use `EventSourcedGrainDefinition.foldEvents` to replay event history:

```fsharp
let events = [
    Deposited 100m
    Withdrawn 30m
    Deposited 50m
]

let finalState =
    EventSourcedGrainDefinition.foldEvents bankAccount
        { Balance = 0m; TransactionCount = 0 }
        events

// finalState = { Balance = 120m; TransactionCount = 3 }
```

---

## Handling Commands Programmatically

Use `EventSourcedGrainDefinition.handleCommand` to process a command and get both the events and the resulting state:

```fsharp
let currentState = { Balance = 100m; TransactionCount = 0 }
let command = Withdraw 30m

let newState, events =
    EventSourcedGrainDefinition.handleCommand bankAccount currentState command

// newState = { Balance = 70m; TransactionCount = 1 }
// events = [ Withdrawn 30m ]
```

---

## Testing with FsCheck

Event-sourced grains are highly testable because `apply` and `handle` are pure functions:

```fsharp
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Testing

let balanceInvariant state =
    state.Balance >= 0m

let applyCommand state cmd =
    let newState, _ =
        EventSourcedGrainDefinition.handleCommand bankAccount state cmd
    newState

[<Property>]
let ``balance is never negative for any command sequence`` () =
    let arb = GrainArbitrary.forCommands<BankAccountCommand>()
    Prop.forAll arb (fun commands ->
        FsCheckHelpers.stateMachineProperty
            { Balance = 0m; TransactionCount = 0 }
            applyCommand
            balanceInvariant
            commands)
```

You can also test `apply` in isolation:

```fsharp
[<Property>]
let ``deposits always increase balance`` (amount: decimal) =
    amount > 0m ==>
        lazy
            let state = { Balance = 50m; TransactionCount = 0 }
            let newState = bankAccount.Apply state (Deposited amount)
            newState.Balance = state.Balance + amount
```

---

## EventStore Module

The `EventStore` module provides lower-level functions for the C# CodeGen bridge:

| Function | Description |
|---|---|
| `EventStore.processCommand def state cmd` | Produce events from a command |
| `EventStore.applyEvent def state event` | Apply a single event |
| `EventStore.replayEvents def state events` | Replay a list of events |

These are used internally by the generated C# `JournaledGrain` class.

---

## Log Consistency Providers

Orleans provides several built-in log consistency providers:

| Provider | Description |
|---|---|
| `"LogStorage"` | Events stored in a log; state rebuilt by replay |
| `"StateStorage"` | Full state snapshot stored; events for recent changes |

If you omit `logConsistencyProvider`, the silo's default provider is used.

---

## Complete Example

```fsharp
open Orleans.FSharp.EventSourcing
open Orleans.FSharp.Runtime

// Types
[<GenerateSerializer>]
type InventoryState = { Items: Map<string, int> }

[<GenerateSerializer>]
type InventoryEvent =
    | [<Id(0u)>] ItemAdded of sku: string * qty: int
    | [<Id(1u)>] ItemRemoved of sku: string * qty: int

[<GenerateSerializer>]
type InventoryCommand =
    | [<Id(0u)>] AddStock of sku: string * qty: int
    | [<Id(1u)>] RemoveStock of sku: string * qty: int
    | [<Id(2u)>] CheckStock of sku: string

// Grain definition
let inventory =
    eventSourcedGrain {
        defaultState { Items = Map.empty }

        apply (fun state event ->
            match event with
            | ItemAdded(sku, qty) ->
                let current = state.Items |> Map.tryFind sku |> Option.defaultValue 0
                { state with Items = state.Items |> Map.add sku (current + qty) }
            | ItemRemoved(sku, qty) ->
                let current = state.Items |> Map.tryFind sku |> Option.defaultValue 0
                let newQty = max 0 (current - qty)
                { state with Items = state.Items |> Map.add sku newQty })

        handle (fun state cmd ->
            match cmd with
            | AddStock(sku, qty) when qty > 0 ->
                [ ItemAdded(sku, qty) ]
            | RemoveStock(sku, qty) when qty > 0 ->
                let available = state.Items |> Map.tryFind sku |> Option.defaultValue 0
                if available >= qty then [ ItemRemoved(sku, qty) ]
                else []
            | CheckStock _ -> []
            | _ -> [])

        logConsistencyProvider "LogStorage"
    }

// Silo configuration
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}
```

## Next steps

- [Grain Definition](grain-definition.md) -- standard `grain { }` CE for non-event-sourced grains
- [Testing](testing.md) -- property testing of event-sourced grains
- [Advanced](advanced.md) -- transactions, state migration, and more
