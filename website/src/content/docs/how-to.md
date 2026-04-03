---
title: How to Build Distributed Systems with F# and Orleans
description: Step-by-step guide to building distributed systems with Orleans.FSharp — from installation to production deployment with Microsoft Orleans and F#
---

**Build a distributed system with F# and Microsoft Orleans in under 15 minutes.**

Orleans.FSharp provides idiomatic F# computation expressions for Microsoft Orleans, the virtual actor framework. This guide walks you through the entire process — from installing the .NET SDK to running a production-ready silo with grains, state persistence, and property-based tests.

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

## Step 5: Configure the silo

Use the `siloConfig {}` CE to configure Microsoft Orleans clustering, storage, and streaming:

```fsharp
open Orleans.FSharp.Runtime

let config = siloConfig {
    useLocalhostClustering          // single-node for development
    addMemoryStorage "Default"      // in-memory state (swap to Redis/Azure for production)
    enableDashboard 8080            // Orleans Dashboard at http://localhost:8080
}
```

For production, replace with persistent providers:

```fsharp
let prodConfig = siloConfig {
    useRedisClustering "redis-connection-string"
    addRedisStorage "Default" "redis-connection-string"
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
open Orleans.FSharp.Streaming

// In a grain handler:
let! streamRef = Stream.getRef<AccountEvent> ctx "StreamProvider" "Accounts" grainId
do! Stream.publish streamRef (Deposited amount)
```

## Step 9: Deploy to production

Orleans.FSharp supports all Microsoft Orleans production features:

- **Clustering**: Redis, Azure Table Storage, Consul, ZooKeeper, Kubernetes
- **State persistence**: Redis, Azure Blob, Cosmos DB, DynamoDB, ADO.NET (SQL Server, PostgreSQL)
- **Streaming**: Event Hubs, Azure Queue, memory streams
- **Security**: TLS/mTLS, call filters, request context propagation
- **Observability**: OpenTelemetry, health checks, Orleans Dashboard

See the [Silo Configuration](/orleans-fsharp/guides/silo-configuration/) and [Security](/orleans-fsharp/guides/security/) guides for production setup.

## Next steps

- [Grain Definition](/orleans-fsharp/guides/grain-definition/) -- all 31 keywords in the `grain {}` CE
- [Event Sourcing](/orleans-fsharp/guides/event-sourcing/) -- CQRS with `eventSourcedGrain {}`
- [Testing](/orleans-fsharp/guides/testing/) -- TestHarness, GrainMock, and property tests
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
      "name": "Implement the grain with the grain {} computation expression",
      "text": "Use the grain {} CE with defaultState, handle, and persist keywords to define grain behavior declaratively.",
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
      "text": "Use Stream.getRef and Stream.publish from Orleans.FSharp.Streaming for typed event streams.",
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
