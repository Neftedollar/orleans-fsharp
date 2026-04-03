# Data Model: F# Idiomatic API Layer for Orleans

**Date**: 2026-04-02
**Branch**: `001-fsharp-orleans-api`

## Core Types

### GrainDefinition<'State, 'Message>

The central abstraction — a description of grain behavior.

**Fields**:
- `StateType`: Type descriptor for the grain's DU state
- `InitialState`: `'State` — default state on first activation
- `Handlers`: `Map<TypeId, 'State -> 'Message -> Task<'State * 'Result>>` — message dispatch table
- `OnActivate`: `'State -> Task<'State>` option — lifecycle hook
- `OnDeactivate`: `'State -> Task<unit>` option — lifecycle hook
- `PersistenceName`: `string option` — storage provider name (None = transient)

**Validation rules**:
- At least one handler MUST be defined
- `InitialState` MUST be a valid case of the state DU
- All DU cases of `'Message` MUST have a corresponding handler (exhaustive)

**State transitions**:
```
Unregistered → Defined (via grain { } CE)
Defined → Registered (at silo startup, discovered by Orleans)
Registered → Active (first message received)
Active → Deactivating (idle timeout or silo shutdown)
Deactivating → Inactive (state persisted if configured)
Inactive → Active (next message reactivates)
```

---

### GrainState<'T>

Wrapper over Orleans `IPersistentState<T>` for F# immutable values.

**Fields**:
- `Current`: `'T` — current immutable state value
- `IsDirty`: `bool` — true if changed since last write
- `Etag`: `string option` — optimistic concurrency tag

**Operations**:
- `read: unit -> Task<'T>` — reload from storage
- `write: 'T -> Task<unit>` — persist new state
- `clear: unit -> Task<unit>` — delete persisted state

**Validation rules**:
- `'T` MUST be serializable (has `[<GenerateSerializer>]` or registered converter)
- `write` MUST be called within grain activation context (not from external code)

---

### GrainRef<'TInterface, 'TKey>

Type-safe reference to a remote grain.

**Fields**:
- `Key`: `'TKey` — the grain's identity (string | Guid | int64)
- `Interface`: `'TInterface` — compile-time interface constraint

**Operations**:
- `invoke: ('TInterface -> Task<'R>) -> Task<'R>` — call a grain method
- `cast<'TOther> : unit -> GrainRef<'TOther, 'TKey>` — interface cast

**Constraints**:
- `'TInterface` MUST extend `IGrain`
- `'TKey` MUST be one of: `string`, `Guid`, `int64`

---

### StreamRef<'T>

Typed reference to an Orleans stream.

**Fields**:
- `Namespace`: `string` — stream namespace
- `Key`: `string` — stream key within namespace
- `Provider`: `string` — stream provider name

**Operations**:
- `publish: 'T -> Task<unit>` — emit an event
- `subscribe: ('T -> Task<unit>) -> Task<StreamSubscription>` — callback subscription
- `asTaskSeq: unit -> TaskSeq<'T>` — pull-based consumption

**Constraints**:
- `'T` MUST be serializable
- Subscription state is persisted with the owning grain

---

### SiloConfig

Composable silo configuration descriptor.

**Fields**:
- `ClusteringMode`: `Localhost | AzureTable of connStr | Redis of connStr | Custom of (ISiloBuilder -> ISiloBuilder)`
- `StorageProviders`: `Map<string, StorageProvider>`
- `StreamProviders`: `Map<string, StreamProvider>`
- `LoggingConfig`: `LoggingConfig`
- `CustomServices`: `(IServiceCollection -> unit) list`

**StorageProvider** (sub-type):
- `Memory`
- `AzureBlob of connStr`
- `AzureTable of connStr`
- `AdoNet of connStr * invariant`
- `Custom of (ISiloBuilder -> ISiloBuilder)`

---

### TestHarness

In-process test environment.

**Fields**:
- `Cluster`: `InProcessTestCluster option` — for integration tests
- `LogCapture`: `CapturedLogEntry list` — intercepted log entries
- `StorageSnapshot`: `Map<string, obj>` — grain state snapshots

**Operations**:
- `activateGrain: GrainRef<'T, 'K> -> Task<'T>` — activate in test context
- `getState<'S>: GrainRef<_, _> -> 'S` — inspect current grain state
- `getLogs: unit -> CapturedLogEntry list` — retrieve captured logs
- `reset: unit -> Task<unit>` — clear all state and logs

## Entity Relationships

```
SiloConfig ──configures──> Orleans Silo
                              │
                              ├── discovers ──> GrainDefinition<'S,'M>
                              │                      │
                              │                      ├── manages ──> GrainState<'S>
                              │                      └── handles ──> 'M (messages)
                              │
                              └── hosts ──> StreamRef<'T>
                                               │
GrainRef<'I,'K> ──references──> GrainDefinition │
                                                │
TestHarness ──wraps──> Orleans Silo (in-process) │
    │                                            │
    └── captures ──> Logs, State, Streams ───────┘
```
