# Code Review — 2026-04-04

## 1. GrainDiscovery.fs — `primaryKeyOpt` extraction (lines 437–482)

### Bug: silent swallow of exceptions
`with _ -> None` at line 482 silences all exceptions, including
`OutOfMemoryException` or `StackOverflowException`. Use a tighter catch:
```fsharp
with :? System.InvalidOperationException -> None
```

### Edge case: compound keys (`IGrainWithGuidCompoundKey` / `IGrainWithIntegerCompoundKey`)
The branch logic checks only the three simple-key interfaces. Grains that implement
a compound-key interface will fall through to the heuristic, which discards the
string extension entirely. This could silently produce a wrong `PrimaryKey` for
compound-key grains that happen to match the GUID heuristic.

### Minor: mutable variables re-declared in every branch
`guidKey`, `guidExt`, `intKey`, `intExt` are independently declared in each
`if` arm. Extracting them to a small helper avoids repetition and makes the
pattern clearer — no behavioural impact, but reduces noise.

### Not a bug, but worth noting
The `IGrainWithStringKey` branch formats GUID-encoded keys with `"N"` format.
This is documented in the comment, but callers consuming `PrimaryKey` via
`GrainContext.primaryKeyString` need to be aware of this normalisation; a key
originally registered as `"6ba7b810-9dad-11d1-80b4-00c04fd430c8"` will come
back as `"6ba7b8109dad11d180b400c04fd430c8"`. A test covering hyphenated
GUID strings would pin this behaviour.

---

## 2. GrainLifecycleTests.fs — `GrainGuidKeyIntegrationTests` / `GrainIntKeyIntegrationTests`

### Hard-coded key ranges risk cross-test pollution
`GrainIntKeyIntegrationTests` uses keys `50001L–50003L` with a comment noting
they must not overlap with `UniversalGrainPatternTests`. This is a fragile
convention. Consider using `Random.Shared.NextInt64()` at test start (as is done
for GUID tests with `Guid.NewGuid()`).

### Missing: `int64.MinValue` and `int64.MaxValue` boundary tests
The negative-key test (`-50001L`) is good, but extreme values are not covered.
Orleans uses `GrainIdKeyExtensions.TryGetIntegerKey` which parses a 64-bit
value; boundary behaviour should be verified.

### Missing: GUID all-zeros / all-ones tests for `GrainGuidKeyIntegrationTests`
`Guid.Empty` is a valid grain key but never tested. Orleans encodes it
differently from a random GUID in some storage providers.

### XML docs
`GrainGuidKeyIntegrationTests` and `GrainIntKeyIntegrationTests` both have
`<summary>` blocks. `GrainLifecycleTests` and `DuplicateRegistrationTests` do
not. Inconsistent, but not critical for test classes.

---

## 3. EventSourcingTests.fs — EventStore property tests (lines 239–364)

### Tautological property: `replayEvents with only GetBalance commands`
The test at line 299 never actually applies any commands — it just calls
`replayEvents` with an empty list regardless of the `balances` parameter.
The `balances` argument is unused. The test passes vacuously and gives no
additional coverage beyond the `replayEvents with empty list is identity` fact.
Either use `balances` to drive initial state, or remove the property.

### `processCommand is deterministic` only tests one command
The determinism property at line 325 hard-codes `Credit 100m`. A
stronger version would use an `Arbitrary<BankAccountCommand>` parameter so
determinism is checked across the whole command space.

### Missing: `Debit` when balance equals amount exactly
`handleCommand` uses `state.Balance >= amount`, so `Debit` with
`amount = balance` should succeed. No unit test covers this exact-boundary
case; only "sufficient" (balance > amount) and "insufficient" are tested.

### `Credit then fold produces correct balance` — arithmetic edge case
`abs amount % 1_000_000m + 0.01m` can generate very large `initial` values
(`abs initial` is unbounded), so `Balance + amount` could overflow `decimal`
in theory. Adding `abs initial % 1_000_000m` would make the property safer.

### Idiomatic nit: redundant lambda in `replayEvents is equivalent to List.fold`
```fsharp
// current
events |> List.fold (fun s e -> EventStore.applyEvent bankAccountDef s e) initial
// simpler
events |> List.fold (EventStore.applyEvent bankAccountDef) initial
```

---

## Summary

| File | Severity | Count |
|---|---|---|
| GrainDiscovery.fs | Bug (silent catch) + 1 edge case | 2 |
| GrainLifecycleTests.fs | Test fragility + 2 missing edge cases | 3 |
| EventSourcingTests.fs | Tautological property + 3 gaps/nits | 4 |

Highest-priority fix: tighten the `with _ -> None` catch in `GrainDiscovery.fs`
to avoid masking serious runtime exceptions. Second priority: fix the unused-
parameter tautology in `EventSourcingTests.fs`.
