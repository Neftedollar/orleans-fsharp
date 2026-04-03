# Orleans.FSharp.EventSourcing

Declarative event sourcing for Orleans grains in F#.

## What it does

Provides the `eventSourcedGrain {}` computation expression for defining event-sourced grains with pure `apply` and `handle` functions. Bridges to Orleans `JournaledGrain` for durable event storage and includes Marten configuration helpers.

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

## Key modules

| Module | Description |
|--------|-------------|
| `EventSourcedGrainBuilder` | The `eventSourcedGrain {}` CE with `defaultState`, `apply`, `handle`, `logConsistencyProvider` |
| `EventSourcedGrainDefinition` | `foldEvents` and `handleCommand` helpers for replaying and testing |
| `EventStore` | Event store abstraction for the journaled grain bridge |
| `MartenConfig` | Marten (PostgreSQL document DB) configuration helpers |

## Dependencies

- `Microsoft.Orleans.EventSourcing`
- `Marten 8.x`
- `Orleans.FSharp`
- `Orleans.FSharp.Runtime`

## License

MIT
