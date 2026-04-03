# Order Processing

DU state machine for order lifecycle management with reminders for timeout detection and timers for periodic status checks. Demonstrates the full `grain {}` feature set including `onReminder` and `onTimer`.

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
--- Order Processing: DU State Machine + Reminders + Timers ---

Place order:   Ok (Created ("Widget x10", ...))

Waiting for timer status check...
  [Timer] Status check #1: Created ("Widget x10", ...)

Confirm order: Ok (Confirmed ("Widget x10", ...))
Ship order:    Ok (Shipped ("Widget x10", ...))
Cancel (invalid): Rejected "Cannot cancel a shipped order"
Deliver order: Ok (Delivered ("Widget x10", ...))

Final status:  Ok (Delivered ("Widget x10", ...))

Waiting for reminder tick...
  [Reminder] Order timeout check #1

Done. Shutting down...
```

## Key concepts

- **DU state machine** models the order lifecycle as `Created | Confirmed | Shipped | Delivered | Cancelled`
- **`onTimer`** registers a periodic timer for status checks (fires during grain activation)
- **`onReminder`** registers a persistent reminder for timeout detection (survives restarts)
- **Invalid transitions** are rejected with descriptive error messages
- **FsCheck property tests** verify any command sequence always produces valid states

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
