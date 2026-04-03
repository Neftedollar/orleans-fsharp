# Token Bucket Rate Limiter with Orleans F#

A distributed rate limiter using the token bucket algorithm, implemented as
an Orleans grain. Each grain instance manages rate limiting for a specific key
(e.g., user ID, API key, IP address).

## Algorithm

The token bucket works as follows:
- A bucket starts with a maximum number of tokens
- Each request consumes one token
- Tokens are replenished at a fixed rate
- If no tokens are available, the request is rejected

## State Definition

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

## Grain Implementation

```fsharp
open Orleans.FSharp

let refillTokens (state: TokenBucketState) (now: System.DateTimeOffset) =
    let elapsed = (now - state.LastRefill).TotalSeconds
    let newTokens = min state.MaxTokens (state.Tokens + elapsed * state.RefillRate)
    { state with Tokens = newTokens; LastRefill = now }

let rateLimiterGrain = grain {
    name "RateLimiter"
    state {
        Tokens = 100.0
        MaxTokens = 100.0
        RefillRate = 10.0  // 10 tokens per second
        LastRefill = System.DateTimeOffset.UtcNow
    }
    handle (fun ctx state msg ->
        task {
            let now = System.DateTimeOffset.UtcNow
            let refilled = refillTokens state now

            match msg with
            | TryConsume count ->
                let needed = float count
                if refilled.Tokens >= needed then
                    let newState = { refilled with Tokens = refilled.Tokens - needed }
                    return Ok newState
                else
                    // Calculate when enough tokens will be available
                    let deficit = needed - refilled.Tokens
                    let waitSeconds = deficit / refilled.RefillRate
                    return Ok refilled  // State unchanged, caller gets Denied

            | GetRemaining ->
                return Ok refilled

            | Reset ->
                return Ok { refilled with Tokens = refilled.MaxTokens }
        })
}
```

## Client Usage

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

## Configuration Variants

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

## Distributed Behavior

Since each grain is keyed by a unique identifier, the rate limiter
naturally distributes across the cluster:

- User "alice" gets her own grain on silo A
- User "bob" gets his own grain on silo B
- No shared state, no locking, no contention between different keys
- Single-threaded grain execution ensures correctness per key

## When to Use

- API rate limiting per user, key, or IP
- Throttling expensive operations (database queries, external API calls)
- Protecting downstream services from overload
- Implementing fair usage policies in multi-tenant systems
