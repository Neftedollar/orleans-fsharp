---
title: "State and Lifecycle"
description: "Ephemeral state, persistent facets, activation hooks, timers, reminders, and durable schemas."
---

# State and Lifecycle

**Choose state durability per actor and per state element.**

| Model | Use when |
|---|---|
| `defaultState` / `initialState` | State may disappear when the activation is collected |
| `stateFrom` / `usePersistentState` | Current state must survive deactivation and silo restart |
| `journaledGrainFor` | State must be rebuilt as a fold over durable events |

A persistent descriptor names both the logical state element and its Orleans provider:

```fsharp
type AccountState = { balance: decimal }

[<RequireQualifiedAccess>]
module AccountState =
    let empty = { balance = 0m }
    let deposit amount state = { state with balance = state.balance + amount }

let accountState =
    PersistentState.create<AccountState> "account" "Default"
    |> PersistentState.withCodec FunctionalPersistenceCodec.FSharpJson

let accountDefinition =
    grainFor AccountApi.contract {
        initialState (fun _ -> AccountState.empty)
        stateFrom accountState

        handle (_.deposit) (fun context state amount ->
            task {
                let next = AccountState.deposit amount state
                let storage = context.persistentState accountState
                storage.State <- next
                do! storage.WriteStateAsync()
                return next, next.balance
            })
    }
```

Use a unique state name for each element. Several persistent facets are independent Orleans writes,
not an implicit transaction; use Orleans transactions when atomicity spans elements or grains.

## Codec and schema choices

Codec precedence is element, then grain, then silo default. A codec chooses the stored payload
format. A `FunctionalSchema` chooses how historical versions become the current type. They solve
different problems.

See [Serialization](/orleans-fsharp/serialization/) before changing a format in a deployed application.
Changing direct binary state to an envelope can require an explicit migration even when both
formats deserialize the same F# type.

## Activation order

The runtime restores persistent state or replays the journal before application activation hooks.
Use hooks to validate restored values and establish activation-local resources; do not postpone an
important durable write until deactivation.

- `onActivate` runs after state is ready.
- `onLifecycle` targets an explicit Orleans lifecycle stage.
- `onDeactivate` is cleanup, not a durability guarantee.

## Timers and reminders

A timer belongs to one activation and disappears with it. A reminder is registered through the
Orleans reminder service and can reactivate the grain after collection or restart.

Use `onTimer` for in-memory periodic work and `onReminder` for durable scheduling. Renaming a
reminder does not remove the old registration; unregister the old name explicitly during
migration.

## Details

- [Persistence model](/orleans-fsharp/functional-grains/#persistence-model)
- [Lifecycle hooks](/orleans-fsharp/functional-grains/#activation-ordering-and-lifecycle-hooks)
- [Timers, reminders, and collection age](/orleans-fsharp/functional-grains/#timers-reminders-and-collection-age)
- [Versioned state and upcasters](/orleans-fsharp/serialization/#versioned-state-and-upcasters)
- [Journal state and snapshots](/orleans-fsharp/event-sourcing/)
