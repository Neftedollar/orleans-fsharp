---
title: How to Build Distributed Systems with F# and Orleans
description: Step-by-step guide to building distributed systems with Orleans.FSharp — from installation to production deployment with Microsoft Orleans and F#
---

**Build a distributed system with F# and Microsoft Orleans in under 15 minutes.**

Orleans.FSharp provides idiomatic F# computation expressions for Microsoft Orleans, the virtual actor framework. This guide walks you through the entire process — from installing the .NET SDK to running a production-ready silo with grains, state persistence, and property-based tests.

> **Note.** This tutorial is written against the `grain { }` CE, which now carries `[<Obsolete>]`
> (warning, not error) -- every step still works exactly as written. For the current grain authoring
> model (`grainContract` / `grainFor` / `FunctionalGrain.ref` / `AddFunctionalGrain`) see
> [functional-grains.md](/orleans-fsharp/functional-grains/); the silo, persistence and testing steps are the same
> under both models.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A code editor (VS Code with Ionide, JetBrains Rider, or Visual Studio)

## Step 1: Install Orleans.FSharp templates

Orleans.FSharp ships a `dotnet new` template that scaffolds a complete solution:

```bash
dotnet new install Orleans.FSharp.Templates
```

## Step 2: Create a new project

Generate a working Orleans.FSharp solution with a silo, grain definitions, and tests:

```bash
dotnet new orleans-fsharp -n MyDistributedApp
cd MyDistributedApp
```

This creates:

- `src/MyDistributedApp.Silo/` — the host process with silo configuration
- `src/MyDistributedApp.Grains/` — grain definitions using `grain {}` CEs
- `tests/MyDistributedApp.Tests/` — FsCheck property tests with GrainArbitrary

## Step 3: Define a grain with discriminated union state

Open the grains project and define your state as an F# discriminated union:

```fsharp
open Orleans
open Orleans.FSharp

[<GenerateSerializer>]
type AccountState =
    | [<Id(0u)>] Inactive
    | [<Id(1u)>] Active of balance: decimal

[<GenerateSerializer>]
type AccountCommand =
    | [<Id(0u)>] Deposit of decimal
    | [<Id(1u)>] Withdraw of decimal
    | [<Id(2u)>] GetBalance
    | [<Id(3u)>] Close
```

## Step 4: Implement the grain with the `grain {}` computation expression

Use the `grain {}` CE to define the grain declaratively — no class inheritance, no mutable state:

```fsharp
let account =
    grain {
        defaultState Inactive

        handle (fun state cmd ->
            task {
                match state, cmd with
                | Inactive, Deposit amount when amount > 0m ->
                    return Active amount, box amount
                | Active balance, Deposit amount when amount > 0m ->
                    let newBalance = balance + amount
                    return Active newBalance, box newBalance
                | Active balance, Withdraw amount when amount > 0m && amount <= balance ->
                    let newBalance = balance - amount
                    if newBalance = 0m then
                        return Inactive, box 0m
                    else
                        return Active newBalance, box newBalance
                | Active balance, GetBalance ->
                    return Active balance, box balance
                | Inactive, GetBalance ->
                    return Inactive, box 0m
                | Active _, Close ->
                    return Inactive, box true
                | _ ->
                    return state, box false
            })

        persist "Default"
    }
```

The F# compiler ensures every state-command combination is handled. Invalid transitions are caught at compile time, not runtime.

## Step 4 (functional equivalent): the current authoring model

Step 4 above uses the deprecated `grain { }` CE. The functional runtime -- the current model -- gives
the same domain a **contract** (wire identity + key codec + policies) and an **API record** of typed
operations, instead of one boxed message DU:

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type AccountActor = private AccountActor of unit

[<NoEquality; NoComparison>]
type AccountApi =
    { deposit: decimal -> Task<decimal>
      withdraw: decimal -> Task<Result<decimal, string>>
      getBalance: unit -> Task<decimal>
      close: unit -> Task<bool> }

[<RequireQualifiedAccess>]
module AccountApi =
    let contract =
        grainContract<AccountActor, string, AccountApi> {
            grainType "account"
            version 1
            stringKey

            readOnly (_.getBalance)
        }

    let ref = FunctionalGrain.ref contract

let accountDefinition =
    grainFor AccountApi.contract {
        defaultState (fun () -> Inactive)

        handle
            (_.deposit)
            (fun _context state amount ->
                task {
                    if amount <= 0m then
                        return state, (match state with Active b -> b | Inactive -> 0m)
                    else
                        match state with
                        | Inactive -> return Active amount, amount
                        | Active balance ->
                            let next = balance + amount
                            return Active next, next
                })

        handle
            (_.withdraw)
            (fun _context state amount ->
                task {
                    match state with
                    | Active balance when amount > 0m && amount <= balance ->
                        let next = balance - amount
                        if next = 0m then return Inactive, Ok 0m else return Active next, Ok next
                    | Active _ -> return state, Error "invalid withdrawal amount"
                    | Inactive -> return state, Error "account is inactive"
                })

        handle
            (_.getBalance)
            (fun _context state () -> task { return state, (match state with Active b -> b | Inactive -> 0m) })

        handle
            (_.close)
            (fun _context state () ->
                task {
                    match state with
                    | Active _ -> return Inactive, true
                    | Inactive -> return Inactive, false
                })
    }
```

Reuses the exact same `AccountState` DU from Step 3. `readOnly (_.getBalance)` tells the runtime the
handler's returned state is discarded and lets it interleave with other read-only calls. Register
with `siloBuilder.AddFunctionalGrain(accountDefinition)` on the silo builder (inside the
`builder.UseOrleans(fun siloBuilder -> ...)` delegate), then call it with a typed record instead of a
boxed message:

```fsharp
let account = AccountApi.ref factory "account-1"
let! balance = account.deposit 100m
let! result = account.withdraw 40m
```

See [functional-grains.md](/orleans-fsharp/functional-grains/) for the complete guide, including
persistence, timers, reminders, and multi-provider writes.

## Step 5: Configure the silo

Use the `siloConfig {}` CE to configure Microsoft Orleans clustering, storage, and streaming:

```fsharp
open Orleans.FSharp.Runtime

let config = siloConfig {
    useLocalhostClustering          // single-node for development
    addMemoryStorage "Default"      // in-memory state (swap to Redis/Azure for production)
    addDashboard                    // Orleans Dashboard (map it in your ASP.NET Core pipeline)
}
```

For production, replace with persistent providers:

```fsharp
let prodConfig = siloConfig {
    addRedisClustering redisConnectionString
    addRedisStorage "Default" redisConnectionString
    addMemoryStreams "StreamProvider"
    enableHealthChecks
}
```

## Step 6: Build and run

```bash
dotnet build
dotnet test
dotnet run --project src/MyDistributedApp.Silo
```

The silo starts, activates grains on demand, and persists state automatically. Grains are virtual actors — they are always addressable and activated on first call.

## Step 7: Write property-based tests

Orleans.FSharp includes GrainArbitrary for FsCheck, which auto-generates random command sequences from your DU definition:

```fsharp
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Testing

let accountInvariant state =
    match state with
    | Inactive -> true
    | Active balance -> balance > 0m

let applyCommand state cmd =
    match state, cmd with
    | Inactive, Deposit amount when amount > 0m -> Active amount
    | Active balance, Deposit amount when amount > 0m -> Active(balance + amount)
    | Active balance, Withdraw amount when amount > 0m && amount <= balance ->
        if balance - amount = 0m then Inactive else Active(balance - amount)
    | _ -> state

[<Property>]
let ``account balance is never negative`` () =
    let arb = GrainArbitrary.forCommands<AccountCommand>()
    Prop.forAll arb (fun commands ->
        FsCheckHelpers.stateMachineProperty Inactive applyCommand accountInvariant commands)
```

## Step 8: Add streaming

Publish and subscribe to event streams with typed `StreamRef<'T>`:

```fsharp
open Orleans.Streams              // IStreamProvider
open Orleans.FSharp.Streaming     // the Stream module

// In a functional handler: the named provider is a keyed service on context.services
let provider = context.services.GetRequiredKeyedService<IStreamProvider> "StreamProvider"
let stream = Stream.getStream<AccountEvent> provider "Accounts" (string context.key)
do! Stream.publish stream (Deposited amount)
```

## Step 9: Deploy to production

Orleans.FSharp supports all Microsoft Orleans production features:

- **Clustering**: Redis, Azure Table Storage, Consul, ZooKeeper, Kubernetes
- **State persistence**: Redis, Azure Blob, Cosmos DB, DynamoDB, ADO.NET (SQL Server, PostgreSQL)
- **Streaming**: Event Hubs, Azure Queue, memory streams
- **Security**: TLS/mTLS, call filters, request context propagation
- **Observability**: OpenTelemetry, health checks, Orleans Dashboard

See the [Silo Configuration](/orleans-fsharp/silo-configuration/) and [Security](/orleans-fsharp/security/) guides for production setup.

## Next steps

- [Functional Grain Runtime](/orleans-fsharp/functional-grains/) -- the current authoring model, full guide
- [Grain Definition](/orleans-fsharp/grain-definition/) -- all 27 keywords in the `grain {}` CE (deprecated model, kept for reference)
- [Event Sourcing](/orleans-fsharp/event-sourcing/) -- `journaledGrainFor { }`, a grain whose state is the fold of an event journal
- [Testing](/orleans-fsharp/testing/) -- TestHarness, GrainMock, and property tests
- [API Reference](/orleans-fsharp/api-reference/) -- complete module and function reference

<script type="application/ld+json">
{
  "@context": "https://schema.org",
  "@type": "HowTo",
  "name": "How to Build Distributed Systems with F# and Orleans",
  "description": "Step-by-step guide to building distributed systems with Orleans.FSharp, the idiomatic F# API for Microsoft Orleans.",
  "totalTime": "PT15M",
  "tool": [
    { "@type": "HowToTool", "name": ".NET 10 SDK" },
    { "@type": "HowToTool", "name": "Orleans.FSharp NuGet packages" }
  ],
  "step": [
    {
      "@type": "HowToStep",
      "name": "Install Orleans.FSharp templates",
      "text": "Run: dotnet new install Orleans.FSharp.Templates",
      "position": 1
    },
    {
      "@type": "HowToStep",
      "name": "Create a new project",
      "text": "Run: dotnet new orleans-fsharp -n MyDistributedApp",
      "position": 2
    },
    {
      "@type": "HowToStep",
      "name": "Define grain state as a discriminated union",
      "text": "Define your grain state and commands as F# discriminated unions with [<GenerateSerializer>] attributes.",
      "position": 3
    },
    {
      "@type": "HowToStep",
      "name": "Implement the grain with the grain {} computation expression (deprecated; see the functional grain runtime)",
      "text": "Use the grain {} CE with defaultState, handle, and persist keywords to define grain behavior declaratively. This CE is deprecated; new code should use the functional grain runtime (grainContract / grainFor / AddFunctionalGrain).",
      "position": 4
    },
    {
      "@type": "HowToStep",
      "name": "Configure the silo",
      "text": "Use siloConfig {} CE to configure clustering, storage, and streaming providers.",
      "position": 5
    },
    {
      "@type": "HowToStep",
      "name": "Build and run",
      "text": "Run: dotnet build && dotnet test && dotnet run --project src/MyDistributedApp.Silo",
      "position": 6
    },
    {
      "@type": "HowToStep",
      "name": "Write property-based tests",
      "text": "Use GrainArbitrary.forCommands to auto-generate random command sequences and verify state machine invariants with FsCheck.",
      "position": 7
    },
    {
      "@type": "HowToStep",
      "name": "Add streaming",
      "text": "Use Stream.getStream and Stream.publish from Orleans.FSharp.Streaming for typed event streams.",
      "position": 8
    },
    {
      "@type": "HowToStep",
      "name": "Deploy to production",
      "text": "Replace localhost clustering with Redis/Azure/Kubernetes. Add TLS, health checks, and OpenTelemetry.",
      "position": 9
    }
  ]
}
</script>
