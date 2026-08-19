# Grain Resilience

**Retry, circuit-breaker, and timeout strategies for Orleans grain calls, powered by Polly v8.**

> **Note.** The "Wrapping typed `FSharpGrain.ask` calls" section uses the deprecated
> `FSharpGrain.*` handle module, which now carries `[<Obsolete>]` (warning, not error). The
> resilience policies themselves are model-agnostic and wrap functional-runtime calls the same way;
> see [functional-grains.md](functional-grains.md).

## What you'll learn

- How to retry transient grain failures automatically
- What a Polly timeout over a grain call can and cannot cancel
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

// 2. Timeout — caps the pipeline's own waiting, not an in-flight call
//    (see "What the timeout can and cannot cancel" below)
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
        CircuitBreakerThreshold: int option

        /// How long the circuit stays open before attempting a probe call. None falls back
        /// to 30 s, and is only consulted when CircuitBreakerThreshold is set.
        CircuitBreakerDuration: TimeSpan option

        /// Deadline over the whole protected operation. None = no timeout.
        /// Read "What the timeout can and cannot cancel" below before relying on it.
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

#### What the timeout can and cannot cancel

**It does not abort a grain call that is merely slow.** The protected operation is a
`unit -> Task<'T>`, which takes no `CancellationToken`, so the call has already started by the time
Polly's timeout is armed and nothing can interrupt it. A three-second timeout over a call that
takes eight seconds returns that call's result after eight seconds — no
`TimeoutRejectedException` is raised.

What the timeout does cut short is the waiting the pipeline itself controls: the delay between
retry attempts. So under `execute` with retries a `TimeoutRejectedException` is a real outcome
(it fires while the pipeline is sleeping between attempts), and under `withTimeout` alone — where
there are no retries and therefore no waiting of Polly's own — it effectively never fires.

For a real deadline on a slow grain, use Orleans' own mechanisms instead: shorten
`SiloMessagingOptions.ResponseTimeout` / `ClientMessagingOptions.ResponseTimeout` (30 seconds by
default), or make the operation cancellable and drive it with
`FunctionalGrainRef.callCancellable` from the [functional grain runtime](functional-grains.md),
whose token reaches the handler through `context.cancellationToken`.

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
- The timeout spans the whole attempt sequence, not one attempt — but only over the waiting the
  pipeline itself does; it cannot interrupt an in-flight grain call (see
  [What the timeout can and cannot cancel](#what-the-timeout-can-and-cannot-cancel)).
- The circuit breaker opens only after the retry strategy has given up.
- A single Polly `TimeoutRejectedException` or `BrokenCircuitException` bypasses the retry.

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

It does **not** make a single slow call fail fast — see
[What the timeout can and cannot cancel](#what-the-timeout-can-and-cannot-cancel).

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

For integration tests with a real `TestCluster`, use a grain that tracks its own call count and fails on the first N calls — see `flakyGrain` (over `FlakyState` / `FlakyCommand`) in `tests/Orleans.FSharp.Integration/ClusterFixture.fs`, driven by `GrainResilienceIntegrationTests.fs`.

---

## Package dependency

`GrainResilience` is in the `Orleans.FSharp` core package. It adds a dependency on `Polly` 8.x, which is already bundled — no additional NuGet reference is required.
