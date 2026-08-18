# Bank Transactions

Orleans ACID transactions with atomic cross-grain transfers. Two bank accounts are created, funded,
and a transfer is executed atomically -- both the debit and the credit succeed, or neither does.
The live demo runs the functional grain runtime's twin (`AccountGrainFunctional.fs`): a `grainFor`
account with a `transactionalStateFrom` facet and per-operation
`transactional TransactionOption.CreateOrJoin`, plus a state-free `grainFor` orchestrator declaring
`transactional TransactionOption.Create`. `AccountGrain.fs` keeps the original
`FSharpTransactionalGrain` / `FSharpAtmGrain` version as reference -- see `Program.fs` for why it
cannot run standalone, and
[docs/functional-grains.md, "Distributed ACID transactions"](../../docs/functional-grains.md) for
the full model.

**The twin does not restate a single business rule.** It hands `AccountGrainDef.deposit` and
`AccountGrainDef.withdraw` straight to Orleans as `'State -> 'State` update functions, over the
same `AccountBalance` state, under the same `("state", "TransactionStore")` identity. So
`tests/Domain.Tests` -- which tests those two pure functions -- is parity evidence for both paths
at once, and the overdraft message a rolled-back transfer reports is character-for-character the
one the classic grain reported.

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
--- Bank Transactions (Functional Grain Runtime): ACID transfers on grainFor ---

Alice deposits $1000 -> balance = $1000
Bob deposits $1000   -> balance = $1000

Atomic transfer: $500 from Alice to Bob...
  Alice balance: $500
  Bob balance:   $1500

Attempting transfer of $2000 from Alice to Bob (overdraft, should fail)...
  Transaction rolled back: Insufficient funds: balance=500, requested=2000
  Alice balance (unchanged): $500
  Bob balance (unchanged):   $1500

Transferring $200 and then failing AFTER both accounts were written...
  Transaction rolled back: the orchestrator failed after both accounts had been written
  Alice balance (unchanged): $500 -- her withdrawal was undone
  Bob balance (unchanged):   $1500 -- his deposit was undone

Both balances in one transaction: Alice $500, Bob $1500
Total across both accounts: $2000 (should be $2000)

Done. Shutting down...
```

**Two aborts, on purpose.** The overdraft is the classic demo's abort and it is real -- but it
aborts *before* Bob's account is touched, so on its own it only shows that the transfer
short-circuited. The second one is the atomicity proof: `transferThenFail` completes the withdrawal
*and* the deposit and only then throws, so Orleans has two in-flight writes on two different grains
to undo. Neither balance moves. Nothing in the example catches or compensates; the rollback is
Orleans'.

## Parity: classic member → functional operation

| classic | functional | note |
|---|---|---|
| `ITransactionalAccountGrain.Deposit` | `AccountApi.deposit` | `transactional CreateOrJoin` |
| `ITransactionalAccountGrain.Withdraw` | `AccountApi.withdraw` | throws, and so aborts, exactly as before |
| `ITransactionalAccountGrain.GetBalance` | `AccountApi.balance` | `transactional CreateOrJoin` |
| `IAtmGrain.Transfer` | `AtmApi.transfer` | `transactional Create` |
| — | `AtmApi.totals` | both balances read in ONE transaction |
| — | `AtmApi.transferThenFail` | the atomicity control described above |
| `[<TransactionalState("state", "TransactionStore")>]` ctor injection | `transactionalStateFrom` + `context.transactionalState` | same names, same storage |
| `[<Transaction(TransactionOption.X)>]` on an interface method | `transactional TransactionOption.X (_.op)` on the contract | same Orleans policy, declared instead of attributed |
| `[<Reentrant>]` on the grain class | — | the functional runtime applies what a transactional definition needs |
| `CopyState: 'State -> 'State -> unit` | — | gone; see below |

**`CreateOrJoin`, not `Join`.** The classic demo calls `Deposit` / `GetBalance` straight from the
client *and* from inside the ATM's transaction. `CreateOrJoin` is exactly that: join the caller's
transaction when there is one, start one otherwise. `Join` would refuse the direct calls; `Create`
would give the transfer three separate transactions and lose atomicity.

**The disappearing `CopyState`.** Orleans applies a transactional update by mutating the instance
it stores, so the classic `TransactionalGrainDefinition` carried a fourth function whose only job
was to copy a freshly computed state's fields back into Orleans' instance -- hand-written per state
type and easy to leave behind when the type grows a field. The functional runtime keeps the
application's value inside its own box and hands application code a plain `'State -> 'State`,
performing the single reference assignment itself. That is also why an ordinary *immutable* F#
record works as transactional state on this runtime even though Orleans' own constraint is
`TState : class, new()`; this twin keeps the classic `AccountBalance` anyway, because reusing the
very same state type is what makes the parity checkable.

## Why the classic path is not the entry point

`Program.fs` keeps the original demo as a commented block, and the reason is not the deprecation:
F# assemblies carry none of Orleans' source-generated `[assembly: ApplicationPart]` /
`[assembly: TypeManifestProvider]` attributes (Roslyn generators never run on an F# project), so
`factory.GetGrain<ITransactionalAccountGrain>(...)` fails with *"Could not find an implementation
for interface ITransactionalAccountGrain"*, and this example never had a C# CodeGen bridge project
to fill that gap. That was verified by running it, not inferred -- the silo starts fine and the very
first `GetGrain` throws. The functional runtime needs no such bridge: the API record *is* the
surface. See
[docs/functional-grains.md, "Running a silo from a standalone F# process"](../../docs/functional-grains.md#running-a-silo-from-a-standalone-f-process).

Both classic definitions stay compiled, stay registered on the silo
(`AddFSharpTransactionalGrain` / `AddFSharpAtmGrain`), and stay under test.

## Key concepts

- **`transactional TransactionOption.X (_.op)`** the per-operation transaction policy, declared on the contract instead of attributed on an interface method
- **`TransactionalState.create<'S> name storage`** the facet descriptor: state name (durable identity, part of the `ParticipantId`) and the storage the transactional store resolves
- **`transactionalStateFrom descriptor initializer`** attaches the facet to a definition; the initializer is substituted on read and stores nothing, so a pure read never becomes a write
- **`context.transactionalState descriptor`** the invocation-bound facade: `read`, `readWith`, `update`, `updateWith` -- all synchronous update functions, because Orleans runs them inside its reader-writer lock and rejects re-entering the same state from inside one
- **A state-free participant** the ATM declares `transactional` and attaches no facet at all -- the shape every "unit of work" grain has
- **`UseTransactions()`** must be called on the silo builder; one call serves both authoring models
- **`addMemoryStorage "TransactionStore"`** memory storage really is a transactional store: `NamedTransactionalStateStorageFactory` falls back to a keyed `IGrainStorage` wrapped in `TransactionalStateStorageProviderWrapper`, ETags and all
- **Abort is Orleans'** a throw anywhere in the transaction aborts it; the example adds no retry, no compensation, and no catch on the write path
- **Pure F# business logic** deposit/withdraw are testable without the Orleans runtime, and are the *same* functions both grain models run
- **FsCheck property tests** verify the transfer preserves the total balance, plus the twin's transactional-state identity

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
