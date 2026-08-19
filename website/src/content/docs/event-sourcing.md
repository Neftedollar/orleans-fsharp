---
title: "Event Sourcing"
description: "journaledGrainFor: a functional grain whose state is the fold of an event journal."
---

# Event Sourcing

**Guide to `journaledGrainFor { }` — a grain whose state is the fold of an event journal.**

## What you'll learn

- How to define a journaled grain: `initialEventState`, `apply`, and handlers that raise events
- Which Orleans log-consistency provider to name, and what each one actually stores
- Exactly when events become durable, and what a caller can conclude from a reply
- Which `grainFor` operations carry over to a journaled definition, and why the rest do not
- What this model does **not** give you

---

## Overview

A journaled definition is a second definition kind over the same contract layer as
[`grainFor`](/orleans-fsharp/functional-grains/). Three things change, and nothing else:

| | `grainFor` | `journaledGrainFor` |
|---|---|---|
| initial state | `defaultState` / `initialState` | `initialEventState` |
| what a handler returns | `state', reply` | `events, reply` |
| where the state comes from | memory, or a `stateFrom` holder | the fold of the journal |

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type AccountActor = private AccountActor of unit

type Account = { balance: decimal; entries: string list }

type AccountEvent =
    | Deposited of decimal
    | Withdrawn of decimal

[<NoEquality; NoComparison>]
type AccountApi =
    { deposit: decimal -> Task<decimal>
      withdraw: decimal -> Task<bool>
      balance: unit -> Task<decimal> }

let accountContract =
    grainContract<AccountActor, string, AccountApi> () {
        grainType "bank.account"
        version 1
        stringKey
        readOnly (_.balance)
    }

let accountDefinition =
    journaledGrainFor accountContract {
        initialEventState (fun key -> { balance = 0m; entries = [ $"opened:{key}" ] })

        apply (fun state event ->
            match event with
            | Deposited amount ->
                { state with
                    balance = state.balance + amount
                    entries = state.entries @ [ $"+{amount}" ] }
            | Withdrawn amount ->
                { state with
                    balance = state.balance - amount
                    entries = state.entries @ [ $"-{amount}" ] })

        logProvider "LogStorage"
        journalStorage "Journals"

        handle (_.deposit) (fun _ state amount ->
            task { return [ Deposited amount ], state.balance + amount })

        handle (_.withdraw) (fun _ state amount ->
            task {
                if state.balance < amount then
                    return [], false            // a refused command raises nothing
                else
                    return [ Withdrawn amount ], true
            })

        handle (_.balance) (fun _ state () -> task { return [], state.balance })
    }
```

Calling it is exactly the same as calling any other functional grain — the definition kind is
invisible to a caller:

```fsharp
let account = FunctionalGrain.ref accountContract grainFactory "acct-1"

let! afterDeposit = account.deposit 100m
let! balance = account.balance ()
```

`initialEventState` and `apply` are the first two operations, in that order, and both are
required: the first introduces the state type and the second the event type, so every later
operation is typed against both.

---

## Hosting

```fsharp
silo.AddMemoryGrainStorage "Journals" |> ignore
silo.AddLogStorageBasedLogConsistencyProvider "LogStorage" |> ignore
silo.AddFunctionalJournaledGrain accountDefinition |> ignore
```

Silo startup validates, before the silo admits any traffic, that:

- the name given to `logProvider` resolves to a registered `ILogViewAdaptorFactory`;
- the silo has the `Factory<IGrainContext, ILogConsistencyProtocolServices>` Orleans' adaptors
  need (every stock `Add*BasedLogConsistencyProvider` call registers it);
- the name given to `journalStorage` — or the silo's default `IGrainStorage`, when the
  operation is omitted — resolves, whenever the provider writes through storage;
- the state type and the event type both have an Orleans serializer, and both are declared as
  top-level payload types.

`logProvider` is **required**. The two built-in providers store completely different things
under the same key and cannot read each other's records, so defaulting one silently would make
an irreversible storage decision invisible in the definition.

---

## Which provider, and what it stores

| | `AddLogStorageBasedLogConsistencyProvider` | `AddStateStorageBasedLogConsistencyProvider` |
|---|---|---|
| what is written | the **whole event log**, rewritten on every confirm | the **folded view** plus the log position |
| activation cost | replays every event through `apply` | reads one record; nothing is replayed |
| write cost | grows with the length of the journal | constant in the length of the journal |
| the event history | kept, and readable | **not kept** — only the latest view survives |

Choose `LogStorage` when the history itself is the point (audit, projections rebuilt from
events, temporal queries). Choose `StateStorage` when you want the event-sourced *authoring*
model — commands producing events producing state — without paying to keep or replay history.

**Neither one truncates or snapshots.** There is no `snapshotEvery`, and its absence is
deliberate rather than an omission: Orleans' `ILogViewAdaptor` surface has no snapshot or
truncate operation at all, so no implementation could be honest on the built-in providers. On
`StateStorage` the view is written on *every* confirm, which is what "snapshot every event"
would mean anyway; on `LogStorage` the log grows without bound and every activation replays all
of it. Keep a `LogStorage` journal short, or bring a provider that snapshots.

The one log-lifecycle operation the adaptors do expose is a **complete** clear
(`ClearLogAsync`), which is all-or-nothing and is not exposed on the journaled definition
surface.

---

## Confirmation: what a reply means

**Guaranteed.** The runtime appends a handler's returned events as one atomic batch and waits
for the log-consistency provider to confirm them, and it does that **after the handler has
returned and before the reply leaves the activation**. So:

- A caller that received a reply is looking at state the provider has confirmed. There is no
  "it will be written shortly" window a caller can observe.
- A handler observes the journal as it was when the turn started. `context.journalVersion` is
  the pre-turn version even for a handler that is about to raise three events, and the `state`
  it was handed is the confirmed fold at that point — never a tentative one.
- A handler that returns an empty event list performs **no storage write at all**. A query, or
  a command the handler refused, leaves no trace and does not move the version.
- Events raised by one handler are appended together. A later replay can never observe half of
  them.
- A handler that **throws** appends nothing: the events never leave its return value.
- A **one-way** operation appends and confirms in its own turn like any other, but its caller
  completed at the local acknowledgement, so it learns nothing about the outcome — including
  whether the append happened at all.
- `raiseConditional` confirms *inside* the turn, so a handler that calls it and then reads its own
  `state` argument is reading the pre-turn state, not the state its conditional append produced.

### When the confirm does not succeed

The confirmation goes through Orleans' own adaptor, and its failure behaviour is Orleans', not
this library's:

- A storage failure does **not** fail the call. `PrimaryBasedLogViewAdaptor` records the issue,
  retries, and keeps retrying — `UpdatePrimary` loops while the write reports no progress and
  never throws to the caller. The turn therefore *blocks* rather than throwing, and what the
  caller eventually observes is its own request timeout while the activation is still retrying.
  The retry is fast at first and only then backs off: `PrimaryOperationFailed.ComputeRetryDelay`
  returns `TimeSpan.Zero` for the first failure, then roughly 7–22 ms, then 19–56 ms, growing
  x1.5 up to a 10-second slow-poll interval. So a brief storage blip costs milliseconds, and a
  sustained outage settles into a 10-second poll.
- If the confirm does eventually succeed, the events are in the journal even though the caller
  saw a timeout. **A timed-out call is not a rolled-back call.** Make commands idempotent, or
  carry a de-duplication key in the event, if a caller may retry.
  (Both halves are tested rather than inferred: a fault-injecting storage provider refuses the
  first three writes, the call does not fault, the storage records exactly four write attempts,
  and a later activation replays the events —
  `tests/Orleans.FSharp.Integration/FunctionalPhaseEIntegrationTests.fs`, "a storage failure
  during confirmation blocks the turn and then completes".)
- An `IConnectionIssueListener` warning is logged for every such issue and an informational line
  when it resolves, so a stuck journal is visible in the silo log rather than silent.

### `apply` failures

A fold that throws is caught by Orleans' adaptors, logged, and skipped — the view simply does
not advance, and the *event stays in the journal*, so every later activation replays it and
skips it again. The runtime does not leave that in place. It folds the events over the confirmed
state **before** anything is submitted, so a failing `apply` fails the call with nothing
appended, and it re-raises a fold failure observed inside the adaptor rather than serving a state
that is not the fold of its own journal.

---

## `apply` must be pure

`apply` is `'State -> 'Event -> 'State`. It receives no invocation context, no grain factory, no
service provider, no cancellation token, and no key — so it cannot call another grain, read
storage, start a timer, or observe the clock through anything the runtime hands it. That shape
is the API making impurity hard, and it is load-bearing:

**the fold runs twice for the same event, at two different times, and both runs must agree.** It
runs once when the event is raised, to move this activation's view forward, and again on every
later activation that replays the journal — hours or months later, in a different process, quite
possibly on a different silo. A fold that read the clock, generated an identifier, or called a
service would produce a different state on replay than the one the application saw when the event
was raised, and nothing would report the difference.

Put the impure part in the **handler**, which may do anything a `grainFor` handler may do, and
have it put the result *into the event*:

```fsharp
// Wrong: the identifier changes on every replay.
apply (fun state (Deposited amount) ->
    { state with entries = state.entries @ [ $"{Guid.NewGuid()}" ] })

// Right: the handler decides once, the event carries the decision.
handle (_.deposit) (fun context state amount ->
    task { return [ Deposited(amount, Guid.NewGuid(), context.utcNow) ], () })
```

---

## The context surface

A journaled definition's handlers get the ordinary invocation context plus two members:

```fsharp
handle (_.audit) (fun context state () ->
    task { return [], context.journalVersion })         // the confirmed length of the journal
```

```fsharp
handle (_.reserve) (fun context state amount ->
    task {
        let! accepted = context.raiseConditional [ Reserved amount ]
        return [], accepted
    })
```

`raiseConditional` appends at the journal's current confirmed position **only** and reports
whether it was accepted. It can only ever answer `false` when something else can write this
grain's journal between the handler's read and its append — and with a non-reentrant definition
on a single cluster nothing can, because the activation is the sole writer and Orleans does not
interleave its turns. It becomes meaningful for a `reentrant` or `mayInterleave` contract, where
a second turn of the same activation can append in between.

Both members raise a definition-stage diagnostic on an ordinary `grainFor` definition, which has
no journal.

---

## Which `grainFor` operations carry over

| Operation | On a journaled definition | Why |
|---|---|---|
| `handle` | **yes**, with the events-and-reply shape | the whole point |
| `onActivate` / `onDeactivate` | **yes**, returning `unit` | the journal is the state, so there is nothing to replace; `onActivate` runs after the replay has completed |
| `collectionAge` | **yes** | activation lifetime is orthogonal to the journal |
| `placement` | **yes** | placement is orthogonal; the journal is addressed by grain identity |
| `statelessWorker` | **no** | many activations of one grain identity, each with its own log-view adaptor over the same journal, racing each other's appends through the adaptor's e-tag retry loop |
| `defaultState` / `initialState` | **no** | replaced by `initialEventState` |
| `stateFrom`, `usePersistentState` | **no** | a second durable holder on the same activation is a second source of truth, with no ordering against the journal |
| `transactionalStateFrom`, `transactional` | **no** | an Orleans log-view adaptor is not a transaction participant: it registers nothing with the transaction manager and has no prepare or abort, so events confirmed inside a transaction would survive its abort |
| `onStream`, `onBroadcast`, `onTimer`, `onReminder` | **no** | every one of them is a whole-state-replacement hook, which a journaled definition has no way to honour |

Anything declared on the **contract** — `version`, `acceptsVersions`, `sinceVersion`, `readOnly`,
`oneWay`, `reentrant`, `mayInterleave`, the key mapping — works unchanged, with two rules the
runtime enforces:

- `transactional` is refused at sealing (the row above).
- A `readOnly` or `alwaysInterleave` operation that raises events is refused at dispatch. Such an
  operation may run while another turn is in flight, so its appends could not be ordered against
  that turn's; dropping the events silently would be worse, because the handler believed it had
  changed the grain.

A journaled definition always requires an explicit `grainType` on its contract: the grain type
name is part of the storage key of the journal, so a brand rename would orphan every stored
event rather than a single record.

---

## Calling from C#

Nothing about the definition kind reaches the interop boundary — the facade is built from the
**contract**, which a journaled definition shares with an ordinary one:

```csharp
public interface IAccountFacade
{
    Task<decimal> Deposit(decimal amount);
    Task<decimal> Balance();
}

var account = FunctionalGrainInterop.For<IAccountFacade>(Contracts.Account, client, "acct-1");
await account.Deposit(100m);
```

See [calling-from-csharp.md](/orleans-fsharp/calling-from-csharp/).

---

## What this does NOT give you

- **No snapshotting or log truncation.** Covered above: the surface does not exist on the
  providers, so `snapshotEvery` does not exist here.
- **No cross-cluster replication.** Orleans 10's log-consistency machinery still declares a
  multi-cluster protocol gateway, but nothing constructs or calls it, and
  `ILogConsistencyProtocolServices` carries no message-sending member at all. A journal is
  single-cluster.
- **No event upcasting.** An event is serialized with the definition's exact declared event type
  through the F# binary codec, whose union format is positional. Adding a case at the **end** of
  a union is safe; reordering cases, or changing the fields of an existing case, is not — old
  entries decode into the new shape by position. There is no hook that sees an old event and
  returns a new one. Until there is, evolve an event type by adding cases and keeping the old
  ones foldable.
- **No transactions.** See the table above.
- **No read of the raw event history.** The journal is exposed as the fold and the version, not
  as a queryable log. `LogStorage` keeps the entries, so a projection can be built against the
  storage provider directly, but that is outside this API.
- **No exactly-once command semantics.** Confirmation is per turn and durable, but a caller that
  times out cannot tell "not written" from "written, reply lost". Make commands idempotent.
- **No ordering guarantee across grains.** Each grain's journal is its own; there is no global
  sequence.

---

## Classic path (superseded)

> **Superseded, not deprecated.** The `eventSourcedGrain { }` computation expression and
> `Orleans.FSharp.EventSourcing` build on Orleans' `JournaledGrain` through a generated C# class,
> and they need C# CodeGen for the grain interface. New code should use `journaledGrainFor` from
> the functional grain runtime (above, and [functional-grains.md](/orleans-fsharp/functional-grains/)). Nothing
> in `Orleans.FSharp.EventSourcing` carries `[<Obsolete>]`, so this path compiles without a
> warning; it is still shipped and is not being removed.

The classic model splits a grain into `apply` (a pure fold), `handle` (a command handler
returning events), and `defaultState`, and the `Orleans.FSharp.CodeGen` package generates a C#
`JournaledGrain` that delegates to them.

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
["Bringing your own provider"](#bringing-your-own-provider).

---

## Bringing your own provider

A log-consistency provider is an `ILogViewAdaptorFactory` registered as a **keyed** service under
a name. Both models resolve it by that name and nothing else, so a third-party adapter package
composes with `journaledGrainFor` without any functional-specific work:

```fsharp
siloBuilder.Services.AddKeyedSingleton<ILogViewAdaptorFactory>(
    "MyProvider",
    Func<IServiceProvider, obj, ILogViewAdaptorFactory>(fun _ _ -> MyProvider() :> _))
|> ignore
```

One constraint applies, and silo startup checks it: Orleans' adaptors are handed an
`ILogConsistencyProtocolServices` built by a `Factory<IGrainContext, ILogConsistencyProtocolServices>`
that only `AddLogConsistencyProtocolServicesFactory` registers — and that method is **internal to
Orleans**. A provider registered entirely by hand therefore has to ride along with one stock
`Add*BasedLogConsistencyProvider` call, which registers the factory as a side effect.

## Next steps

- [Functional Grains](/orleans-fsharp/functional-grains/) -- the `grainContract` / `grainFor` model this builds on
- [Calling from C#](/orleans-fsharp/calling-from-csharp/) -- the facade over a journaled contract
- [Testing](/orleans-fsharp/testing/) -- property testing of folds and handlers
- [Advanced](/orleans-fsharp/advanced/) -- transactions, state migration, and more
