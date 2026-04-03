# Bank Transactions

Orleans ACID transactions with atomic cross-grain transfers. Two bank accounts are created, funded, and a transfer is executed atomically -- both the debit and credit succeed or neither does.

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
--- Bank Transactions: ACID Transaction Demo ---

Alice deposits $1000 -> balance = $1000
Bob deposits $1000   -> balance = $1000

Atomic transfer: $500 from Alice to Bob...
  Alice balance: $500
  Bob balance:   $1500

Attempting transfer of $2000 from Alice to Bob (should fail)...
  Transaction rolled back: Insufficient funds: balance=500, requested=2000
  Alice balance (unchanged): $500
  Bob balance (unchanged):   $1500

Total across both accounts: $2000 (should be $2000)

Done. Shutting down...
```

## Key concepts

- **`ITransactionalState<T>`** Orleans transactional state with ACID guarantees
- **`TransactionalState.read` / `TransactionalState.update`** F# wrappers for transactional state access
- **`[Transaction(TransactionOption.Create)]`** starts a new transaction context (ATM grain)
- **`[TransactionalState]`** constructor injection of transactional state into account grain
- **`[Reentrant]`** required on transactional grains so Orleans can interleave calls within a transaction
- **Atomic cross-grain transfers** debit one account, credit another -- both or neither succeed
- **`UseTransactions()`** must be called on the silo builder to enable transaction support
- **Pure F# business logic** deposit/withdraw functions are testable without Orleans runtime
- **FsCheck property tests** verify transfer preserves total balance invariant

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
