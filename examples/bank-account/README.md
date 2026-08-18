# Bank Account

Event-sourced bank account: deposits, withdrawals with overdraft protection, and an inter-account
transfer -- all backed by an immutable event log. The live demo runs the functional grain runtime's
twin (`AccountGrainFunctional.fs`): a `journaledGrainFor` definition over the same `AccountEvent`
journal and the same `AccountState` view, seeded with `initialEventState`, folded by `apply`, with
handlers that return `events, reply` and typed refusals instead of a silently empty event list.
`AccountGrain.fs` keeps the original `eventSourcedGrain {}` version as reference -- see `Program.fs`
for why it cannot run standalone, [docs/event-sourcing.md](../../docs/event-sourcing.md) for the
journaled model, and [docs/functional-grains.md](../../docs/functional-grains.md) for the rest of
the functional runtime.

**The twin does not restate a single business rule.** Its `apply` *is*
`AccountGrainDef.applyEvent`, passed as a function value, and every write handler runs
`AccountGrainDef.handleCommand` to decide. So `tests/Domain.Tests` -- which tests those two pure
functions -- is parity evidence for both paths at once, and a change to the classic rules cannot
leave the twin behind.

## How to run

```bash
dotnet run --project src/Silo
```

## Run tests

```bash
dotnet test tests/Domain.Tests
```

## Expected output

```
--- Bank Account (Functional Grain Runtime): journaledGrainFor + AccountEvent ---

  [activation] 'alice' replayed to journal version 0
Alice deposits $1000 -> Ok 1000M
Alice deposits $500  -> Ok 1500M
  [activation] 'bob' replayed to journal version 0
Bob deposits $200    -> Ok 200M

Transfer $300 from Alice to Bob (two journals, not one transaction)...
  Alice after withdrawal: Ok 1200M
  Bob after deposit:      Ok 500M

Alice tries to withdraw $5000 (overdraft): Error (InsufficientFunds (1200M, 5000M))
Alice tries to deposit $0:                 Error (NonPositiveAmount 0M)

Journal versions (confirmed events): Alice = 3, Bob = 2
  -- neither refusal above is in a journal.

Ending Alice's activation (context.deactivateOnIdle), then reading again...
  [activation] 'alice' replayed to journal version 3
  Alice balance after replay: $1200 (from journal version 3, nothing was written)

Final balances:
  Alice: $1200
  Bob:   $500

Done. Shutting down...
```

The two `[activation]` lines are the demo's own proof, printed from the definition's `onActivate`
hook. The last one is the point: after `recycle` ends Alice's activation, the next call rebuilds
her `$1200` by replaying three events through the very same `applyEvent` -- nothing wrote a
balance anywhere.

## Parity: classic command → functional operation

| classic `AccountCommand` | functional operation | reply |
|---|---|---|
| `Deposit amount` | `deposit: decimal -> Task<Result<decimal, AccountRefusal>>` | `Ok newBalance` / `Error refusal` |
| `Withdraw amount` | `withdraw: decimal -> Task<Result<decimal, AccountRefusal>>` | `Ok newBalance` / `Error refusal` |
| `GetBalance` | `balance: unit -> Task<decimal>` (`readOnly`) | the folded balance |
| — | `journalVersion: unit -> Task<int>` (`readOnly`) | events confirmed so far |
| — | `recycle: unit -> Task<unit>` | ends the activation, so the next call replays |

And the three things the definition kind itself changes:

| | `eventSourcedGrain {}` | `journaledGrainFor {}` |
|---|---|---|
| initial state | `defaultState (AccountState())` | `initialEventState (fun key -> ...)` |
| a handler returns | an event list | `events, reply` |
| the provider name | `logConsistencyProvider "LogStorage"` | `logProvider "LogStorage"` |

Two shape differences are the reason the twin exists:

- **Refusals are typed instead of silent.** `AccountGrainDef.handleCommand` answers an overdraft, a
  zero deposit and a negative withdrawal with the same empty list; the caller sees an unchanged
  balance and has to guess why. The twin calls that same function and names the reason
  (`InsufficientFunds` / `NonPositiveAmount`). The journal is identical either way -- a refused
  command still raises nothing and still performs no storage write at all.
- **Replies are typed instead of boxed.** `IBankAccountGrain.HandleCommand` is
  `AccountCommand -> Task<obj>` returning the boxed `AccountState`; each operation has its own
  argument and its own reply type, checked at the call site.

`refusalFor` is the one thing the twin derives on its own rather than delegating, so it is the one
thing that could drift. `tests/Domain.Tests` pins it against `AccountGrainDef.handleCommand`
itself: for any state and any amount, whenever the classic handler refuses, the named reason has to
be the true one.

## Why the classic path is not the entry point

`Program.fs` keeps the original demo as a commented block, and the reason is not the deprecation:
F# assemblies carry none of Orleans' source-generated `[assembly: ApplicationPart]` /
`[assembly: TypeManifestProvider]` attributes (Roslyn generators never run on an F# project), so
`factory.GetGrain<IBankAccountGrain>(...)` fails with *"Could not find an implementation for
interface IBankAccountGrain"*, and this example never had a C# CodeGen bridge project to fill that
gap. That was verified by running it, not inferred -- the silo starts fine and the very first
`GrainRef.ofString` throws. The functional runtime needs no such bridge: the API record *is* the
surface. See
[docs/functional-grains.md, "Running a silo from a standalone F# process"](../../docs/functional-grains.md#running-a-silo-from-a-standalone-f-process).

The classic definition itself stays compiled, stays registered on the silo
(`AddFSharpEventSourcedGrain`), and stays under test.

## Key concepts

- **`journaledGrainFor {}`** a definition whose state is the fold of an event journal -- no `stateFrom`, no state writes, only events
- **`initialEventState`** seeds per grain key (the classic `defaultState` was one shared value)
- **`apply`** the pure fold, `'State -> 'Event -> 'State`; here it is literally `AccountGrainDef.applyEvent`
- **`events, reply`** every handler returns both; an empty event list means *no storage write at all*
- **`logProvider "LogStorage"`** names Orleans' log-consistency provider that stores the whole event log and replays it on every activation (`AddLogStorageBasedLogConsistencyProvider`); `journalStorage` is omitted, so the silo's default `IGrainStorage` holds it
- **`context.journalVersion`** the confirmed length of the journal, observed as it was when the turn started
- **`readOnly`** on `balance` / `journalVersion` -- queries never take the write path
- **Confirmation** a reply means the provider has confirmed the events; there is no "will be written shortly" window (see [docs/event-sourcing.md](../../docs/event-sourcing.md), "Confirmation: what a reply means")
- **Not a transaction** two accounts are two journals; the transfer above is two independent commands. See the [bank-transactions](../bank-transactions/README.md) example for the atomic version
- **FsCheck property tests** verify "balance is never negative", event replay equivalence, and the twin's refusal mapping

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
