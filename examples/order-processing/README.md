# Order Processing

DU state machine for order lifecycle management with reminders for timeout detection and timers
for periodic status checks. The live demo runs the functional grain runtime's twin
(`OrderGrainFunctional.fs`): the same `OrderStatus` DU state machine, a pure `transition` function,
typed `Result<OrderStatus, OrderError>` replies, explicit `stateFrom` persistence, a declarative
`onTimer` status check, and a declarative `onReminder` timeout auto-cancel. `OrderGrain.fs` keeps
the original `grain {}` version (same domain, boxed `OrderResult` replies) as deprecated reference
-- see `Program.fs` for why it cannot run standalone and
[docs/functional-grains.md](../../docs/functional-grains.md) for the full migration guide.

**A second, unrelated standalone-hosting gap surfaced and was fixed in this example specifically.**
`addMemoryReminderService` reaches `Orleans.IReminderTableGrain`'s implementation
(`InMemoryReminderTable`, in `Orleans.Reminders.dll`) only through an F# hop, and Orleans only
manifests assemblies already loaded when it takes its startup snapshot. `SiloConfig.applyToHost`
force-loads the two assemblies `addMemoryStorage` and the F# surface itself need, but
`addMemoryReminderService` is not one of them, so the silo failed to start with `Could not find an
implementation for interface Orleans.IReminderTableGrain` until `Program.fs` added one more
force-load line before `applyToHost` runs -- exactly the pattern
[docs/functional-grains.md, "Running a silo from a standalone F# process"](../../docs/functional-grains.md#running-a-silo-from-a-standalone-f-process)
already documents for any assembly reached only through F#. None of the other five console/web
examples in this repo use `addMemoryReminderService`, so none of them hit this.

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
--- Order Processing (Functional Grain Runtime): DU State Machine + Timer + Reminder ---

Place order-001:   Ok (Created ("Widget x10", ...))

Waiting for timer status check...
  [Timer] Status check #1: Created ("Widget x10", ...)

Confirm order-001: Ok (Confirmed ("Widget x10", ...))
Ship order-001:    Ok (Shipped ("Widget x10", ...))
Deliver order-001: Ok (Delivered ("Widget x10", ...))
Final status order-001: Some (Delivered ("Widget x10", ...))

Place order-002:   Ok (Created ("Widget x5", ...))
Deliver order-002 (invalid -- skipped confirm+ship): Error (InvalidTransition ("Created", "Deliver"))

Place order-003:   Ok (Created ("Widget x2", ...))
Confirm order-003: Ok (Confirmed ("Widget x2", ...))
Cancel order-003:  Ok (Cancelled ("changed mind", ...))

Cancel order-004 (invalid -- already shipped): Error (InvalidTransition ("Shipped", "Cancel \"too late\""))

Reminder note: OrderTimeout is registered for real, on Orleans' actual 1-minute
reminder-period floor, and auto-cancels any order left Created for 30+ minutes.
Not waited for live here (that would be a 1-minute-plus demo) -- see
OrderGrainFunctional.fs and this example's README for the exact schedule.

Done. Shutting down...
```

**On the reminder's schedule, precisely.** `OrderTimeout` is declared with a 10-second due time and
a 1-minute period -- Orleans' actual `ReminderOptions.MinimumReminderPeriod` floor, which this
example does not override, and which the functional runtime validates at startup (so a shorter
period would have failed to start, not silently run faster). Each tick increments an in-memory
counter and prints `[Reminder] Order timeout check #N`; a tick that finds an order in the `Created`
state for more than 30 minutes cancels it (`Cancelled("Timed out", ...)`) and persists that
transition. The demo above does not wait a full minute to show a tick live (`LocalReminderService`
does start, visible in the host's own log output), but the reminder is genuinely registered and
would fire on schedule in a longer-running process.

## Key concepts

- **DU state machine** models the order lifecycle as `Created | Confirmed | Shipped | Delivered | Cancelled` (reused byte-for-byte from `OrderState.fs` by both the old and functional grains)
- **Pure `transition` function** (`OrderGrainFunctional.fs`) takes the clock as a parameter instead of reading it internally, so it stays a pure `state -> intent -> Result<...>` function callable from every write handler
- **`Result<OrderStatus, OrderError>`** typed replies replace the old grain's boxed `OrderResult` (`Ok | Rejected of string | NoOrder`); every rejected transition becomes one typed `InvalidTransition of from * attempted` case
- **`readOnly (_.status)`** the query operation never blocks on the write path
- **`stateFrom` + `PersistentState.create` + explicit `WriteStateAsync`** every successful transition (including a reminder-triggered auto-cancel) is persisted; rejections write nothing
- **`onTimer`** a declarative status-check timer, same 5s due / 10s period as the original
- **`onReminder`** a declarative timeout-detection reminder, registered for real on Orleans' actual 1-minute floor -- see the schedule note above
- **`collectionAge`** sets this definition's idle-deactivation threshold (30 minutes here); not a data TTL, only governs when an idle in-memory activation may be released
- **FsCheck property tests** (`tests/Domain.Tests`) verify any command sequence against the old grain's `transition` always produces valid states

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
