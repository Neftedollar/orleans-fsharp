---
title: "Advanced Topics"
description: "Transactions, grain directory, OpenTelemetry, shutdown, state migration, and serialization."
---

# Advanced Topics

**Transactions, grain directory, OpenTelemetry, shutdown, state migration, and serialization.**

> **Current API.** This page complements the functional runtime guide. Historical CodeGen patterns are retained in [Legacy Advanced Patterns](/orleans-fsharp/legacy/advanced/).

## What you'll learn

- How to use Orleans transactions from functional definitions
- How to configure the grain directory
- How to integrate with OpenTelemetry
- How to perform graceful shutdown
- How to migrate grain state between versions
- How to configure F# type serialization

---

## Transactions

Use `transactionalStateFrom` on the definition and mark participating operations with `transactional` on the contract. The complete semantics and examples live in [Distributed ACID transactions](/orleans-fsharp/functional-grains/#distributed-acid-transactions).

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type LedgerActor = private LedgerActor of unit
type Ledger = { balance: decimal }

[<NoEquality; NoComparison>]
type LedgerApi = { deposit: decimal -> Task<decimal> }

let ledgerContract =
    grainContract<LedgerActor, string, LedgerApi> {
        grainType "ledger"
        version 1
        stringKey
        transactional Orleans.TransactionOption.CreateOrJoin (_.deposit)
    }

let ledgerState = TransactionalState.create<Ledger> "ledger" "TransactionStore"

let ledgerDefinition =
    grainFor ledgerContract {
        defaultState (fun () -> ())
        transactionalStateFrom ledgerState (fun _ -> { balance = 0m })

        handle (_.deposit) (fun context () amount ->
            task {
                let state = context.transactionalState ledgerState
                let! balance =
                    state.updateWith(fun current ->
                        let next = { balance = current.balance + amount }
                        next, next.balance)
                return (), balance
            })
    }
```

## Grain Directory

The grain directory maps grain identities to their physical silo locations. You can configure it with different backing stores.

```fsharp
open Orleans.FSharp.GrainDirectory

// Default in-memory distributed directory
let configureFn = GrainDirectory.configure Default

// Redis-backed directory
let configureFn = GrainDirectory.configure (Redis redisConnStr)

// Azure Table-backed directory
let configureFn = GrainDirectory.configure (AzureStorage azureConnStr)

// Custom directory
let configureFn = GrainDirectory.configure (Custom myConfigurator)

// Apply to silo builder
configureFn siloBuilder |> ignore
```

---

## OpenTelemetry

Orleans publishes traces and metrics under well-known activity-source and meter names. Wire
them into OpenTelemetry directly with the standard .NET builder — no Orleans.FSharp helper is
required.

### Activity source and meter names

```fsharp
// Orleans tracing activity sources
"Microsoft.Orleans.Runtime"        // runtime traces
"Microsoft.Orleans.Application"    // application traces

// Orleans metrics meter
"Microsoft.Orleans"
```

### Full OpenTelemetry setup

```fsharp
open OpenTelemetry.Trace
open OpenTelemetry.Metrics

builder.Services
    .AddOpenTelemetry()
    .WithTracing(fun tracing ->
        tracing
            .AddSource("Microsoft.Orleans.Runtime")
            .AddSource("Microsoft.Orleans.Application")
            .AddOtlpExporter()
        |> ignore)
    .WithMetrics(fun metrics ->
        metrics
            .AddMeter("Microsoft.Orleans")
            .AddOtlpExporter()
        |> ignore)
|> ignore
```

---

## Graceful Shutdown

The `Shutdown` module provides helpers for clean silo shutdown.

### Configure drain timeout

```fsharp
open System
open Microsoft.Extensions.Hosting
open Orleans.FSharp

let configureShutdown (hostBuilder: IHostBuilder) =
    Shutdown.configureGracefulShutdown (TimeSpan.FromSeconds 30.) hostBuilder
```

### Stop the host

```fsharp
let stop (host: IHost) = Shutdown.stopHost host
```

### Register a shutdown handler

```fsharp
let registerShutdownHandler (hostBuilder: IHostBuilder) =
    Shutdown.onShutdown
        (fun _ct -> task { printfn "Silo is shutting down..." })
        hostBuilder
```

Multiple shutdown handlers can be registered; they run in registration order.

---

## State Migration

The `StateMigration` module enables upgrading grain state schemas across deployments.

### Define migrations

```fsharp
open Orleans.FSharp

type CounterStateV1 = { Count: int }
type CounterStateV2 = { Count: int; CreatedAt: DateTime }
type CounterStateV3 = { Value: int; CreatedAt: DateTime }

// Migration from v1 to v2: add a new field with a default value
let v1ToV2 =
    StateMigration.migration<CounterStateV1, CounterStateV2> 1 2
        (fun v1 -> { Count = v1.Count; CreatedAt = DateTime.MinValue })

// Migration from v2 to v3: rename a field
let v2ToV3 =
    StateMigration.migration<CounterStateV2, CounterStateV3> 2 3
        (fun v2 -> { Value = v2.Count; CreatedAt = v2.CreatedAt })
```

### Apply migrations

```fsharp
let migrations = [ v1ToV2; v2ToV3 ]
let oldV1State = { Count = 3 }

// Upgrade from v1 to latest (throws if chain is invalid)
let currentState : CounterStateV3 =
    StateMigration.applyMigrations<CounterStateV3> migrations 1 (box oldV1State)

// Safe version — validate and apply in one call, returns Result
match StateMigration.tryApplyMigrations<CounterStateV3> migrations 1 (box oldV1State) with
| Ok newState -> printfn "Migrated value: %d" newState.Value
| Error errors -> errors |> List.iter (printfn "Migration error: %s")
```

Migrations are sorted by `FromVersion` and applied sequentially.
`tryApplyMigrations` runs `validate` first; if the chain has gaps or duplicates it returns
`Error (string list)` without touching the state.

### Validate migration chain

```fsharp
let errors = StateMigration.validate migrations

match errors with
| [] -> printfn "Migration chain is valid"
| errs -> for e in errs do printfn "Error: %s" e
```

Validates:
- No duplicate `FromVersion` values
- Contiguous chain (each migration's `ToVersion` matches the next migration's `FromVersion`)

---

## Serialization

### FSharp.SystemTextJson

Orleans.FSharp ships with pre-configured JSON serialization options for F# types:

```fsharp
open Orleans.FSharp

// Pre-configured options with DU support
let options = Serialization.fsharpJsonOptions

// Or the raw FSharpJson module
let options = FSharpJson.serializerOptions
```

These options support:
- Discriminated unions (adjacent tag encoding)
- Records
- Options / ValueOptions
- Lists, Sets, Maps
- Tuples

### Register F# converters

```fsharp
open System.Text.Json

let myOptions = JsonSerializerOptions()
Serialization.addFSharpConverters myOptions |> ignore
```

### Create options with extra converters

```fsharp
open System.Text.Json.Serialization

let options =
    Serialization.withConverters [ JsonStringEnumConverter() :> JsonConverter ]
```

### Orleans native F# serialization

For native Orleans serializer support (not JSON), use the `FSharpSerialization` module:

```fsharp
open Orleans.FSharp.FSharpSerialization

FSharpSerialization.addFSharpSerialization siloBuilder |> ignore
```

Requires `Microsoft.Orleans.Serialization.FSharp`.

---

## Immutable Values

Use `Immutable<'T>` for zero-copy grain argument passing when the value will not be modified:

```fsharp
open Orleans.FSharp

let data = immutable [1; 2; 3; 4; 5]
// Pass 'data' to a grain method -- Orleans skips serialization copies

let values = unwrapImmutable data
// values = [1; 2; 3; 4; 5]
```

---

## Persistent State Operations

Functional definitions attach typed descriptors with `stateFrom` or `usePersistentState`.
Inside a callback, resolve an additional facet through the functional context:

```fsharp
open Orleans.FSharp

type Profile = { displayName: string }
type ProfileActor = private ProfileActor of unit

let profileState = PersistentState.create<Profile> "profile" "Default"

let rename (context: FunctionalGrainContext<ProfileActor, string>) newName =
    task {
        let state = context.persistentState profileState
        state.State <- { displayName = newName }
        do! state.WriteStateAsync()
    }
```

---

## Functional Observers

Declare an `observerContract`, host a typed handler record with `FunctionalObserver.create`, and
pass the resulting handle as an ordinary operation argument. The grain holds live handles in an
ephemeral `FunctionalObserverManager` and pushes with `Notify`. See
[Functional observers](/orleans-fsharp/functional-grains/#push-to-clients-functional-observers) for the complete,
self-contained example and lifetime rules.

---

## Grain Services

Register background services that run on every silo with the `addGrainService` operation in
`siloConfig { }`:

```fsharp
open Orleans.FSharp.Runtime

let config = siloConfig {
    useLocalhostClustering
    addGrainService typeof<MyBackgroundService>
}
```

---

## Parallel Grain Calls

Orleans grains are independent actors — many queries can be issued concurrently. Orleans.FSharp provides two complementary approaches.

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type BatchAccountActor = private BatchAccountActor of unit

[<NoEquality; NoComparison>]
type BatchAccountApi =
    { balance: unit -> Task<decimal>
      deposit: decimal -> Task<unit>
      optIn: unit -> Task<string option> }

[<RequireQualifiedAccess>]
module BatchAccountApi =
    let contract =
        grainContract<BatchAccountActor, string, BatchAccountApi> {
            grainType "batch-account"
            version 1
            stringKey
            readOnly (_.balance)
            readOnly (_.optIn)
        }

    let ref = FunctionalGrain.ref contract
```

### `and!` — fixed parallel bindings (F# applicative CE syntax)

For a **small, fixed set** of concurrent grain calls use the `and!` keyword inside a `task {}` expression. All bound tasks start simultaneously and the CE collects their results:

```fsharp
let totalThree (account1: BatchAccountApi) (account2: BatchAccountApi) (account3: BatchAccountApi) =
  task {
    let! balance1 = account1.balance ()
    and! balance2 = account2.balance ()
    and! balance3 = account3.balance ()
    // All three calls ran in parallel
    return balance1 + balance2 + balance3
  }
```

`and!` goes through the task builder's `MergeSources`, which starts every bound task before awaiting any of them — three 400 ms calls take about 400 ms, not 1,200. It is the idiomatic F# way to express parallel fan-out for a known set of grain references.

### `GrainBatch` — dynamic collections of grains

When the number of grains is determined at runtime (e.g., retrieved from a roster or config), use `GrainBatch`:

```fsharp
open Orleans.FSharp

let fanOut (factory: Orleans.IGrainFactory) =
    task {
        let keys = [ "alice"; "bob"; "carol"; "dave" ]
        let accounts = keys |> List.map (BatchAccountApi.ref factory)

        let! balances = GrainBatch.map accounts (fun account -> account.balance ())
        let! total = GrainBatch.aggregate accounts (fun account -> account.balance ()) List.sum
        let! results = GrainBatch.tryMap accounts (fun account -> account.balance ())
        let! ok, failed = GrainBatch.partition accounts (fun account -> account.balance ())
        let! opted = GrainBatch.choose accounts (fun account -> account.optIn ())
        do! GrainBatch.iter accounts (fun account -> account.deposit 0.01m)
        let! statuses = GrainBatch.tryIter accounts (fun account -> account.deposit 0.01m)

        return balances, total, results, ok, failed, opted, statuses
    }
```

### When to use which

| Scenario | Recommendation |
|---|---|
| 2–4 specific grain calls | `and!` — cleaner syntax, no list overhead |
| N grains from a collection | `GrainBatch.map` / `GrainBatch.aggregate` |
| Tolerating individual failures | `GrainBatch.tryMap` / `GrainBatch.partition` |
| Await all side-effecting calls | `GrainBatch.iter` |
| Await all side-effecting calls and capture errors | `GrainBatch.tryIter` |
| Filter grain responses | `GrainBatch.choose` |

---

## TaskHelpers

Utility functions for composing Task-based operations with Result:

```fsharp
open Orleans.FSharp

let composed n =
    TaskHelpers.taskResult n
    |> TaskHelpers.taskBind (fun n ->
        if n > 0 then TaskHelpers.taskResult (n * 2)
        else TaskHelpers.taskError "negative")
```

---

## Logging

The `Log` module provides structured logging with automatic correlation ID propagation:

```fsharp
open System
open Microsoft.Extensions.Logging
open Orleans.FSharp

let logOrder (logger: ILogger) orderId requestId =
    task {
        Log.logInfo logger "Processing order {OrderId}" [| box orderId |]
        do!
            Log.withCorrelation requestId (fun () ->
                task { Log.logInfo logger "Inside correlated operation" [||] })
        return Log.currentCorrelationId()
    }
```

---

## Reminders and Timers

Functional definitions declare durable reminders with `onReminder` and activation-local timers
with `onTimer`. Both callbacks receive `FunctionalGrainContext` and return replacement state
(or events on a journaled definition). Renaming or removing a reminder requires an explicit
unregister migration because the durable registration outlives the old definition; see
[Reminder rename and removal](/orleans-fsharp/functional-grains/#reminder-rename-and-removal-the-explicit-unregister-migration).

---

## Request Context

The `RequestCtx` module wraps Orleans `RequestContext` for idiomatic F# access.
Values placed in the request context are automatically propagated from callers to callees
by the Orleans runtime — useful for correlation IDs, tenant IDs, or any per-call metadata.

### Set and get a value

```fsharp
open Orleans.FSharp

// Set before making a grain call
RequestCtx.set "tenantId" (box "acme-corp")

// Inside the callee grain — returns Some "acme-corp"
let tenantId = RequestCtx.get<string> "tenantId"

// Or with a fallback
let tenant = RequestCtx.getOrDefault<string> "tenantId" "unknown"
```

### Remove a value

```fsharp
RequestCtx.remove "tenantId"
```

### Scoped context with `withValue`

`withValue` sets a key before running a Task and removes it afterwards — even if the Task throws:

```fsharp
open System.Threading.Tasks

let withCorrelation requestId (work: unit -> Task<'Reply>) =
    RequestCtx.withValue<'Reply> "correlationId" (box requestId) work
```

### API summary

| Function | Signature | Description |
|---|---|---|
| `RequestCtx.set` | `string -> obj -> unit` | Set a context value |
| `RequestCtx.get<'T>` | `string -> 'T option` | Read a typed value (None if missing or wrong type) |
| `RequestCtx.getOrDefault<'T>` | `string -> 'T -> 'T` | Read with a fallback default |
| `RequestCtx.remove` | `string -> unit` | Remove a key from the context |
| `RequestCtx.withValue<'T>` | `string -> obj -> (unit -> Task<'T>) -> Task<'T>` | Scoped set/remove around a Task |

---

## Scripting / REPL

The scripting helpers start a local in-process silo from an F# script (`.fsx` file), letting you
iterate on grain logic without a full project setup.

`Scripting.startOnPorts` builds and starts its host internally and takes no configuration
callback, so it can host only what its fixed recipe already registers — it cannot be handed a
grain definition. `FunctionalScripting.startOnPorts` (in `Orleans.FSharp.Runtime`) is the one that
takes definitions, so a script that wants to call its own grain uses the functional runtime:

```fsharp
#r "nuget: Orleans.FSharp"
#r "nuget: Orleans.FSharp.Runtime"

open System.Threading.Tasks
open Orleans.FSharp

// Define a grain inline: an API record, a contract, and a definition
type PingActor = private PingActor of unit

[<NoEquality; NoComparison>]
type PingApi =
    { ping: unit -> Task<int>
      count: unit -> Task<int> }

let pingContract =
    grainContract<PingActor, string, PingApi> {
        grainType "scripting.ping"
        stringKey
    }

let pingDefinition =
    grainFor pingContract {
        defaultState (fun () -> 0)
        handle (_.ping) (fun _ n () -> task { return n + 1, n + 1 })
        handle (_.count) (fun _ n () -> task { return n, n })
    }

let run () =
    task {
        // Start an in-process silo on the given ports, hosting that definition.
        let! silo =
            FunctionalScripting.startOnPorts
                11111
                30000
                [ FunctionalGrainRegistration.of' pingDefinition ]

        let api = FunctionalGrain.ref pingContract silo.GrainFactory "ping-1"
        let! n = api.ping ()
        printfn "Count: %d" n

        do! Scripting.shutdown silo
    }

run().GetAwaiter().GetResult()
```

`samples/quickstart-functional.fsx` is this script, runnable.

### API

| Function | Description |
|---|---|
| `Scripting.startOnPorts siloPort gatewayPort` | Start a silo on specific ports, with the fixed recipe only |
| `FunctionalScripting.startOnPorts siloPort gatewayPort registrations` | The same silo, hosting the given functional grain definitions (`Orleans.FSharp.Runtime`) |
| `FunctionalGrainRegistration.of' definition` | Erase a definition's type parameters so a heterogeneous list can be passed |

| `Scripting.shutdown handle` | Stop the silo and release resources |

Both entry points return the same `SiloHandle`, which exposes `.Host`, `.Client`, and
`.GrainFactory` for direct access when you need lower-level control.

The silo is pre-configured with in-memory storage (the default store plus `Default` and
`PubSubStore`), an in-memory stream provider (`StreamProvider`), and in-memory reminders.

---

## Kubernetes

The `Kubernetes` module (namespace `Orleans.FSharp.Kubernetes`) configures silo clustering
for Kubernetes deployments.  It uses reflection to call the Orleans Kubernetes extension
method, so the NuGet package `Microsoft.Orleans.Hosting.Kubernetes` remains an optional
runtime dependency — your silo project only needs it when deployed to Kubernetes.

### Standard Kubernetes clustering

```fsharp
open Orleans.FSharp.Kubernetes

// Returns ISiloBuilder -> ISiloBuilder; apply during silo configuration
let configure = Kubernetes.useKubernetesClustering

// Wire it into a siloConfig manually when you need more control
builder.UseOrleans(fun siloBuilder ->
    siloBuilder |> configure |> ignore) |> ignore
```

### Multi-tenant: Kubernetes clustering with a custom namespace

```fsharp
// Uses the Kubernetes namespace as the Orleans ServiceId
let configure = Kubernetes.useKubernetesClusteringWithNamespace "my-k8s-namespace"

builder.UseOrleans(fun siloBuilder ->
    siloBuilder |> configure |> ignore) |> ignore
```

### How it works

Both functions search all loaded assemblies for the `UseKubernetesHosting` extension method
at runtime.  If the package is not found, an `InvalidOperationException` is thrown with a
clear message naming the missing NuGet package.  This keeps the core library free of a hard
assembly dependency on the Kubernetes hosting package.

### API

| Function | Signature | Description |
|---|---|---|
| `Kubernetes.useKubernetesClustering` | `ISiloBuilder -> ISiloBuilder` | Configure Kubernetes clustering (uses Kubernetes API for silo discovery) |
| `Kubernetes.useKubernetesClusteringWithNamespace` | `string -> ISiloBuilder -> ISiloBuilder` | Same, but sets `ClusterOptions.ServiceId` to the given namespace |

---

## Next steps

- [Legacy API](/orleans-fsharp/legacy/) -- maintenance documentation for earlier authoring models
- [Silo Configuration](/orleans-fsharp/silo-configuration/) -- configure providers for these features
- [API Reference](/orleans-fsharp/api-reference/) -- complete list of all public types and functions
