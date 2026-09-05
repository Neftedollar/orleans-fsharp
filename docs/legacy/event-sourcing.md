# Legacy Event Sourcing

> **Archived and unsupported.** This material is retained only to help migrate existing
> applications. There is no new Legacy release line, feature or compatibility work, or security
> fixes. New development must use the current functional API.

**Migration archive for the original `eventSourcedGrain { }` model.**

> This page is intentionally isolated from the current `journaledGrainFor` documentation. The
> classic package was part of the 4.1-and-earlier release shape and is not published in the 5.0
> package set.

## Classic event-sourced grain

> The historical `eventSourcedGrain { }` computation expression and
> `Orleans.FSharp.EventSourcing` built on Orleans' `JournaledGrain` through a generated C# class.
> They depended on the archived CodeGen source bridge. Neither project is published in the 5.0
> package set; new code must use `journaledGrainFor` from the functional runtime.

The classic model splits a grain into `apply` (a pure fold), `handle` (a command handler
returning events), and `defaultState`. The retained generator can emit a C# `JournaledGrain`
which delegates to them, but only for this historical event-sourced model; it is not a generator
for ordinary `GrainDefinition` values.

The following installation command is preserved to identify 4.1-and-earlier project files. It
does not install a 5.0 package:

```bash
dotnet add package Orleans.FSharp.EventSourcing
```

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

[<GenerateSerializer>]
type BankAccountCommand =
    | [<Id(0u)>] Deposit of amount: decimal
    | [<Id(1u)>] Withdraw of amount: decimal
    | [<Id(2u)>] GetBalance

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
                    TransactionCount = state.TransactionCount + 1 })

        handle (fun state cmd ->
            match cmd with
            | Deposit amount when amount > 0m -> [ Deposited amount ]
            | Withdraw amount when amount > 0m && state.Balance >= amount -> [ Withdrawn amount ]
            | GetBalance -> []          // no events -- this is a query
            | _ -> [])                  // reject invalid commands silently

        logConsistencyProvider "LogStorage"
    }
```

### Replaying and handling commands in-process

```fsharp
let finalState =
    EventSourcedGrainDefinition.foldEvents bankAccount
        { Balance = 0m; TransactionCount = 0 }
        [ Deposited 100m; Withdrawn 30m; Deposited 50m ]
// finalState = { Balance = 120m; TransactionCount = 3 }

let newState, events =
    EventSourcedGrainDefinition.handleCommand bankAccount
        { Balance = 100m; TransactionCount = 0 }
        (Withdraw 30m)
// newState = { Balance = 70m; TransactionCount = 1 }; events = [ Withdrawn 30m ]
```

### Testing with FsCheck

Both `apply` and `handle` are pure functions, so they test directly:

```fsharp
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Testing

let balanceInvariant state = state.Balance >= 0m

let applyCommand state cmd =
    let newState, _ = EventSourcedGrainDefinition.handleCommand bankAccount state cmd
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

### EventStore module

| Function | Description |
|---|---|
| `EventStore.processCommand def state cmd` | Produce events from a command |
| `EventStore.applyEvent def state event` | Apply a single event |
| `EventStore.replayEvents def state events` | Replay a list of events |

These are used internally by the generated C# `JournaledGrain` class.

### Clearing the event log

```fsharp
open Orleans.FSharp   // the FSharpEventSourcedGrain handle module lives here, not in
                      // Orleans.FSharp.EventSourcing, which holds the CE and its types

let handle = FSharpEventSourcedGrain.ref<BankAccountState, BankAccountCommand> grainFactory "acc-1"
do! handle |> FSharpEventSourcedGrain.clearLog
```

> **Provider-dependent.** This routes through Orleans' `JournaledGrain.ClearLogAsync`, which
> throws `NotSupportedException` for log-consistency providers that do not override
> `ClearPrimaryLogAsync`. Both built-in providers do; a custom one need not.

### Third-party event stores

There is no first-party adapter for an external event store (Marten, EventStoreDB, …), and the
placeholder `Orleans.FSharp.EventSourcing.Marten` package — whose helpers only forwarded to
Orleans' own `LogStorage` provider — has been removed. An adapter for any store is a separate
package registering a named `ILogViewAdaptorFactory`, which both the classic and the journaled
model then name with no further work — see
["Bringing your own provider"](../event-sourcing.md#bringing-your-own-provider).

---

## Current replacement

For new code, use [Event Sourcing with journaledGrainFor](../event-sourcing.md).
