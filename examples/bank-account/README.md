# Bank Account

Event-sourced bank account using the `eventSourcedGrain {}` computation expression. Demonstrates deposits, withdrawals with overdraft protection, and inter-account transfers -- all backed by an immutable event log.

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
--- Bank Account: Event Sourcing Demo ---

Alice deposits $1000 -> balance = $1000
Alice deposits $500  -> balance = $1500
Bob deposits $200    -> balance = $200

Transfer $300 from Alice to Bob...
  Alice after withdrawal: $1200
  Bob after deposit:      $500

Alice tries to withdraw $5000 (overdraft): balance unchanged = $1200

Final balances:
  Alice: $1200
  Bob:   $500

Done. Shutting down...
```

## Key concepts

- **`eventSourcedGrain {}`** computation expression for event-sourced grain definitions
- **Pure `apply` function** folds events into state deterministically
- **Command handler** produces events (empty list = rejected command)
- **`JournaledGrain`** C# base class bridges to Orleans event sourcing runtime
- **FsCheck property tests** verify invariants like "balance is never negative"
- **Event replay** produces identical state to direct command processing

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
