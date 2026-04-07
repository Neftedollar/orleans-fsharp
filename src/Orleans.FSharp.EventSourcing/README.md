# Orleans.FSharp.EventSourcing

Declarative event sourcing for Orleans grains in F#.

## What it does

Provides the `eventSourcedGrain {}` computation expression for defining event-sourced grains with pure `apply` and `handle` functions. Bridges to Orleans `JournaledGrain` for durable event storage.

## Quick example

```fsharp
open Orleans.FSharp.EventSourcing

type State   = { Balance: decimal }
type Event   = Deposited of decimal | Withdrawn of decimal
type Command = Credit of decimal    | Debit of decimal

let bankAccount = eventSourcedGrain {
    defaultState { Balance = 0m }
    apply (fun state event ->
        match event with
        | Deposited amount -> { state with Balance = state.Balance + amount }
        | Withdrawn amount -> { state with Balance = state.Balance - amount })
    handle (fun state cmd ->
        match cmd with
        | Credit amount -> [ Deposited amount ]
        | Debit amount when state.Balance >= amount -> [ Withdrawn amount ]
        | Debit _ -> [])
    logConsistencyProvider "LogStorage"
}
```

## CE keywords

| Keyword | Required | Description |
|---------|----------|-------------|
| `defaultState` | yes | Initial grain state (before any events) |
| `apply` | yes | Pure function: `state -> event -> state` |
| `handle` | yes | Command handler: `state -> command -> event list` |
| `logConsistencyProvider` | no | Orleans provider name, e.g. `"LogStorage"`, `"StateStorage"`, `"CustomStorage"` |
| `snapshot` | no | Snapshot strategy: `Never`, `Every n`, `Condition pred` |
| `customStorage` | no | Custom storage callbacks for `CustomStorageBasedLogConsistencyProvider` |

## Custom storage (`ICustomStorageInterface`)

Use `customStorage` to plug in your own event store (e.g. EventStoreDB, Marten in append-only mode, or any database).
Pair it with `logConsistencyProvider "CustomStorage"`.

```fsharp
let myGrain = eventSourcedGrain {
    defaultState MyState.empty
    apply applyEvent
    handle handleCommand
    logConsistencyProvider "CustomStorage"
    customStorage
        (fun () -> task {
            // Load current version and state from your store
            let! version, state = myStore.ReadAsync(grainId)
            return version, state
        })
        (fun events expectedVersion -> task {
            // Append events with optimistic concurrency
            return! myStore.AppendAsync(events, expectedVersion)
            // return true on success, false on concurrency conflict
        })
}
```

The `customStorage` operation takes two functions:
- **read** `unit -> Task<int * 'State>` — loads the current (version, state)
- **write** `'Event list -> int -> Task<bool>` — appends events; returns `true` on success, `false` on version conflict

The generated C# stub automatically implements `ICustomStorageInterface<WrappedEventSourcedState, WrappedEventSourcedEvent>` and the `[LogConsistencyProvider(ProviderName = "CustomStorage")]` attribute is emitted on the class.

## Key modules

| Module | Description |
|--------|-------------|
| `EventSourcedGrainBuilder` | The `eventSourcedGrain {}` CE |
| `EventSourcedGrainDefinition` | `foldEvents` and `handleCommand` helpers for replaying and testing |
| `EventStore` | `applyEvent`, `replayEvents`, `processCommand`, `shouldSnapshot` |

## Dependencies

- `Microsoft.Orleans.EventSourcing`
- `Orleans.FSharp`
- `Orleans.FSharp.Runtime`

## License

MIT
