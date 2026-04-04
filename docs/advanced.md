# Advanced Topics

**Transactions, grain directory, OpenTelemetry, shutdown, state migration, serialization, and behavior pattern.**

## What you'll learn

- How to use Orleans transactions from F#
- How to configure the grain directory
- How to integrate with OpenTelemetry
- How to perform graceful shutdown
- How to migrate grain state between versions
- How to configure F# type serialization
- How to model grains as phase state machines with the behavior pattern

---

## Transactions

The `Orleans.FSharp.Transactions` module provides F# wrappers for Orleans transactional state.

### Transaction options

```fsharp
open Orleans.FSharp.Transactions

// These map to Orleans [Transaction(option)] attributes in CodeGen
type TransactionOption =
    | Create           // Always creates a new transaction
    | Join             // Must run within an existing transaction
    | CreateOrJoin     // Joins if exists, creates otherwise
    | Supported        // Not transactional but can be called within one
    | NotAllowed       // Cannot be called within a transaction
    | Suppress         // Suppresses any ambient transaction
```

### Reading transactional state

```fsharp
let! currentBalance = TransactionalState.read accountState
```

### Updating transactional state

```fsharp
do! TransactionalState.update
    (fun state -> { state with Balance = state.Balance + amount })
    accountState
```

### Performing a read with projection

```fsharp
let! balance =
    TransactionalState.performRead
        (fun state -> state.Balance)
        accountState
```

### Converting options

```fsharp
let orleansOption = TransactionOption.toOrleans CreateOrJoin
// Returns Orleans.TransactionOption.CreateOrJoin
```

---

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

The `Telemetry` module provides constants and helpers for OpenTelemetry integration.

### Activity source names

```fsharp
open Orleans.FSharp

// Add these to your OpenTelemetry tracing configuration
Telemetry.runtimeActivitySourceName       // "Microsoft.Orleans.Runtime"
Telemetry.applicationActivitySourceName   // "Microsoft.Orleans.Application"
Telemetry.activitySourceNames             // Both as a list
```

### Meter name

```fsharp
Telemetry.meterName  // "Microsoft.Orleans"
```

### Enable activity propagation

```fsharp
Telemetry.enableActivityPropagation siloBuilder |> ignore
```

### Full OpenTelemetry setup

```fsharp
open OpenTelemetry.Trace
open OpenTelemetry.Metrics
open Orleans.FSharp

builder.Services
    .AddOpenTelemetry()
    .WithTracing(fun tracing ->
        tracing
            .AddSource(Telemetry.runtimeActivitySourceName)
            .AddSource(Telemetry.applicationActivitySourceName)
            .AddOtlpExporter()
        |> ignore)
    .WithMetrics(fun metrics ->
        metrics
            .AddMeter(Telemetry.meterName)
            .AddOtlpExporter()
        |> ignore)
|> ignore
```

---

## Graceful Shutdown

The `Shutdown` module provides helpers for clean silo shutdown.

### Configure drain timeout

```fsharp
open Orleans.FSharp

Shutdown.configureGracefulShutdown (TimeSpan.FromSeconds 30.) hostBuilder
|> ignore
```

### Stop the host

```fsharp
do! Shutdown.stopHost host
```

### Register a shutdown handler

```fsharp
Shutdown.onShutdown (fun ct ->
    task {
        printfn "Silo is shutting down..."
        // Clean up resources
    }) hostBuilder
|> ignore
```

Multiple shutdown handlers can be registered; they run in registration order.

---

## State Migration

The `StateMigration` module enables upgrading grain state schemas across deployments.

### Define migrations

```fsharp
open Orleans.FSharp

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

// Upgrade from v1 to latest (throws if chain is invalid)
let currentState : CounterStateV3 =
    StateMigration.applyMigrations<CounterStateV3> migrations 1 (box oldV1State)

// Safe version — validate and apply in one call, returns Result
match StateMigration.tryApplyMigrations<CounterStateV3> migrations 1 (box oldV1State) with
| Ok newState -> // use newState
| Error errs  -> for e in errs do log.LogError("Migration error: {Error}", e)
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
let myOptions = JsonSerializerOptions()
Serialization.addFSharpConverters myOptions |> ignore
```

### Create options with extra converters

```fsharp
let options = Serialization.withConverters [ myCustomConverter ]
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

## Grain Extensions

Get a grain extension reference for adding behavior to existing grains:

```fsharp
open Orleans.FSharp

let extension = GrainExtension.getExtension<IMyExtension> grainRef
```

---

## Grain State Operations

The `GrainState` module wraps `IPersistentState<'T>` for idiomatic F# access:

```fsharp
open Orleans.FSharp

// Read from storage
let! value = GrainState.read persistentState

// Write to storage
do! GrainState.write persistentState newValue

// Clear storage
do! GrainState.clear persistentState

// Get in-memory value (no I/O)
let current = GrainState.current persistentState
```

---

## Observers

The `Observer` module manages grain observer lifecycle:

```fsharp
open Orleans.FSharp

// Create a grain object reference for a local observer
let observerRef = Observer.createRef<IMyObserver> grainFactory myObserver

// Delete when done (prevents memory leaks)
Observer.deleteRef<IMyObserver> grainFactory observerRef

// Or use subscribe for automatic cleanup via IDisposable
use subscription = Observer.subscribe<IMyObserver> grainFactory myObserver
// observerRef is automatically deleted when subscription is disposed
```

---

## Grain Services

Register background services that run on every silo:

```fsharp
open Orleans.FSharp

GrainServices.addGrainService<MyBackgroundService> siloBuilder |> ignore
```

---

## TaskHelpers

Utility functions for composing Task-based operations with Result:

```fsharp
open Orleans.FSharp

let! result = TaskHelpers.taskResult 42            // Task<Result<int, _>> = Ok 42
let! error = TaskHelpers.taskError "failed"        // Task<Result<_, string>> = Error "failed"

let! mapped =
    TaskHelpers.taskResult 42
    |> TaskHelpers.taskMap (fun n -> n * 2)         // Ok 84

let! bound =
    TaskHelpers.taskResult 42
    |> TaskHelpers.taskBind (fun n ->
        if n > 0 then TaskHelpers.taskResult (n * 2)
        else TaskHelpers.taskError "negative")      // Ok 84
```

---

## Logging

The `Log` module provides structured logging with automatic correlation ID propagation:

```fsharp
open Orleans.FSharp

// Structured log messages
Log.logInfo logger "Processing order {OrderId}" [| box orderId |]
Log.logWarning logger "Slow response from {Service}" [| box serviceName |]
Log.logError logger exn "Failed to process {Command}" [| box command |]
Log.logDebug logger "Cache hit for {Key}" [| box cacheKey |]

// Correlation scopes
do! Log.withCorrelation requestId (fun () ->
    task {
        // All logs in this scope include {CorrelationId}
        Log.logInfo logger "Step 1" [||]
        Log.logInfo logger "Step 2" [||]
    })

// Get current correlation ID
let corrId = Log.currentCorrelationId()  // Some "abc-123" or None
```

---

## Reminders (Module API)

For programmatic reminder management (outside the `grain { }` CE):

```fsharp
open Orleans.FSharp

let! handle = Reminder.register grain "MyReminder" dueTime period
do! Reminder.unregister grain "MyReminder"
let! existing = Reminder.get grain "MyReminder"  // Some handle or None
```

---

## Timers (Module API)

For programmatic timer management (outside the `grain { }` CE):

```fsharp
open Orleans.FSharp

let timer = Timers.register grain callback dueTime period
// Dispose to cancel: timer.Dispose()

let timerWithState = Timers.registerWithState grain callback state dueTime period
```

---

## Behavior Pattern

The **behavior pattern** models a grain as a state machine where the state type includes
a _phase_ discriminated union. The handler dispatches on `(phase, command)` tuples, making
illegal transitions compile errors.

### Define the domain

```fsharp
open Orleans.FSharp

type Phase =
    | WaitingForConfig
    | Running of maxHistory: int
    | Suspended of reason: string

type ChatState = { Phase: Phase; Messages: string list }

type ChatCommand =
    | Configure of maxHistory: int
    | Send of text: string
    | GetHistory
    | Suspend of reason: string
    | Resume
```

### Write the behavior handler

Return `BehaviorResult<'State>` to express transitions:

```fsharp
let chatHandler (state: ChatState) (cmd: ChatCommand) : Task<BehaviorResult<ChatState>> =
    task {
        match state.Phase, cmd with
        | WaitingForConfig, Configure maxHistory ->
            return Become { state with Phase = Running maxHistory }
        | WaitingForConfig, _ ->
            return Stay state
        | Running maxHistory, Send msg ->
            let newMessages = (msg :: state.Messages) |> List.truncate maxHistory
            return Stay { state with Messages = newMessages }
        | Running _, Suspend reason ->
            return Become { state with Phase = Suspended reason }
        | Running _, _ ->
            return Stay state
        | Suspended _, Resume ->
            return Become { state with Phase = Running 50 }
        | Suspended _, _ ->
            return Stay state
    }
```

| Case | Meaning |
|---|---|
| `Stay state` | Keep the current phase, update state. |
| `Become state` | Transition to a new phase (phase is part of state). |
| `Stop` | Signal that this grain should deactivate. |

### Wire it into the grain CE

Use `Behavior.run` to plug the handler into `handleState` without any manual unwrapping:

```fsharp
let chatGrain =
    grain {
        defaultState { Phase = WaitingForConfig; Messages = [] }
        persist "Default"
        handleState (Behavior.run chatHandler)  // one line — no match on BehaviorResult
    }
```

If you need access to `ctx.GrainFactory` or grain-to-grain calls, use `handleStateWithContext`
and `Behavior.runWithContext`. When the handler returns `Stop`, it automatically calls
`ctx.DeactivateOnIdle()`:

```fsharp
let chatHandler' (ctx: GrainContext) (state: ChatState) (cmd: ChatCommand) : Task<BehaviorResult<ChatState>> =
    task {
        match state.Phase, cmd with
        | Running _, Stop -> return Stop   // grain will deactivate after this message
        | _ -> return! chatHandler state cmd  // delegate to the context-free handler
    }

let chatGrain =
    grain {
        defaultState { Phase = WaitingForConfig; Messages = [] }
        handleStateWithContext (Behavior.runWithContext chatHandler')
    }
```

### Helper functions

| Function | Description |
|---|---|
| `Behavior.run handler` | Adapts a `BehaviorResult` handler for `handleState`. Stop returns original state. |
| `Behavior.runWithContext handler` | Adapts a `BehaviorResult` handler for `handleStateWithContext`. Stop calls `DeactivateOnIdle`. |
| `Behavior.unwrap original result` | Extracts state from Stay/Become; returns `original` for Stop. |
| `Behavior.map f result` | Maps a function over the state inside a BehaviorResult. |
| `Behavior.isTransition result` | True if result is `Become`. |
| `Behavior.isStopped result` | True if result is `Stop`. |
| `Behavior.toHandlerResult original result` | Converts to `state * obj` tuple for use with raw `handle`. |

### Testing behavior handlers directly

Because behavior handlers are pure functions returning `Task<BehaviorResult<'State>>`, they
test without a silo — and with `Behavior.run` the whole grain can be driven via `getHandler`:

```fsharp
open Orleans.FSharp
open Xunit
open Swensen.Unquote
open FsCheck.Xunit

[<Fact>]
let ``Configure transitions from WaitingForConfig to Running`` () =
    task {
        let initial = { Phase = WaitingForConfig; Messages = [] }
        let! ns = Behavior.run chatHandler initial (Configure 10)
        test <@ ns.Phase = Running 10 @>
    }

// Property: phase invariants hold for any command sequence
[<Property>]
let ``chat phase invariant`` (commands: ChatCommand list) =
    let mutable s = { Phase = WaitingForConfig; Messages = [] }
    for cmd in commands do
        s <- (Behavior.run chatHandler s cmd).GetAwaiter().GetResult()
    match s.Phase with
    | WaitingForConfig -> true
    | Running n -> n > 0
    | Suspended r -> r.Length > 0
```

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
let! result =
    RequestCtx.withValue<OrderState> "correlationId" (box requestId) (fun () ->
        task {
            // All grain calls made here propagate correlationId automatically
            let handle = FSharpGrain.ref<OrderState, OrderCommand> factory "order-42"
            return! handle |> FSharpGrain.send PlaceOrder
        })
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

The `Scripting` module starts a local in-process silo from an F# script (`.fsx` file), letting
you iterate on grain logic without a full project setup.

### Quick start

```fsharp
#r "nuget: Orleans.FSharp"
#r "nuget: Orleans.FSharp.Runtime"

open Orleans.FSharp
open Orleans.FSharp.Runtime

// Define a grain inline
type PingState = { Count: int }
type PingCmd = Ping | GetCount

let pingGrain =
    grain {
        defaultState { Count = 0 }
        handle (fun state cmd -> task {
            match cmd with
            | Ping     -> return { Count = state.Count + 1 }, box (state.Count + 1)
            | GetCount -> return state, box state.Count
        })
    }

// Start an in-process silo (localhost clustering + in-memory storage)
let! silo = Scripting.quickStart()

// Register and call the grain
silo.Host.Services
    .AddFSharpGrain<PingState, PingCmd>(pingGrain)
    |> ignore

let handle = FSharpGrain.ref<PingState, PingCmd> silo.GrainFactory "ping-1"
let! s1 = handle |> FSharpGrain.send Ping
printfn "Count: %d" s1.Count

// Shut down when done
do! Scripting.shutdown silo
```

### API

| Function | Description |
|---|---|
| `Scripting.quickStart()` | Start a silo on the default ports (11111 / 30000) |
| `Scripting.startOnPorts siloPort gatewayPort` | Start a silo on specific ports (useful when running multiple silos) |
| `Scripting.getGrain<'T> handle key` | Get a grain reference by `int64` key |
| `Scripting.getGrainByString<'T> handle key` | Get a grain reference by `string` key |
| `Scripting.shutdown handle` | Stop the silo and release resources |

The `SiloHandle` returned by `quickStart` exposes `.Host`, `.Client`, and `.GrainFactory` for
direct access when you need lower-level control.

The silo is pre-configured with in-memory storage (`Default` and `PubSubStore`),
an in-memory stream provider (`StreamProvider`), and in-memory reminders.

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

- [Grain Definition](grain-definition.md) -- use these features in grain definitions
- [Silo Configuration](silo-configuration.md) -- configure providers for these features
- [API Reference](api-reference.md) -- complete list of all public types and functions
