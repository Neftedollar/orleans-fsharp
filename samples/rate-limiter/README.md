# Token Bucket Rate Limiter with Orleans F#

A distributed rate limiter using the token bucket algorithm, implemented as
an Orleans grain. Each grain instance manages rate limiting for a specific key
(e.g., user ID, API key, IP address).

> **Note.** The functional grain runtime below (`grainContract` / `grainFor`) is the current
> authoring model for this pattern. The original `grain { }` CE version is kept, unchanged, under
> [Classic model (deprecated)](#classic-model-deprecated) below -- that CE now carries
> `[<Obsolete>]` (warning, not error) but still compiles and runs as described there.

## Algorithm

The token bucket works as follows:
- A bucket starts with a maximum number of tokens
- Each request consumes one token
- Tokens are replenished at a fixed rate
- If no tokens are available, the request is rejected

This is true of the algorithm itself, independent of authoring model -- both presentations below
implement exactly this.

## State Definition

```fsharp
open System
open System.Threading.Tasks
open Orleans.FSharp

type TokenBucketState =
    { tokens: float
      maxTokens: float
      refillRate: float // tokens per second
      lastRefill: DateTimeOffset }

type RateLimitResult =
    | Allowed of remainingTokens: float
    | Denied of retryAfterSeconds: float
```

`RateLimitResult` no longer needs a case for every possible reply the way the classic
`RateLimitMessage`/`RateLimitResult` pair did -- `tryConsume` below gets exactly this two-case
type as its reply, `remaining` gets `float` directly, and `reset` gets `unit`, so each operation's
API record field states precisely what it returns instead of one reply type shared (and
under-constrained) across every message.

## Grain Implementation

```fsharp
type RateLimiterActor = private RateLimiterActor of unit

[<NoEquality; NoComparison>]
type RateLimiterApi =
    { tryConsume: int -> Task<RateLimitResult>
      remaining: unit -> Task<float>
      reset: unit -> Task<unit> }

[<RequireQualifiedAccess>]
module RateLimiterApi =
    let contract =
        grainContract<RateLimiterActor, string, RateLimiterApi> {
            grainType "rate-limiter.token-bucket"
            version 1
            stringKey

            readOnly (_.remaining)
        }

    let ref = FunctionalGrain.ref contract

module Definition =

    let private refill (state: TokenBucketState) (now: DateTimeOffset) =
        let elapsed = (now - state.lastRefill).TotalSeconds
        let refilled = min state.maxTokens (state.tokens + elapsed * state.refillRate)
        { state with
            tokens = refilled
            lastRefill = now }

    let rateLimiter =
        grainFor RateLimiterApi.contract {
            defaultState (fun () ->
                { tokens = 100.0
                  maxTokens = 100.0
                  refillRate = 10.0 // 10 tokens per second
                  lastRefill = DateTimeOffset.UtcNow })

            handle
                (_.tryConsume)
                (fun context state count ->
                    task {
                        let refilled = refill state context.utcNow
                        let needed = float count

                        if refilled.tokens >= needed then
                            let next = { refilled with tokens = refilled.tokens - needed }
                            return next, Allowed next.tokens
                        else
                            // Calculate when enough tokens will be available
                            let deficit = needed - refilled.tokens
                            let waitSeconds = deficit / refilled.refillRate
                            return refilled, Denied waitSeconds
                    })

            handle
                (_.remaining)
                (fun context state () ->
                    task {
                        let refilled = refill state context.utcNow
                        return refilled, refilled.tokens
                    })

            handle (_.reset) (fun _context state () -> task { return { state with tokens = state.maxTokens }, () })
        }
```

`remaining` is declared `readOnly`: it still calls `refill` to *report* an up-to-date token count,
but the runtime discards whatever state it returns (see "Immutable-state guidance" in
[docs/functional-grains.md](../../docs/functional-grains.md)) -- so a plain read never advances
`lastRefill` in storage, only `tryConsume` and `reset` do. That is the correct bucket semantics
(reads should not need writes) and is stated explicitly here rather than left implicit the way
the classic model's single `handle` case for `GetRemaining` left it. State stays in memory only
(no `stateFrom`), exactly as the classic grain's `state { ... }` (no `persist` call) chose.

## Client Usage

```fsharp
// Get a rate limiter grain for a specific API key
let checkRateLimit (factory: Orleans.IGrainFactory) (apiKey: string) =
    task {
        let api = RateLimiterApi.ref factory apiKey
        let! result = api.tryConsume 1

        match result with
        | Allowed remainingTokens ->
            printfn "Request allowed. %f tokens remaining." remainingTokens
            return true
        | Denied retryAfter ->
            printfn "Rate limited. Retry after %.1f seconds." retryAfter
            return false
    }
```

`factory.GetGrain<IRateLimiterGrain>(apiKey)` becomes `RateLimiterApi.ref factory apiKey`; the
match no longer needs a catch-all `| _ -> return false` branch, because `RateLimitResult` only
ever has the two cases `tryConsume` can actually produce.

## Configuration Variants

Different rate limits for different tiers -- the same record shape `defaultState` or
`initialState` would build:

```fsharp
let freeUserConfig =
    { tokens = 10.0
      maxTokens = 10.0
      refillRate = 1.0 // 1 request per second
      lastRefill = DateTimeOffset.UtcNow }

let proUserConfig =
    { tokens = 100.0
      maxTokens = 100.0
      refillRate = 10.0 // 10 requests per second
      lastRefill = DateTimeOffset.UtcNow }

let enterpriseConfig =
    { tokens = 1000.0
      maxTokens = 1000.0
      refillRate = 100.0 // 100 requests per second
      lastRefill = DateTimeOffset.UtcNow }
```

## Distributed Behavior

Since each grain is keyed by a unique identifier, the rate limiter naturally distributes across
the cluster, exactly as it does under the classic model -- this is an Orleans-level guarantee,
not something either authoring model changes:

- User "alice" gets her own grain on silo A
- User "bob" gets his own grain on silo B
- No shared state, no locking, no contention between different keys
- Single-threaded grain execution ensures correctness per key

## When to Use

- API rate limiting per user, key, or IP
- Throttling expensive operations (database queries, external API calls)
- Protecting downstream services from overload
- Implementing fair usage policies in multi-tenant systems

## Classic model (deprecated)

This is the original write-up, kept unchanged. It is written against the `grain { }` CE, which
now carries `[<Obsolete>]` (warning, not error) -- it still compiles and runs as described. The
pattern it demonstrates is the same one presented on the functional runtime above; only the
authoring model differs.

### State Definition

```fsharp
type TokenBucketState = {
    Tokens: float
    MaxTokens: float
    RefillRate: float  // tokens per second
    LastRefill: System.DateTimeOffset
}

type RateLimitMessage =
    | TryConsume of count: int
    | GetRemaining
    | Reset

type RateLimitResult =
    | Allowed of remainingTokens: float
    | Denied of retryAfterSeconds: float
    | RemainingTokens of float
    | ResetComplete
```

### Grain Implementation

```fsharp
open Orleans.FSharp

let refillTokens (state: TokenBucketState) (now: System.DateTimeOffset) =
    let elapsed = (now - state.LastRefill).TotalSeconds
    let newTokens = min state.MaxTokens (state.Tokens + elapsed * state.RefillRate)
    { state with Tokens = newTokens; LastRefill = now }

let rateLimiterGrain = grain {
    defaultState {
        Tokens = 100.0
        MaxTokens = 100.0
        RefillRate = 10.0  // 10 tokens per second
        LastRefill = System.DateTimeOffset.UtcNow
    }
    handleTyped (fun state msg ->
        task {
            let now = System.DateTimeOffset.UtcNow
            let refilled = refillTokens state now

            match msg with
            | TryConsume count ->
                let needed = float count
                if refilled.Tokens >= needed then
                    let newState = { refilled with Tokens = refilled.Tokens - needed }
                    return newState, Allowed newState.Tokens
                else
                    // Calculate when enough tokens will be available
                    let deficit = needed - refilled.Tokens
                    let waitSeconds = deficit / refilled.RefillRate
                    return refilled, Denied waitSeconds   // state unchanged, caller gets Denied

            | GetRemaining ->
                return refilled, Allowed refilled.Tokens

            | Reset ->
                let reset = { refilled with Tokens = refilled.MaxTokens }
                return reset, Allowed reset.Tokens
        })
}
```

### Client Usage

```fsharp
// Get a rate limiter grain for a specific API key
let checkRateLimit (factory: IGrainFactory) (apiKey: string) =
    task {
        let grain = factory.GetGrain<IRateLimiterGrain>(apiKey)
        let! result = grain.TryConsume(1)
        match result with
        | Allowed remaining ->
            printfn "Request allowed. %f tokens remaining." remaining
            return true
        | Denied retryAfter ->
            printfn "Rate limited. Retry after %.1f seconds." retryAfter
            return false
        | _ -> return false
    }
```

### Configuration Variants

Different rate limits for different tiers:

```fsharp
let freeUserConfig = {
    Tokens = 10.0
    MaxTokens = 10.0
    RefillRate = 1.0  // 1 request per second
    LastRefill = System.DateTimeOffset.UtcNow
}

let proUserConfig = {
    Tokens = 100.0
    MaxTokens = 100.0
    RefillRate = 10.0  // 10 requests per second
    LastRefill = System.DateTimeOffset.UtcNow
}

let enterpriseConfig = {
    Tokens = 1000.0
    MaxTokens = 1000.0
    RefillRate = 100.0  // 100 requests per second
    LastRefill = System.DateTimeOffset.UtcNow
}
```

### Distributed Behavior

Since each grain is keyed by a unique identifier, the rate limiter
naturally distributes across the cluster:

- User "alice" gets her own grain on silo A
- User "bob" gets his own grain on silo B
- No shared state, no locking, no contention between different keys
- Single-threaded grain execution ensures correctness per key

### When to Use

- API rate limiting per user, key, or IP
- Throttling expensive operations (database queries, external API calls)
- Protecting downstream services from overload
- Implementing fair usage policies in multi-tenant systems
