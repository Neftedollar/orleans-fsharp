# Library Research: F#/.NET for Orleans.FSharp
**Date**: 2026-04-04

## Summary Table

| Category | Current | Status | Recommendation |
|----------|---------|--------|-----------------|
| Property Testing | FsCheck v3 | ✅ Optimal | Stay current (v3.3.2) |
| Validation/UMX | N/A | Recommended | Add FSharp.UMX v1.1.0 for ID type safety |
| Resilience | N/A | Recommended | Add Polly v8.x for grain call resilience |
| Type-Safe IDs | (Built-in DUs) | Partial | Add Orleans.TypedGrainKeys for complex keys |
| Async Utilities | TaskSeq v0.4.0 | ✅ Optimal | Stay current; optional IcedTasks |

---

## 1. Property-Based Testing: FsCheck v3.3.2 (No upgrade needed)

Already integrated — excellent fit. v3.3.2 is current; no v4 planned.
Good C# struct record support (v3.2.0+), deep xUnit integration.
**Action**: None.

---

## 2. FSharp.UMX v1.1.0 — Type-safe grain IDs

Zero-overhead type erasure for primitives: `string<GrainId>`, `Guid<TenantId>`.
Compile-time prevention of ID confusion (can't pass `string<CustomerId>` where `string<OrderId>` expected).
Supported base types: bool, byte, uint64, Guid, string, TimeSpan, DateTime, DateTimeOffset.

**Orleans concern**: Needs custom serialization/codec for grain boundary crossing.
**Best for**: Intra-process domain model safety.
**Action**: Medium priority — add as optional utility/pattern for users.

---

## 3. Polly v8.x — Grain call resilience

ResiliencePipeline model (replaces PolicyWrap from v7). Zero-allocation; native async/await; built-in telemetry.
Features: Circuit Breaker, Retry, Timeout, Hedging, Bulkhead.
Part of Microsoft.Extensions.Resilience (official integration).

**Orleans fit**: Excellent — wrap grain proxy calls with retry/circuit-breaker.
**F# concern**: C#-first API, would benefit from thin F# wrappers in a `GrainResilience` module.
**Action**: High priority — add `GrainResilience` module with F#-idiomatic pipeline helpers.

---

## 4. Orleans.TypedGrainKeys — Complex composite keys

Strongly-typed grain keys beyond string/int/Guid. Implicit operator integration with Orleans API.
Designed specifically for multi-tenant composite key scenarios.
GitHub: https://github.com/christiansparre/Orleans.TypedGrainKeys

**Action**: Medium priority — evaluate for complex key scenarios.

---

## 5. Async Utilities: Current stack is optimal

- **FSharp.Control.TaskSeq v0.4.0**: IAsyncEnumerable with taskSeq CE — keep as-is
- **IcedTasks v0.11.9**: ValueTask/CancellableTask CEs — add only if CancellationToken propagation needed

**Action**: Low priority.

---

## Implementation Priority for Orleans.FSharp

1. **High**: Polly v8 — `GrainResilience` module with F# pipeline helpers
2. **Medium**: FSharp.UMX — add to docs/testing as pattern example
3. **Medium**: Orleans.TypedGrainKeys — evaluate for grain key type safety
4. **Low**: IcedTasks — only if CancellationToken scenarios arise
5. **No Action**: FsCheck (optimal), TaskSeq (optimal)
