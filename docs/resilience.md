# Grain Resilience

**Retry, circuit-breaker, and timeout strategies for Orleans grain calls, powered by Polly v8.**

## What you'll learn

- How to retry transient grain failures automatically
- How to add per-call timeouts to grain calls
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

// 2. Per-call timeout — fail fast if the grain takes > 2 seconds
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

        /// How long the circuit stays open before attempting a probe call. Default: 30 s.
        CircuitBreakerDuration: TimeSpan option

        /// Per-attempt deadline. None = no timeout.
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

Retries the grain call up to `maxAttempts` times before giving up.

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

Enforces a hard deadline on a single grain call. Throws `Polly.Timeout.TimeoutRejectedException` when the deadline is exceeded.

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
- The timeout applies to the total time including all retries.
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

### Fail fast on slow grains

```fsharp
let! result =
    GrainResilience.withTimeout<Result> (TimeSpan.FromSeconds 2) (fun () ->
        slowGrain.HandleMessage query)
```

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

For integration tests with a real `TestCluster`, use a grain that tracks its own call count and fails on the first N calls — see `FlakyGrain` in `tests/Orleans.FSharp.Integration/ClusterFixture.fs`.

---

## Package dependency

`GrainResilience` is in the `Orleans.FSharp` core package. It adds a dependency on `Polly` 8.x, which is already bundled — no additional NuGet reference is required.
