# Redis Example: Shopping Cart Service

**End-to-end guide — Redis storage, clustering, and reminders for a real-world shopping cart.**

## What you'll build

A shopping cart service where each cart is a durable Orleans grain backed by Redis.
The service uses:

- **Redis clustering** — silos discover each other through Redis instead of Kubernetes or Azure
- **Redis grain storage** — cart state survives silo restarts
- **Redis reminders** — an idle-cart cleanup reminder fires after 30 minutes of inactivity
- **Universal grain pattern** — no C# interface stubs needed

---

## Prerequisites

### Redis

```bash
docker run -d -p 6379:6379 redis:7-alpine
```

### NuGet packages

```bash
dotnet add package Orleans.FSharp
dotnet add package Orleans.FSharp.Runtime
dotnet add package Orleans.FSharp.Abstractions   # C# shim for Orleans proxy gen

# Redis providers — optional at reference time, required at runtime
dotnet add package Microsoft.Orleans.Clustering.Redis
dotnet add package Microsoft.Orleans.Persistence.Redis
dotnet add package Microsoft.Orleans.Reminders.Redis
```

---

## Domain types

```fsharp
open Orleans.FSharp

type CartItem = { ProductId: string; Quantity: int; UnitPrice: decimal }

type CartState =
    { Items: CartItem list
      CheckedOut: bool }

type CartCommand =
    | AddItem    of productId: string * qty: int * unitPrice: decimal
    | RemoveItem of productId: string
    | GetItems
    | Checkout
    | Clear
```

---

## Grain definition

```fsharp
open Orleans.FSharp
open Orleans.FSharp.Runtime

let cartGrain =
    grain {
        defaultState { Items = []; CheckedOut = false }
        persist "Default"   // maps to the "Default" Redis storage provider

        // Idle-cart cleanup: remind once after 30 minutes
        onActivate (fun ctx state -> task {
            do! Reminder.register ctx.Self "idle-cleanup"
                    (TimeSpan.FromMinutes 30.)
                    (TimeSpan.FromMinutes 30.)
                |> Task.ignore
            return state
        })

        onReminder "idle-cleanup" (fun ctx state -> task {
            // Deactivate empty or checked-out carts after inactivity
            if state.Items.IsEmpty || state.CheckedOut then
                ctx.DeactivateOnIdle()
            return state
        })

        handle (fun state cmd -> task {
            match cmd with
            | AddItem(productId, qty, price) ->
                if state.CheckedOut then
                    return state, box state   // ignore — cart is closed
                else
                    let existing = state.Items |> List.tryFind (fun i -> i.ProductId = productId)
                    let updated =
                        match existing with
                        | Some item ->
                            state.Items
                            |> List.map (fun i ->
                                if i.ProductId = productId
                                then { i with Quantity = i.Quantity + qty }
                                else i)
                        | None ->
                            { ProductId = productId; Quantity = qty; UnitPrice = price }
                            :: state.Items
                    let newState = { state with Items = updated }
                    return newState, box newState

            | RemoveItem productId ->
                let newState =
                    { state with Items = state.Items |> List.filter (fun i -> i.ProductId <> productId) }
                return newState, box newState

            | GetItems ->
                return state, box state.Items

            | Checkout ->
                let newState = { state with CheckedOut = true }
                return newState, box newState

            | Clear ->
                let newState = { Items = []; CheckedOut = false }
                return newState, box newState
        })
    }
```

---

## Silo configuration

```fsharp
open System
open Orleans.FSharp.Runtime

let redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION") // "localhost:6379"

let config = siloConfig {
    addRedisClustering    redisConn
    addRedisStorage       "Default" redisConn
    addRedisReminderService redisConn
    clusterId "shopping-cart-cluster"
    serviceId "shopping-cart"
}
```

The three Redis CE operations map to these Orleans packages:

| CE operation | NuGet package |
|---|---|
| `addRedisClustering` | `Microsoft.Orleans.Clustering.Redis` |
| `addRedisStorage` | `Microsoft.Orleans.Persistence.Redis` |
| `addRedisReminderService` | `Microsoft.Orleans.Reminders.Redis` |

---

## Full silo startup

```fsharp
open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime

[<EntryPoint>]
let main _ =
    let redisConn =
        Environment.GetEnvironmentVariable("REDIS_CONNECTION")
        |> Option.ofObj
        |> Option.defaultValue "localhost:6379"

    let config = siloConfig {
        addRedisClustering    redisConn
        addRedisStorage       "Default" redisConn
        addRedisReminderService redisConn
        clusterId "shopping-cart-cluster"
        serviceId "shopping-cart"
    }

    let builder = Host.CreateApplicationBuilder()
    SiloConfig.applyToHost config builder

    // Register the grain definition
    builder.Services.AddFSharpGrain<CartState, CartCommand>(cartGrain) |> ignore

    let host = builder.Build()
    host.Run()
    0
```

---

## Client configuration

A standalone client (separate process or service) connects to the cluster via static
gateway clustering.  The gateway port matches the silo's default port (30000).

```fsharp
open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime

let clientCfg = clientConfig {
    useStaticClustering ["127.0.0.1:30000"]
    clusterId "shopping-cart-cluster"
    serviceId "shopping-cart"
}

let builder = HostApplicationBuilder()
ClientConfig.applyToHost clientCfg builder

let host = builder.Build()
host.Start()

let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()
```

For multi-silo production clusters, list multiple gateway addresses:

```fsharp
let clientCfg = clientConfig {
    useStaticClustering ["10.0.0.1:30000"; "10.0.0.2:30000"; "10.0.0.3:30000"]
    clusterId "shopping-cart-cluster"
    serviceId "shopping-cart"
}
```

---

## Calling the grain

```fsharp
open Orleans.FSharp

// Get a handle for user "user-42"'s cart
let cart = FSharpGrain.ref<CartState, CartCommand> factory "user-42"

// Add items
let! _ = cart |> FSharpGrain.send (AddItem("sku-001", 2, 9.99m))
let! _ = cart |> FSharpGrain.send (AddItem("sku-002", 1, 24.50m))

// Read the items (ask — result is CartItem list, not full CartState)
let! items = cart |> FSharpGrain.ask<CartState, CartCommand, CartItem list> GetItems
printfn "Cart has %d line(s)" items.Length

// Remove an item
do! cart |> FSharpGrain.post (RemoveItem "sku-001")

// Checkout
let! finalState = cart |> FSharpGrain.send Checkout
printfn "Checked out: %b" finalState.CheckedOut
```

### Key functions

| Function | When to use |
|---|---|
| `FSharpGrain.ref factory key` | Get a string-keyed handle |
| `FSharpGrain.send cmd handle` | Send a command, get back the new state |
| `FSharpGrain.post cmd handle` | Send a command, discard the result |
| `FSharpGrain.ask<S,C,R> cmd handle` | Send a command, get back a specific result type (not the state) |

---

## Production checklist

### Load connection strings from the environment

Never inline connection strings in source code:

```fsharp
let redisConn =
    Environment.GetEnvironmentVariable("REDIS_CONNECTION")
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith "REDIS_CONNECTION is not set")
```

Or use `IConfiguration`:

```fsharp
open Microsoft.Extensions.Configuration

let redisConn = builder.Configuration.GetConnectionString("Redis")
```

### TLS

Enable TLS between silos and clients using the `useTls` CE operation:

```fsharp
let config = siloConfig {
    addRedisClustering    redisConn
    addRedisStorage       "Default" redisConn
    addRedisReminderService redisConn
    useTls "my-cert-subject"
    clusterId "shopping-cart-cluster"
    serviceId "shopping-cart"
}
```

For Redis itself, use a `rediss://` connection string or pass TLS options
through the underlying provider's configuration.

### Health checks

```fsharp
let config = siloConfig {
    addRedisClustering    redisConn
    addRedisStorage       "Default" redisConn
    addRedisReminderService redisConn
    enableHealthChecks
    clusterId "shopping-cart-cluster"
    serviceId "shopping-cart"
}
```

`enableHealthChecks` wires the Orleans liveness probe into ASP.NET Core's
`/healthz` endpoint (or your configured health check path).

### Multiple storage providers

Use a dedicated provider for frequently-written state to avoid sharing throughput:

```fsharp
let config = siloConfig {
    addRedisClustering    redisConn
    addRedisStorage       "CartStorage"  redisConn   // carts
    addRedisStorage       "SessionStore" redisConn   // sessions on a different logical DB
    addRedisReminderService redisConn
    clusterId "shopping-cart-cluster"
    serviceId "shopping-cart"
}

// In the grain:
let cartGrain = grain {
    persist "CartStorage"
    // ...
}
```

---

## Next steps

- [Silo Configuration](silo-configuration.md) — complete `siloConfig { }` CE reference
- [Client Configuration](client-configuration.md) — complete `clientConfig { }` CE reference
- [Advanced](advanced.md) — transactions, telemetry, graceful shutdown, state migration
- [Security](security.md) — TLS, mTLS, filters, secrets management
