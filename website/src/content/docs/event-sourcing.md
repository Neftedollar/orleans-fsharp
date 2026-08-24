---
title: "Event Sourcing"
description: "journaledGrainFor: a functional grain whose state is the fold of an event journal."
---

# Event Sourcing

**Guide to `journaledGrainFor { }` — a grain whose state is the fold of an event journal.**

## What you'll learn

- How to define a journaled grain: `initialEventState`, `apply`, and handlers that raise events
- Which Orleans log-consistency provider to name, and what each one actually stores
- How binary or F# JSON journal payloads are selected and identified durably
- How typed CustomStorage snapshots are configured globally or per definition
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
open System
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
    grainContract<AccountActor, string, AccountApi> {
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
        journalCodec FunctionalPersistenceCodec.FSharpJson

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
let callAccount (grainFactory: Orleans.IGrainFactory) =
    task {
        let account = FunctionalGrain.ref accountContract grainFactory "acct-1"
        let! afterDeposit = account.deposit 100m
        let! balance = account.balance ()
        return afterDeposit, balance
    }
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
- with `OrleansBinary`, the state and event types are supported by the functional exact-type
  payload codec and are declared as top-level payload types. F# JSON does not require Orleans
  serializers for those application types; the selected `JsonSerializerOptions` must support
  them, and an incompatible converter/schema fails when a payload is encoded or decoded.

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

The built-in providers have no configurable snapshot operation. `StateStorage` writes the folded
view on *every* confirm, so it already behaves like a snapshot-only store and keeps no events.
`LogStorage` never compacts: its log grows without bound and every activation replays all of it.
Application-controlled snapshots are available through Orleans' `CustomStorage` provider below.

The adaptors also expose a **complete** clear (`ClearLogAsync`). The functional equivalent is
`context.clearJournal()`: it removes confirmed and unconfirmed events and restores the
`initialEventState` seed. It is a destructive reset, not truncation up to a selected version and
not snapshotting.

### Journal payload codec

`journalCodec` selects how the functional runtime encodes state and event payloads inside its
Orleans adaptor view and entries. A definition-level choice wins over
`FunctionalPersistenceOptions.DefaultJournalCodec`; the silo default is
`FunctionalPersistenceCodec.OrleansBinary`.

```fsharp
let accountDefinition =
    journaledGrainFor accountContract {
        initialEventState initialAccount
        apply applyAccount
        logProvider "LogStorage"
        journalCodec FunctionalPersistenceCodec.FSharpJson
        // handlers...
    }
```

Both `FunctionalJournalView` and `FunctionalJournalEntry` store the `CodecId` that wrote their
payload. A missing, empty, or whitespace id identifies a record from before codec selection and is
read as Orleans binary. New payloads use stable ids, so replay decodes each stored view or entry by
its own format instead of assuming the definition's current write format.

The one-argument `CreateFSharpJson(options)` compatibility overload uses `fsharp-json-v1`; the
options themselves are not stored. Prefer `CreateFSharpJson("account-json-v1", options)` for a
custom durable contract. When the write format changes, register the previous codec with
`currentCodec.WithReadCodec(previousCodec)` for as long as old payloads can be replayed. See
[Serialization](/orleans-fsharp/serialization/#custom-json-options-are-a-durable-contract).

### Custom storage and snapshots

`journaledGrainFor` implements Orleans'
`ICustomStorageInterface<FunctionalJournalView, FunctionalJournalEntry>` internally and exposes a
typed F# storage contract to application code:

```fsharp
open System.Threading.Tasks

type AccountJournalBackend =
    { read:
        FunctionalJournalStorageIdentity<string> ->
            Task<FunctionalJournalRead<Account, AccountEvent>>
      append:
        FunctionalJournalStorageIdentity<string> ->
            FunctionalJournalWrite<Account, AccountEvent> -> Task<bool>
      clear: FunctionalJournalStorageIdentity<string> -> Task }

type AccountJournalStore(backend: AccountJournalBackend) =
    interface IFunctionalJournalStorage<string, Account, AccountEvent> with
        member _.Read(identity) =
            backend.read identity

        member _.Append(identity, write) =
            backend.append identity write

        member _.Clear(identity) =
            backend.clear identity
```

The `journalCodec` governs the adaptor payloads which cross Orleans' journal boundary. It does
**not** prescribe the durable representation of `AccountJournalStore`. The runtime decodes adaptor
payloads before calling `IFunctionalJournalStorage` and gives the store typed states and events;
that implementation owns its database schema and may use JSON, binary columns, another serializer,
or no serializer at all. Provider-wide `FSharpJsonGrainStorageSerializer` likewise does not control
a custom `IFunctionalJournalStorage`, because that path does not write through `IGrainStorage`.

`Read` returns `FunctionalJournalRead<'State,'Event>`: `Snapshot = None` starts at
`initialEventState`, and `Events` is the ordered tail the runtime folds with the definition's one
authoritative `apply`. `Append` receives `FunctionalJournalWrite<'State,'Event>` and must obey the
same contract as Orleans' custom interface: compare-and-swap on `ExpectedVersion`, append the
batch atomically, and advance the durable version by `Events.Count`. A supplied snapshot has the
resulting version and state; the store may discard every event represented by it in that same
write. `FunctionalJournalStorageIdentity<'Key>` contains the grain type, complete `GrainId`, and
decoded key, so one singleton store can safely serve several grain types.

When Orleans' CustomStorage adaptor calls `Read` or appends events, it treats an ordinary exception
as transient and retries it. A zero-event manual snapshot and `Clear` call the typed store directly;
an ordinary exception from either direct path fails that call once, does not deactivate the grain,
and leaves an explicit retry to the caller. When retrying the same operation cannot succeed without
an application, configuration, or durable-data change, throw
`FunctionalJournalPermanentStorageException` instead. It has `message` and
`message, innerException` constructors. The functional runtime then exits the protocol retry loop,
fails the current journal operation, and requests deactivation; a later call creates a fresh
activation and reads durable state again. Use the permanent exception only for genuinely
non-retryable failures, such as an unsupported stored schema.

The permanent exception has the same fail-and-deactivate meaning on manual snapshot and `Clear`,
even though no Orleans retry loop is active there.

Bind it in the definition and register Orleans' provider under the same name:

```fsharp
open Microsoft.Extensions.DependencyInjection

let initialAccount key =
    { balance = 0m
      entries = [ $"opened:{key}" ] }

let applyAccount state event =
    match event with
    | Deposited amount -> { state with balance = state.balance + amount }
    | Withdrawn amount -> { state with balance = state.balance - amount }

let accountDefinition =
    journaledGrainFor accountContract {
        initialEventState initialAccount
        apply applyAccount
        logProvider "CustomStorage"

        customStorage (fun services ->
            services.GetRequiredService<AccountJournalStore>()
            :> IFunctionalJournalStorage<string, Account, AccountEvent>)

        snapshotPolicy (FunctionalJournalSnapshotPolicy.Every 1_000)
        // handlers...
    }

silo.Services.AddSingleton<AccountJournalStore>() |> ignore
silo.AddCustomStorageBasedLogConsistencyProvider "CustomStorage" |> ignore
silo.AddFunctionalJournaledGrain accountDefinition |> ignore
```

`snapshotPolicy` is `Inherit | Disabled | Every of int | When of (int -> 'State -> bool)`.
Without it, a custom-storage definition inherits the silo default:

```fsharp
open Orleans.Hosting

let configureSnapshots (silo: ISiloBuilder) =
    silo.UseFunctionalJournalSnapshots 5_000 |> ignore

    // Or a heterogeneous silo-wide predicate:
    silo.ConfigureFunctionalJournalSnapshots(fun options ->
        options.Policy <-
            FunctionalJournalSnapshotDefault.When(fun context ->
                context.Version >= 10_000)

        options.ManualSnapshotMaxConflictRetries <- 3)
```

`ManualSnapshotMaxConflictRetries` controls only compare-and-swap conflicts for a zero-event
manual snapshot requested with `context.snapshotNow()`. It must be non-negative and defaults to
`3`: the number counts retries *after* the first CAS attempt, so the default permits at most four
attempts. Each retry synchronizes with durable storage and recomputes the snapshot from confirmed
state. Set it to `0` to make only the first attempt; exhausting the limit fails the call without
writing the snapshot.

Precedence is explicit:

1. `context.snapshotNow()` forces a snapshot for the current successful callback;
2. the definition's `snapshotPolicy` overrides the silo default;
3. `Inherit` or no definition rule uses `FunctionalJournalSnapshotOptions.Policy`;
4. the default is `Disabled`.

A manual request includes events returned or explicitly submitted by that callback and is dropped
when the callback fails. It is rejected in read-only/state-neutral callbacks and on definitions
without `customStorage`. It is also rejected in the synchronous state/connection notification
hooks, which have no asynchronous completion at which storage could be awaited. `Every n` is
boundary-based: a batch from version 2 to 4 triggers `Every 3`, so multi-event batches cannot skip
a snapshot boundary. `journalStorage` must not be combined with `customStorage`; Orleans'
CustomStorage provider calls the typed interface directly and does not use `IGrainStorage`.
Both `When` predicates must be pure: a compare-and-swap conflict can make Orleans retry the same
candidate append and evaluate the predicate again.

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

**the fold may run more than once for the same event, and every run must agree.** The runtime
preflights the event before submission so a failing fold cannot poison the durable journal;
Orleans then updates tentative and confirmed views as required by the selected provider, and
`LogStorage` runs the fold again on every later activation that replays the journal — hours or
months later, in a different process, quite possibly on a different silo. A fold that read the
clock, generated an identifier, or called a service would produce different states for the same
event, and nothing could reconcile them.

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

## Journal API mapping to C# `JournaledGrain`

The functional grain drives the same Orleans `ILogViewAdaptor` directly instead of inheriting
`JournaledGrain<'State,'Event>`. Its API is therefore idiomatic F#, but the C# journal surface
members below have functional equivalents. This is a mapping of the supported surface, not a
completeness claim; provider-dependent and unsupported areas are listed later in this guide.

| C# `JournaledGrain` member | Functional F# equivalent |
|---|---|
| `State` | `context.journalState<'State>()` (handlers also receive this confirmed state as their `state` argument) |
| `TentativeState` | `context.journalTentativeState<'State>()` |
| `Version` | `context.journalVersion` |
| `UnconfirmedEvents` | `context.unconfirmedEvents<'Event>()` |
| `RaiseEvent` / `RaiseEvents` | `context.raiseEvent event` / `context.raiseEvents events` |
| `RaiseConditionalEvent` / `RaiseConditionalEvents` | `context.raiseConditionalEvent event` / `context.raiseConditional events` |
| `ConfirmEvents` | `context.confirmEvents()` |
| `RefreshNow` | `context.refreshJournal()` |
| `RetrieveConfirmedEvents` | `context.retrieveConfirmedEvents<'Event>(fromVersion, toVersion)` |
| `ClearLogAsync` | `context.clearJournal()` |
| `EnableStatsCollection` / `DisableStatsCollection` / `GetStats` | `context.enableJournalStats()` / `context.disableJournalStats()` / `context.getJournalStats()` |
| `TransitionState` | the definition's pure `apply` fold |
| `OnActivateAsync` | the runtime replays the journal first, then runs the definition's `onActivate` hook |
| `OnTentativeStateChanged` / `OnStateChanged` | `onTentativeStateChanged` / `onStateChanged` definition hooks |
| `OnConnectionIssue` / `OnConnectionIssueResolved` | hooks with the same names; they receive Orleans' exact `ConnectionIssue` |
| `InstallAdaptor` / `DefaultAdaptorFactory` | `logProvider`, optional `journalStorage`, and the matching silo registration |
| protected `LogViewAdaptor` and explicit `ILogViewAdaptorHost` members | owned by the functional runtime; `apply` folds adaptor updates and the state hooks observe them |
| explicit `ILogConsistencyProtocolParticipant` members | the runtime's replay/activation/deactivation lifecycle bridge |
| explicit `IConnectionIssueListener` members | the same `onConnectionIssue` / `onConnectionIssueResolved` hooks above |

The normal path stays simpler: return `events, reply` from a handler and the runtime submits the
batch and confirms it before replying. The explicit submit/confirm API is for workflows which
need to inspect tentative state:

```fsharp
handle (_.stage) (fun context _ event ->
    task {
        context.raiseEvent event

        let tentative = context.journalTentativeState<Account>()
        let pending = context.unconfirmedEvents<AccountEvent>()

        do! context.confirmEvents ()
        return [], (tentative, pending.Length)
    })
```

`raiseEvent` and `raiseEvents` only submit; they do **not** become durable until
`confirmEvents` (or another Orleans synchronization) succeeds. Events returned by the same
handler are a separate automatically confirmed batch, so a handler which uses explicit submission
should explicitly confirm it.

`refreshJournal` has the same semantics as C# `RefreshNow`: it confirms every locally submitted
event and synchronizes the confirmed view with the latest global journal. It is not a read-only
reload.

`raiseConditional` and `raiseConditionalEvent` append at the journal's current confirmed
position and confirm inside the turn. They can return `false` only when another interleaved turn
moves that position first.

`retrieveConfirmedEvents` preserves the provider's capabilities: `LogStorage` can return the
half-open confirmed segment `[fromVersion, toVersion)`; `StateStorage` keeps only the folded
view and reports that retrieval is unsupported. `clearJournal` is supported by both built-in
providers and restores the declared initial state.

The four notification hooks are synchronous, just like their C# counterparts. Keep them short and
non-blocking. A connection hook may adjust `ConnectionIssue.RetryDelay`; storage retry and
resolution remain Orleans' responsibility. Connection hooks receive the current confirmed view;
an event whose storage write is being retried is still visible only in the tentative view.

```fsharp
open System
open Microsoft.Extensions.Logging

onTentativeStateChanged (fun context state ->
    context.logger.LogInformation("Tentative balance: {Balance}", state.balance))

onStateChanged (fun context state ->
    context.logger.LogInformation("Confirmed balance: {Balance}", state.balance))

onConnectionIssue (fun context state issue ->
    issue.RetryDelay <- TimeSpan.Zero
    context.logger.LogWarning("Journal connection issue at balance {Balance}", state.balance))

onConnectionIssueResolved (fun context state _issue ->
    context.logger.LogInformation("Journal connection restored at balance {Balance}", state.balance))
```

Both the journal members and hooks refuse use on an ordinary `grainFor` definition, which has no
journal.

---


## Which `grainFor` operations carry over

| Operation | On a journaled definition | Why |
|---|---|---|
| `handle` | **yes**, with the events-and-reply shape | the whole point |
| `handleQuery` | **yes**, on a `readOnly` operation | reply-only sugar over `handle`; it raises nothing, which is already the rule a `readOnly` operation is held to, so the declaration is required and anywhere else it would turn a write into a silent no-op |
| `handleStream` | **yes**, without events | a server-streaming enumeration spans many activation turns, so it reads the confirmed state captured when enumeration starts and cannot append |
| `onActivate` / `onDeactivate` | **yes**, returning `unit` | the journal is the state, so there is nothing to replace; `onActivate` runs after the replay has completed |
| `onTimer` / `onReminder` | **yes**, returning `Task<'Event list>` | each successful tick appends and confirms its event batch; an interleaving timer is supported because the adaptor serializes submissions |
| `onStream` / `onBroadcast` | **yes**, returning `Task<'Event list>` | a successful delivery appends and confirms before Orleans observes completion |
| journal state/connection notifications | **yes** | `onTentativeStateChanged`, `onStateChanged`, `onConnectionIssue`, and `onConnectionIssueResolved` map to the C# callbacks |
| `collectionAge` | **yes** | activation lifetime is orthogonal to the journal |
| `placement` | **yes** | placement is orthogonal; the journal is addressed by grain identity |
| `statelessWorker` | **no** | many activations of one grain identity, each with its own log-view adaptor over the same journal, racing each other's appends through the adaptor's e-tag retry loop |
| `defaultState` / `initialState` | **no** | replaced by `initialEventState` |
| `stateFrom`, `usePersistentState` | **no** | a second durable holder on the same activation is a second source of truth, with no ordering against the journal |
| `transactionalStateFrom`, `transactional` | **no** | an Orleans log-view adaptor is not a transaction participant: it registers nothing with the transaction manager and has no prepare or abort, so events confirmed inside a transaction would survive its abort |

The delivery hooks have the same event-producing shape as a command handler:

```fsharp
onTimer
    "interest"
    (Orleans.Runtime.GrainTimerCreationOptions(
        DueTime = TimeSpan.Zero,
        Period = TimeSpan.FromHours 1.0,
        Interleave = true))
    (fun _context _state -> task { return [ Deposited 1m ] })

onReminder "daily" TimeSpan.Zero (TimeSpan.FromDays 1.0) (fun _context _state _tick ->
    task { return [ Deposited 2m ] })

onStream "Streams" "bank.deposits" (fun _context _state (amount: decimal) ->
    task { return [ Deposited amount ] })

onBroadcast "Channels" "bank.adjustments" (fun _context _state (amount: decimal) ->
    task { return [ Deposited amount ] })
```

Anything declared on the **contract** — `version`, `acceptsVersions`, `sinceVersion`, `readOnly`,
`oneWay`, `reentrant`, `mayInterleave`, the key mapping — works unchanged, with two rules the
runtime enforces:

- `transactional` is refused at sealing (the row above).
- A `readOnly` operation that raises events is refused at dispatch.
- A mutating `alwaysInterleave` operation is supported on a journaled definition: Orleans' adaptor
  serializes concurrent event submissions. The same contract is refused by an ordinary
  whole-state `grainFor` definition, whose concurrent replacements could overwrite one another.

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

- **No configurable snapshots on the built-in providers, and no public selective-truncate
  operation.** `LogStorage` retains its whole log; `StateStorage` retains only its latest folded
  view. A `customStorage` implementation can atomically compact to the snapshot supplied by the
  runtime. `clearJournal` remains the only destructive lifecycle operation exposed to a handler.
- **No cross-cluster replication.** Orleans 10's log-consistency machinery still declares a
  multi-cluster protocol gateway, but nothing constructs or calls it, and
  `ILogConsistencyProtocolServices` carries no message-sending member at all. A journal is
  single-cluster.
- **No event upcasting.** An event is serialized with the definition's exact declared event type
  through the selected journal codec. Binary union data is positional; JSON compatibility depends
  on the stable `JsonSerializerOptions` contract described above. There is no hook that sees an old
  event and returns a new one. Keep old event cases foldable and migrate incompatible schema
  changes explicitly.
- **No transactions.** See the table above.
- **Event-history reads are provider-dependent.** `retrieveConfirmedEvents` works with
  `LogStorage`; `StateStorage` has discarded the entries, and Orleans' CustomStorage adaptor does
  not expose the retained tail through that API. Query custom storage directly for application
  history when it keeps one.
- **No exactly-once command semantics.** Confirmation is per turn and durable, but a caller that
  times out cannot tell "not written" from "written, reply lost". Make commands idempotent.
- **No ordering guarantee across grains.** Each grain's journal is its own; there is no global
  sequence.

---

## Legacy model

The original event-sourcing API is retained in [Legacy Event Sourcing](/orleans-fsharp/legacy/event-sourcing/).

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
