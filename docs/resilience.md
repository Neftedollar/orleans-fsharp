# Grain Resilience

**Retry, circuit-breaker, and timeout strategies for Orleans grain calls, powered by Polly v8.**

> **Note.** The "Wrapping typed `FSharpGrain.ask` calls" section uses the deprecated
> `FSharpGrain.*` handle module, which now carries `[<Obsolete>]` (warning, not error). The
> resilience policies themselves are model-agnostic and wrap functional-runtime calls the same way;
> see [functional-grains.md](functional-grains.md).

## What you'll learn

- How to retry transient grain failures automatically
- What a timeout over a grain call enforces, and what it cannot cancel
- How to protect a downstream service with a circuit breaker
- How to compose multiple strategies into a single reusable pipeline

---

## Overview

`GrainResilience` is a thin F#-idiomatic wrapper around [Polly v8](https://github.com/App-vNext/Polly). It lets you wrap any grain call in a resilience pipeline without touching the grain implementation.

```fsharp
open Orleans.FSharp

// Retry a grain call up to 3 times with a 500 ms delay between attempts
let! result =
    GrainResilience.retry<string> 3 (TimeSpan.FromMilliseconds 500) (fun () ->
        grain.HandleMessage(FetchData id))
```

All helpers work with `Task<'T>`, keeping the code in your standard `task { }` expressions.

---

## Quickstart

```fsharp
open System
open Orleans.FSharp

// 1. Simple retry — 3 extra attempts with a 200 ms delay
let! inventory =
    GrainResilience.retry<int> 3 (TimeSpan.FromMilliseconds 200) (fun () ->
        inventoryGrain.HandleMessage(GetStock itemId))

// 2. Timeout — a deadline the caller can rely on: after 2 seconds this raises
//    TimeoutRejectedException and abandons the call
//    (see "What the deadline does and does not do" below)
let! price =
    GrainResilience.withTimeout<decimal> (TimeSpan.FromSeconds 2) (fun () ->
        pricingGrain.HandleMessage(GetPrice itemId))

// 3. Full options — timeout + circuit breaker + retry
let opts =
    { GrainResilience.defaultOptions with
        MaxRetryAttempts = 3
        RetryDelay = TimeSpan.FromMilliseconds 100
        Timeout = Some(TimeSpan.FromSeconds 5)
        CircuitBreakerThreshold = Some 5
        CircuitBreakerDuration = Some(TimeSpan.FromSeconds 30) }

let! order =
    GrainResilience.execute<OrderResult> opts (fun () ->
        orderGrain.HandleMessage(PlaceOrder cart))
```

---

## API Reference

### `ResilienceOptions` — configuration record

```fsharp
type ResilienceOptions =
    {
        /// Maximum number of retry attempts after the initial call. Default: 3
        MaxRetryAttempts: int

        /// Delay between retries. Default: 1 second
        RetryDelay: TimeSpan

        /// Open the circuit after this many consecutive failures. None = disabled.
        /// Circuit state lives in the pipeline object and `execute` builds a fresh one per
        /// call — see "Circuit state is per pipeline object" below.
        CircuitBreakerThreshold: int option

        /// How long the circuit stays open before attempting a probe call. None falls back
        /// to 30 s, and is only consulted when CircuitBreakerThreshold is set.
        CircuitBreakerDuration: TimeSpan option

        /// Deadline over the whole protected operation — every attempt plus the delays
        /// between them, not one attempt. None = no timeout.
        /// Read "What the deadline does and does not do" below before relying on it.
        Timeout: TimeSpan option
    }

let defaultOptions: ResilienceOptions =
    {
        MaxRetryAttempts = 3
        RetryDelay = TimeSpan.FromSeconds 1
        CircuitBreakerThreshold = None
        CircuitBreakerDuration = None
        Timeout = None
    }
```

**Two bounds Polly enforces**, and it enforces them by throwing
`System.ComponentModel.DataAnnotations.ValidationException` (Polly validates its options with
data annotations) when the pipeline is built — that is, from inside the `execute` / `withTimeout`
call itself, not at configuration time:

- `Timeout` must be between **10 ms and 24 hours**. `TimeSpan.Zero` is not "no timeout"; use
  `None` for that.
- `CircuitBreakerThreshold` must be **at least 2** (it maps to Polly's `MinimumThroughput`).
  `Some 1` and `Some 0` both throw.

### `GrainResilience.retry`

Retries the grain call up to `maxAttempts` further times after the initial one, so `retry 3` makes at most four calls.

```fsharp
val retry<'T>
    : maxAttempts : int
    -> delay      : TimeSpan
    -> f          : (unit -> Task<'T>)
    -> Task<'T>
```

```fsharp
let! count =
    GrainResilience.retry<int> 5 (TimeSpan.FromSeconds 1) (fun () ->
        counterGrain.HandleMessage(Increment))
```

### `GrainResilience.withTimeout`

Applies a Polly timeout strategy around the protected operation, with no retry and no circuit
breaker.

```fsharp
val withTimeout<'T>
    : timeout : TimeSpan
    -> f      : (unit -> Task<'T>)
    -> Task<'T>
```

```fsharp
try
    let! snapshot =
        GrainResilience.withTimeout<Snapshot> (TimeSpan.FromSeconds 3) (fun () ->
            snapshotGrain.HandleMessage(CreateSnapshot))
    processSnapshot snapshot
with :? Polly.Timeout.TimeoutRejectedException ->
    log.Warning("Snapshot timed out — skipping")
```

#### What the deadline does and does not do

**The deadline always fires for the caller.** When it passes, `withTimeout` raises
`TimeoutRejectedException` and stops waiting — measured, a 100 ms budget over an 800 ms call
raises at ~101 ms. (Before 4.0.1 it did not: that same call returned its result after ~810 ms and
raised nothing, because the pipeline was handed a task it could only await.)

**It does not cancel the call.** The protected operation is a `unit -> Task<'T>`, which takes no
`CancellationToken`, so nothing can interrupt the work itself. The in-flight call is *abandoned*:
it keeps running to completion on the silo, its effects land, and only its result is discarded.
A deadline here bounds how long you wait, not what the system does — cancellation without
rollback is this library's documented stance everywhere else too.

For an operation that can honour a token, use `withTimeoutCancellable` / `executeCancellable`
below: the deadline's token is handed to the operation, so it can stop rather than be abandoned.
Beyond that, the other real deadlines available are Orleans' own —
`SiloMessagingOptions.ResponseTimeout` / `ClientMessagingOptions.ResponseTimeout` (30 seconds by
default), and `FunctionalGrainRef.callCancellable` from the
[functional grain runtime](functional-grains.md), whose token reaches the handler through
`context.cancellationToken`.

### `GrainResilience.withTimeoutCancellable`

The same deadline, handed to an operation that takes the token.

```fsharp
val withTimeoutCancellable<'T>
    : timeout : TimeSpan
    -> f      : (CancellationToken -> Task<'T>)
    -> Task<'T>
```

```fsharp
let! rows =
    GrainResilience.withTimeoutCancellable<Row list> (TimeSpan.FromSeconds 3) (fun ct ->
        reportGrain.RunQuery(query, ct))
```

The token is cancelled when the deadline passes. An `OperationCanceledException` raised in
response is reported to the caller as `TimeoutRejectedException`, so both entry points fail the
same way. An operation that ignores the token is still abandoned exactly as under `withTimeout`,
so the deadline holds either way.

To honour a caller's own token as well, link it inside `f`:

```fsharp
GrainResilience.withTimeoutCancellable timeout (fun deadlineToken ->
    use linked = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken, context.cancellationToken)
    grain.RunQuery(query, linked.Token))
```

A cancel that arrives through your own token surfaces as `OperationCanceledException`, not as
`TimeoutRejectedException` — Polly only rebrands a cancellation it caused itself.

### `GrainResilience.execute`

Full-options entry point. Compose timeout, circuit breaker, and retry in one call.

```fsharp
val execute<'T>
    : options : ResilienceOptions
    -> f      : (unit -> Task<'T>)
    -> Task<'T>
```

```fsharp
let myOpts =
    { GrainResilience.defaultOptions with
        MaxRetryAttempts = 2
        Timeout = Some(TimeSpan.FromSeconds 10) }

let! result = GrainResilience.execute<string> myOpts (fun () -> grain.HandleMessage cmd)
```

Every attempt re-invokes `f`, so `MaxRetryAttempts = 2` calls it at most three times.

### `GrainResilience.executeCancellable`

`execute` for an operation that takes the deadline's token — the full-options counterpart of
`withTimeoutCancellable`.

```fsharp
val executeCancellable<'T>
    : options : ResilienceOptions
    -> f      : (CancellationToken -> Task<'T>)
    -> Task<'T>
```

```fsharp
let! result =
    GrainResilience.executeCancellable<Report> myOpts (fun ct ->
        reportGrain.Build(spec, ct))
```

### `GrainResilience.buildPipeline`

Creates a reusable `ResiliencePipeline<'T>` from options. Useful when you want to share a pipeline across many calls.

```fsharp
val buildPipeline<'T> : options : ResilienceOptions -> ResiliencePipeline<'T>
```

```fsharp
let pipeline = GrainResilience.buildPipeline<int> myOpts

// Reuse the same pipeline object many times
let! r1 = pipeline.ExecuteAsync(fun _ -> ValueTask<int>(grain1.HandleMessage cmd)).AsTask()
let! r2 = pipeline.ExecuteAsync(fun _ -> ValueTask<int>(grain2.HandleMessage cmd)).AsTask()
```

### `GrainResilience.circuitBreaker`

Creates a standalone, **non-generic** `ResiliencePipeline` backed only by a circuit breaker. Because the circuit-state is held inside the returned object, you should keep it as a long-lived value (e.g., a `let` binding at the service scope).

```fsharp
val circuitBreaker
    : threshold     : int
    -> breakDuration : TimeSpan
    -> ResiliencePipeline
```

```fsharp
// Open after 5 failures; stay open for 30 seconds
let private cb = GrainResilience.circuitBreaker 5 (TimeSpan.FromSeconds 30)

member _.CallExternalService(cmd) =
    task {
        try
            return! cb.ExecuteAsync(fun _ -> ValueTask<_>(grain.HandleMessage cmd)).AsTask()
        with :? Polly.CircuitBreaker.BrokenCircuitException ->
            return Error "Service unavailable"
    }
```

---

## Strategy composition order

When you use `execute` with multiple strategies enabled, they are layered **outer → inner**:

```
request
  → [Timeout]          ← outermost; cancels everything inside if deadline exceeded
    → [CircuitBreaker] ← trips on consecutive failures; short-circuits when open
      → [Retry]        ← innermost; retries on exceptions
        → grain call
```

This means:
- The timeout is a deadline over the whole attempt sequence, not over one attempt: it covers every
  attempt and every delay between them. One attempt that hangs therefore consumes the entire
  budget and the retries never happen — that is the deliberate trade of a total deadline, and it
  is the guarantee the caller can actually reason about (see
  [What the deadline does and does not do](#what-the-deadline-does-and-does-not-do)).
- For a *per-attempt* deadline, nest the two: `retry` on the outside, `withTimeout` inside the
  function it retries.

  ```fsharp
  GrainResilience.retry<int> 3 (TimeSpan.FromMilliseconds 200) (fun () ->
      GrainResilience.withTimeout<int> (TimeSpan.FromSeconds 1) (fun () ->
          grain.HandleMessage cmd))
  ```

- The circuit breaker sits between them, so it sees one outcome per execution — the retry
  strategy's final verdict, not each attempt.
- A single Polly `TimeoutRejectedException` or `BrokenCircuitException` bypasses the retry.

### Circuit state is per pipeline object

`execute` builds a fresh pipeline on every call. Circuit-breaker state lives *in* the pipeline
object, so a breaker configured through `ResilienceOptions` starts cold each time: five failing
`execute` calls with `CircuitBreakerThreshold = Some 2` all raise the underlying exception and the
circuit never opens. `MaxRetryAttempts` does not change that either — the breaker sees the retry
strategy's single final outcome, not each attempt.

For a breaker that actually trips, keep one pipeline and reuse it:

```fsharp
// Service-scoped: one object, one circuit
let private pipeline =
    GrainResilience.buildPipeline<int>
        { GrainResilience.defaultOptions with
            CircuitBreakerThreshold = Some 5
            CircuitBreakerDuration = Some(TimeSpan.FromSeconds 30) }

member _.Query(cmd) =
    pipeline.ExecuteAsync(fun _ -> ValueTask<_>(grain.HandleMessage cmd)).AsTask()
```

or use `GrainResilience.circuitBreaker`, which exists for exactly this and is non-generic.
Both are pinned by tests: `execute` does not share circuit state, a reused `buildPipeline` does.

---

## Patterns

### Retry transient connectivity errors

```fsharp
let! data =
    GrainResilience.retry<Data> 3 (TimeSpan.FromMilliseconds 200) (fun () ->
        dataGrain.HandleMessage(Fetch key))
```

### Bound the time spent retrying

```fsharp
// The timeout caps the retry sequence, including the delays between attempts.
let bounded =
    { GrainResilience.defaultOptions with
        MaxRetryAttempts = 5
        RetryDelay = TimeSpan.FromMilliseconds 500
        Timeout = Some(TimeSpan.FromSeconds 2) }

let! result = GrainResilience.execute<Result> bounded (fun () -> flakyGrain.HandleMessage query)
```

After two seconds the caller gets `TimeoutRejectedException` whatever the sequence is doing —
mid-attempt or mid-delay. What it does **not** do is stop the attempt that was in flight; see
[What the deadline does and does not do](#what-the-deadline-does-and-does-not-do).

### Protect a shared downstream resource

```fsharp
// Service-scoped — shared circuit state across all calls
let cb = GrainResilience.circuitBreaker 10 (TimeSpan.FromMinutes 1)

member _.Query(cmd) =
    cb.ExecuteAsync(fun _ -> ValueTask<_>(grain.HandleMessage cmd)).AsTask()
```

### Full production resilience

```fsharp
let productionOpts =
    { MaxRetryAttempts       = 3
      RetryDelay             = TimeSpan.FromMilliseconds 500
      CircuitBreakerThreshold = Some 10
      CircuitBreakerDuration  = Some(TimeSpan.FromSeconds 60)
      Timeout                = Some(TimeSpan.FromSeconds 15) }

let! response =
    GrainResilience.execute<ApiResponse> productionOpts (fun () ->
        apiGrain.HandleMessage(ApiRequest payload))
```

### Wrapping typed `FSharpGrain.ask` calls

```fsharp
let! price =
    GrainResilience.retry<decimal> 3 TimeSpan.Zero (fun () ->
        FSharpGrain.ask<PricingState, PricingCommand, decimal> (GetPrice itemId) pricingGrain)
```

---

## Testing resilience

In unit tests, you can drive failures by throwing exceptions inside a closure without touching any real grain:

```fsharp
let mutable attempts = 0

let! result =
    GrainResilience.retry<int> 3 TimeSpan.Zero (fun () ->
        task {
            attempts <- attempts + 1
            if attempts < 3 then failwith "transient"
            return 42
        })

test <@ result = 42 @>
test <@ attempts = 3 @>
```

To test a deadline, do not assert that it fired within some number of milliseconds — that is a
wall-clock budget, and a loaded machine will fail it eventually. Gate the protected call on a
`TaskCompletionSource` that the test never releases inside the timed region, so the call cannot
finish on its own, and assert *which* task completed first under a net so generous it carries no
information about speed:

```fsharp
let gate = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

let guarded =
    task {
        try
            let! _ = GrainResilience.withTimeout (TimeSpan.FromMilliseconds 50.0) (fun () -> gate.Task)
            return null :> exn
        with ex -> return ex
    }

let! finished = Task.WhenAny(guarded, Task.Delay(TimeSpan.FromSeconds 30.0))
test <@ Object.ReferenceEquals(finished, guarded) @>
let! ex = guarded
test <@ ex :? Polly.Timeout.TimeoutRejectedException @>
```

For integration tests with a real `TestCluster`, use a grain that tracks its own call count and fails on the first N calls — see `flakyGrain` (over `FlakyState` / `FlakyCommand`) in `tests/Orleans.FSharp.Integration/ClusterFixture.fs`, driven by `GrainResilienceIntegrationTests.fs`.

---

## Package dependency

`GrainResilience` is in the `Orleans.FSharp` core package. It adds a dependency on `Polly` 8.x, which is already bundled — no additional NuGet reference is required.
