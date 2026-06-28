# Quick Reference: Complete CE Keyword Tables

This document contains exhaustive keyword references for all Orleans.FSharp computation expressions.

---

## `grain { }` — Grain Definition

### Handler Keywords

| Keyword | Signature | Description |
|---|---|---|
| `defaultState` | `state` | Set the initial state value |
| `handle` | `state -> msg -> Task<state * obj>` | Register a message handler with manual boxing |
| `handleState` | `state -> msg -> Task<state>` | Simpler handler — result IS the new state (no manual box) |
| `handleTyped` | `state -> msg -> Task<state * 'R>` | Typed result without manual boxing: use with `ask` |
| `handleWithContext` | `GrainContext -> state -> msg -> Task<state * obj>` | Handler with `GrainContext` for grain-to-grain calls and DI |
| `handleStateWithContext` | `GrainContext -> state -> msg -> Task<state>` | `GrainContext` + state-only result |
| `handleTypedWithContext` | `GrainContext -> state -> msg -> Task<state * 'R>` | `GrainContext` + typed result |
| `handleWithServices` | Alias for `handleWithContext` | Emphasizes DI access |
| `handleStateWithServices` | Alias for `handleStateWithContext` | Services + state-only result |
| `handleTypedWithServices` | Alias for `handleTypedWithContext` | Services + typed result |
| `handleCancellable` | `state -> msg -> CancellationToken -> Task<state * obj>` | Handler with `CancellationToken` support |
| `handleStateCancellable` | `state -> msg -> CancellationToken -> Task<state>` | State-only result + cancellation |
| `handleTypedCancellable` | `state -> msg -> CancellationToken -> Task<state * 'R>` | Typed result + cancellation |
| `handleWithContextCancellable` | `GrainContext -> state -> msg -> CancellationToken -> Task<state * obj>` | Context + cancellation |
| `handleWithServicesCancellable` | Alias for `handleWithContextCancellable` | Services + cancellation |
| `handleStateWithContextCancellable` | `GrainContext -> state -> msg -> CancellationToken -> Task<state>` | Context + state-only + cancellation |
| `handleTypedWithContextCancellable` | `GrainContext -> state -> msg -> CancellationToken -> Task<state * 'R>` | Context + typed result + cancellation |

### Persistence Keywords

| Keyword | Description |
|---|---|
| `persist` | Name the storage provider for state persistence |
| `additionalState` | Declare a named secondary persistent state |

### Lifecycle Hooks

| Keyword | Description |
|---|---|
| `onActivate` | Hook that runs on grain activation |
| `onDeactivate` | Hook that runs on grain deactivation |
| `onReminder` | Register a named reminder handler |
| `onTimer` | Register a declarative timer with dueTime + period |
| `onLifecycleStage` | Hook into grain lifecycle stages |

### Concurrency Keywords

| Keyword | Description |
|---|---|
| `reentrant` | Allow concurrent message processing |
| `interleave` | Mark a method as always interleaved |
| `readOnly` | Mark a method as read-only (interleaved for reads) |
| `mayInterleave` | Custom reentrancy predicate |

### Worker & Placement Keywords

| Keyword | Description |
|---|---|
| `statelessWorker` | Allow multiple activations per silo |
| `maxActivations` | Cap local worker count |
| `oneWay` | Mark a method as fire-and-forget |
| `grainType` | Set a custom grain type name |
| `deactivationTimeout` | Per-grain idle timeout |
| `implicitStreamSubscription` | Auto-subscribe to a stream namespace |
| `preferLocalPlacement` | Place grain on the calling silo |
| `randomPlacement` | Random silo placement |
| `hashBasedPlacement` | Consistent-hash placement |
| `activationCountPlacement` | Fewest-activations placement |
| `resourceOptimizedPlacement` | Resource-aware placement |
| `siloRolePlacement` | Role-based silo targeting |
| `customPlacement` | Custom placement strategy type |

---

## `siloConfig { }` — Silo Configuration

### Clustering Keywords

| Keyword | Description |
|---|---|
| `useLocalhostClustering` | Local dev clustering |
| `addRedisClustering` | Redis-based clustering |
| `addAzureTableClustering` | Azure Table clustering |
| `addAdoNetClustering` | ADO.NET clustering (Postgres, SQL Server) |

### Storage Keywords

| Keyword | Description |
|---|---|
| `addMemoryStorage` | In-memory grain storage |
| `addRedisStorage` | Redis grain storage |
| `addAzureBlobStorage` | Azure Blob grain storage |
| `addAzureTableStorage` | Azure Table grain storage |
| `addAdoNetStorage` | ADO.NET grain storage |
| `addCosmosStorage` | Cosmos DB grain storage |
| `addDynamoDbStorage` | DynamoDB grain storage |
| `addCustomStorage` | Custom storage provider |

### Streaming Keywords

| Keyword | Description |
|---|---|
| `addMemoryStreams` | In-memory stream provider |
| `addPersistentStreams` | Durable stream provider |
| `addBroadcastChannel` | Broadcast channel provider |

### Reminder Keywords

| Keyword | Description |
|---|---|
| `addMemoryReminderService` | In-memory reminders |
| `addRedisReminderService` | Redis reminders |
| `addCustomReminderService` | Custom reminder service |

### Security Keywords

| Keyword | Description |
|---|---|
| `useTls` | TLS encryption |
| `useTlsWithCertificate` | TLS with custom certificate |
| `useMutualTls` | Mutual TLS (client certificate validation) |
| `useMutualTlsWithCertificate` | Mutual TLS with custom certificate |

### Infrastructure Keywords

| Keyword | Description |
|---|---|
| `useSerilog` | Wire Serilog as logging provider |
| `configureServices` | Register custom DI services |
| `addIncomingFilter` | Incoming grain call filter |
| `addOutgoingFilter` | Outgoing grain call filter |
| `addGrainService` | Register a GrainService type |
| `addStartupTask` | Run a task when the silo starts |
| `enableHealthChecks` | Register health check endpoints |
| `addDashboard` | Add Orleans Dashboard with default options |
| `addDashboardWithOptions` | Add Orleans Dashboard with custom options |
| `useGrainVersioning` | Enable grain interface versioning |

### Identity & Network Keywords

| Keyword | Description |
|---|---|
| `clusterId` | Set cluster identity |
| `serviceId` | Set service identity |
| `siloName` | Set silo name |
| `siloPort` | Set silo communication port |
| `gatewayPort` | Set client gateway port |
| `advertisedIpAddress` | Set advertised IP address |
| `grainCollectionAge` | Set global idle deactivation timeout |

---

## `clientConfig { }` — Client Configuration

| Keyword | Description |
|---|---|
| `useLocalhostClustering` | Local dev clustering |
| `useStaticClustering` | Static gateway endpoints |
| `addMemoryStreams` | In-memory stream provider |
| `configureServices` | Register custom DI services |
| `useTls` | TLS encryption |
| `useTlsWithCertificate` | TLS with custom certificate |
| `useMutualTls` | Mutual TLS |
| `clusterId` | Set cluster identity |
| `serviceId` | Set service identity |
| `gatewayListRefreshPeriod` | Gateway refresh interval |
| `preferredGatewayIndex` | Preferred gateway |

---

## `eventSourcedGrain { }` — Event Sourcing

| Keyword | Signature | Description |
|---|---|---|
| `defaultState` | `state` | Initial state before any events |
| `apply` | `state -> event -> state` | Pure event fold: apply event to state |
| `handle` | `state -> command -> event list` | Command handler: return events to apply |
| `logConsistencyProvider` | provider name | Orleans log consistency provider name |

---

## Universal Grain Pattern

Call grains without per-grain C# interfaces:

```fsharp
// Register grain
siloBuilder.Services.AddFSharpGrain<State, Command>(grainDef) |> ignore

// Get reference
let handle = FSharpGrain.ref<State, Command> factory "grain-id"

// String key operations
let! state  = handle |> FSharpGrain.send Command          // returns Task<State>
let! result = handle |> FSharpGrain.ask<State, Cmd, 'R> Query  // typed result
do! handle  |> FSharpGrain.post Command                   // fire-and-forget

// GUID key operations
let h = FSharpGrain.refGuid<State, Command> factory (Guid.NewGuid())
let! s = h |> FSharpGrain.sendGuid Command
let! r = h |> FSharpGrain.askGuid<State, Cmd, 'R> Query

// Integer key operations
let h = FSharpGrain.refInt<State, Command> factory 42L
do! h |> FSharpGrain.postInt Command
```

---

## See Also

- [Getting Started](docs/getting-started.md) — Complete tutorial
- [Grain Definition Reference](docs/grain-definition.md) — Detailed grain definition guide
- [Silo Configuration Reference](docs/silo-configuration.md) — Detailed silo configuration guide
- [Client Configuration Reference](docs/client-configuration.md) — Detailed client configuration guide
- [API Reference](docs/api-reference.md) — All public modules, types, and functions
