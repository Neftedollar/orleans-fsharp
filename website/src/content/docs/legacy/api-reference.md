---
title: "Legacy: API Reference"
description: "Reference for the original Orleans.FSharp authoring APIs."
---

# Legacy API Reference

> **Archived and unsupported.** This material is retained only to help migrate existing
> applications. There is no new Legacy release line, feature or compatibility work, or security
> fixes. New development must use the current functional API.

**Reference for the original Orleans.FSharp authoring APIs kept for existing applications.**

> This page records the 4.1-and-earlier surface for migration. It is not a supported or newly
> released 5.0 API.

## Orleans.FSharp.EventSourcing

The historical `eventSourcedGrain { }` CE and its `JournaledGrain` bridge are not the same thing as
the current `journaledGrainFor` definition builder. The old path needs the archived CodeGen
package and is not published in the 5.0 package set. See [Event Sourcing](/orleans-fsharp/event-sourcing/).

### Types

| Type | Description |
|---|---|
| `EventSourcedGrainDefinition<'State, 'Event, 'Command>` | Event-sourced grain specification (`DefaultState`, `Apply`, `Handle`, `ConsistencyProvider`, `CustomStorage`, `SnapshotStrategy`) |
| `SnapshotStrategy<'State>` | `Never`, `Every of int`, `Condition of (int -> 'State -> bool)` |
| `CustomStorageAdapter` | Boxed read/write pair for a custom log-consistency store |
| `IEventStoreContext<'Event>` | Event store abstraction for the C# CodeGen bridge (`RaiseEvent`, `ConfirmEvents`, `Version`) |
| `FSharpEventSourcedGrain<'State,'Event,'Command>` | Generic `JournaledGrain` base bridging a definition to Orleans |
| `FSharpEventSourcedGrainAttribute` | Binds an implementation to a grain interface |

### Computation expressions

| CE | Builder | Description |
|---|---|---|
| `eventSourcedGrain { }` | `EventSourcedGrainBuilder` | Define event-sourced grain behavior |

#### `eventSourcedGrain { }` — keywords

| Keyword | Signature | Description |
|---|---|---|
| `defaultState` | `'State` | Initial state value |
| `apply` | `'State -> 'Event -> 'State` | The pure fold |
| `handle` | `'State -> 'Command -> 'Event list` | Command handler; an empty list is a refusal |
| `logConsistencyProvider` | `string` | Named Orleans log-consistency provider |
| `snapshot` | `SnapshotStrategy<'State>` | Snapshot strategy (honoured only by a custom store) |
| `customStorage` | `read` + `write` | Custom log-consistency storage pair |

#### `EventSourcedGrainDefinition`

| Function | Signature | Description |
|---|---|---|
| `foldEvents` | `definition -> 'State -> 'Event list -> 'State` | Replay events onto state |
| `handleCommand` | `definition -> 'State -> 'Command -> 'State * 'Event list` | Process a command, returning the folded state and the events |

#### `EventStore`

| Function | Signature | Description |
|---|---|---|
| `processCommand` | `definition -> 'State -> 'Command -> 'Event list` | Produce events from a command |
| `applyEvent` | `definition -> 'State -> 'Event -> 'State` | Apply a single event |
| `replayEvents` | `definition -> 'State -> 'Event list -> 'State` | Replay an event list |
| `shouldSnapshot` | `definition -> int -> 'State -> bool` | Evaluate the snapshot strategy |

#### Registration

| Method | Signature | Description |
|---|---|---|
| `AddFSharpEventSourcedGrain<'State,'Event,'Command>` | `IServiceCollection -> definition -> IServiceCollection` | Register one definition |
| `AddFSharpEventSourcedGrainsFromAssembly` | `IServiceCollection -> Assembly -> IServiceCollection` | Register every definition an assembly declares |

---

## Deprecated: the `grain { }` cluster

Everything below carries `[<Obsolete>]` (warning, not error) and is kept runnable. The replacement
for each entry is the [functional grain runtime](/orleans-fsharp/functional-runtime/); see
[API Migration](/orleans-fsharp/legacy/migration/) for the rewrite recipe.

### Types

| Type | Description | Replacement |
|---|---|---|
| `GrainDefinition<'State, 'Message>` | Immutable record describing a grain's behavior | `FunctionalGrainDefinition<...>` from `grainFor` |
| `GrainContext` | Grain factory, service provider, and named states | `FunctionalGrainContext<'Actor,'Key>` |
| `AdditionalStateSpec` | Named additional persistent state specification | `PersistentState.create` + `usePersistentState` |
| `FSharpGrainAttribute` | Marks a definition for assembly discovery | — (a definition is registered by value) |
| `FSharpGrainHandle<'S,'M>` | Zero-alloc struct handle for a string-keyed grain | `FunctionalGrain.ref` / `rawRef` |
| `FSharpGrainGuidHandle<'S,'M>` | Zero-alloc struct handle for a GUID-keyed grain | `FunctionalGrain.ref` / `rawRef` |
| `FSharpGrainIntHandle<'S,'M>` | Zero-alloc struct handle for an int64-keyed grain | `FunctionalGrain.ref` / `rawRef` |

### Computation expressions

| CE | Builder | Description |
|---|---|---|
| `grain { }` | `GrainBuilder` | Define grain behavior declaratively |

#### `grain { }` — key CE keywords

| Keyword | Handler Signature | Description |
|---|---|---|
| `defaultState` | `'State` | Initial state value |
| `handle` | `'State -> 'Msg -> Task<'State * obj>` | Register handler with manual `box` |
| `handleState` | `'State -> 'Msg -> Task<'State>` | Handler returning only state (no result value) |
| `handleTyped` | `'State -> 'Msg -> Task<'State * 'R>` | Handler with typed result — no `box` needed |
| `handleWithContext` | `GrainContext -> 'State -> 'Msg -> Task<'State * obj>` | Handler with DI/grain-to-grain access |
| `handleStateWithContext` | `GrainContext -> 'State -> 'Msg -> Task<'State>` | Context + state-only return |
| `handleTypedWithContext` | `GrainContext -> 'State -> 'Msg -> Task<'State * 'R>` | Context + typed result |
| `handleCancellable` | `'State -> 'Msg -> CancellationToken -> Task<'State * obj>` | Cancellation, manual `box` |
| `handleStateCancellable` | `'State -> 'Msg -> CancellationToken -> Task<'State>` | Cancellation, state-only return |
| `handleTypedCancellable` | `'State -> 'Msg -> CancellationToken -> Task<'State * 'R>` | Cancellation, typed result |
| `handleWithContextCancellable` | `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State * obj>` | Context + cancellation |
| `handleStateWithContextCancellable` | `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State>` | Context + cancellation, state-only return |
| `handleTypedWithContextCancellable` | `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State * 'R>` | Context + cancellation, typed result |
| `persist` | `string` | Name of the storage provider for state |
| `additionalState<'T>` | `string` (name) + `string` (storage) + `'T` (default) | Named additional persistent state |
| `onActivate` | `'State -> Task<'State>` | Activation hook; may replace the state |
| `onDeactivate` | `'State -> Task<unit>` | Deactivation hook; cleanup only |
| `onReminder` | `string` + `('State -> string -> TickStatus -> Task<'State>)` | Named reminder with a stateful handler |
| `onTimer` | `string` + `TimeSpan` (due) + `TimeSpan` (period) + `('State -> Task<'State>)` | Declarative timer |
| `onLifecycleStage` | `int` + `(CancellationToken -> Task<unit>)` | Hook a raw Orleans grain-lifecycle stage number |
| `interleaveMessage` | `System.Type` | Allow a message type to interleave (`interleaveMessage typeof<Query>`) |

Each `handle*` keyword also has a `*WithServices` form (`handleWithServices`,
`handleStateWithServices`, `handleTypedWithServices`, and their `Cancellable` variants) taking an
`IServiceProvider` instead of a `GrainContext`. See
[legacy Grain Definition guide](/orleans-fsharp/legacy/grain-definition/) for the full keyword list. Per-grain Orleans
attributes (`[Reentrant]`, `[StatelessWorker]`, placement, `[OneWay]`, `[ReadOnly]`,
`[ImplicitStreamSubscription]`, …) are applied via the C# CodeGen path, not `grain { }` keywords.
On the [functional grain runtime](/orleans-fsharp/functional-grains/) they are ordinary contract and definition
operations instead — `reentrant`, `statelessWorker`, `placement`, `oneWay`, `readOnly`, and
`onStream` / `onBroadcast` for implicit subscriptions.

### Modules

#### `GrainContext`

| Function | Signature | Description |
|---|---|---|
| `getService<'T>` | `GrainContext -> 'T` | Resolve a DI service |
| `getState<'T>` | `GrainContext -> string -> IPersistentState<'T>` | Get named additional persistent state |
| `getGrainByString<'T>` | `GrainContext -> string -> GrainRef<'T, string>` | Get grain ref by string key |
| `getGrainByGuid<'T>` | `GrainContext -> Guid -> GrainRef<'T, Guid>` | Get grain ref by GUID key |
| `getGrainByInt64<'T>` | `GrainContext -> int64 -> GrainRef<'T, int64>` | Get grain ref by int64 key |
| `getGrainByGuidCompound<'T>` | `GrainContext -> Guid -> string -> GrainRef<'T, CompoundGuidKey>` | Compound GUID key |
| `getGrainByIntCompound<'T>` | `GrainContext -> int64 -> string -> GrainRef<'T, CompoundIntKey>` | Compound int64 key |
| `deactivateOnIdle` | `GrainContext -> unit` | Request grain deactivation when idle |
| `delayDeactivation` | `GrainContext -> TimeSpan -> unit` | Delay grain deactivation |
| `grainId` | `GrainContext -> GrainId` | Get the GrainId |
| `primaryKeyString` | `GrainContext -> string` | Get string primary key |
| `primaryKeyGuid` | `GrainContext -> Guid` | Get Guid primary key |
| `primaryKeyInt64` | `GrainContext -> int64` | Get int64 primary key |
| `empty` | `GrainContext` | Empty context for unit tests (all fields null/None) |

#### `GrainDefinition`

| Function | Signature | Description |
|---|---|---|
| `hasAnyHandler` | `GrainDefinition -> bool` | True if any handler is registered |
| `getHandler` | `GrainDefinition -> 'State -> 'Message -> Task<'State * obj>` | Get plain handler |
| `getContextHandler` | `GrainDefinition -> GrainContext -> 'State -> 'Message -> Task<'State * obj>` | Get context-aware handler |
| `getCancellableContextHandler` | `GrainDefinition -> GrainContext -> 'State -> 'Message -> CT -> Task<'State * obj>` | Get cancellable context handler |
| `invokeHandler` | `GrainDefinition -> 'State -> 'Message -> Task<'State * obj>` | Invoke handler (C# interop) |
| `invokeContextHandler` | `GrainDefinition -> GrainContext -> 'State -> 'Message -> Task<'State * obj>` | Invoke context handler (C# interop) |
| `invokeCancellableContextHandler` | `GrainDefinition -> GrainContext -> 'State -> 'Message -> CT -> Task<'State * obj>` | Invoke cancellable (C# interop) |
| `invokeOnActivate` | `GrainDefinition -> 'State -> Task<'State>` | Run the activation hook directly |
| `invokeOnDeactivate` | `GrainDefinition -> 'State -> Task` | Run the deactivation hook directly |
| `invokeReminderHandler` | `GrainDefinition -> 'State -> string -> TickStatus -> Task<'State>` | Run one named reminder handler directly |

#### `Reminder`

| Function | Signature | Description |
|---|---|---|
| `register` | `Grain -> string -> TimeSpan -> TimeSpan -> Task<IGrainReminder>` | Register/update reminder |
| `unregister` | `Grain -> string -> Task<unit>` | Unregister reminder |
| `get` | `Grain -> string -> Task<IGrainReminder option>` | Get reminder by name |

Replacement: `onReminder` on a functional definition, which reconciles declared reminders on every
activation.

#### `Timers`

| Function | Signature | Description |
|---|---|---|
| `register` | `Grain -> (CT -> Task<unit>) -> TimeSpan -> TimeSpan -> IGrainTimer` | Register timer |
| `registerWithState<'T>` | `Grain -> ('T -> CT -> Task<unit>) -> 'T -> TimeSpan -> TimeSpan -> IGrainTimer` | Timer with state |

Replacement: `onTimer` on a functional definition.

#### `FSharpGrain` — universal grain pattern

Registered once with `AddFSharpGrain`, called from anywhere with `FSharpGrain.ref`. Replacement:
`grainContract` + `grainFor` + `FunctionalGrain.ref`, which types the reply per operation instead of
boxing one message DU.

| Function | Signature | Description |
|---|---|---|
| `FSharpGrain.ref<'S,'M>` | `IGrainFactory -> string -> FSharpGrainHandle<'S,'M>` | Handle for string-keyed grain |
| `FSharpGrain.refGuid<'S,'M>` | `IGrainFactory -> Guid -> FSharpGrainGuidHandle<'S,'M>` | Handle for GUID-keyed grain |
| `FSharpGrain.refInt<'S,'M>` | `IGrainFactory -> int64 -> FSharpGrainIntHandle<'S,'M>` | Handle for int64-keyed grain |
| `FSharpGrain.send<'S,'M>` | `'M -> FSharpGrainHandle<'S,'M> -> Task<'S>` | Send command, return typed state |
| `FSharpGrain.post<'S,'M>` | `'M -> FSharpGrainHandle<'S,'M> -> Task` | Fire-and-forget command |
| `FSharpGrain.ask<'S,'M,'R>` | `'M -> FSharpGrainHandle<'S,'M> -> Task<'R>` | Send command, return typed result (can differ from state) |
| `FSharpGrain.sendGuid<'S,'M>` | `'M -> FSharpGrainGuidHandle<'S,'M> -> Task<'S>` | Send to GUID-keyed grain |
| `FSharpGrain.postGuid<'S,'M>` | `'M -> FSharpGrainGuidHandle<'S,'M> -> Task` | Post to GUID-keyed grain |
| `FSharpGrain.askGuid<'S,'M,'R>` | `'M -> FSharpGrainGuidHandle<'S,'M> -> Task<'R>` | Ask GUID-keyed grain for typed result |
| `FSharpGrain.sendInt<'S,'M>` | `'M -> FSharpGrainIntHandle<'S,'M> -> Task<'S>` | Send to int64-keyed grain |
| `FSharpGrain.postInt<'S,'M>` | `'M -> FSharpGrainIntHandle<'S,'M> -> Task` | Post to int64-keyed grain |
| `FSharpGrain.askInt<'S,'M,'R>` | `'M -> FSharpGrainIntHandle<'S,'M> -> Task<'R>` | Ask int64-keyed grain for typed result |

DI registration (call once per grain definition at silo startup):

```fsharp
// Automatically registers FSharpBinaryCodec (idempotent)
services.AddFSharpGrain<CounterState, CounterCommand>(counterGrain) |> ignore
```

`AddFSharpGrainsFromAssembly` registers every `[<FSharpGrain>]`-marked definition an assembly
declares.

#### Testing helpers for the deprecated model

| Function | Signature | Description |
|---|---|---|
| `GrainMock.withFSharpGrain<'S,'M>` | `string -> GrainDefinition<'S,'M> -> MockGrainFactory -> MockGrainFactory` | Register an F# grain definition as a mock, by string key |
| `GrainMock.withFSharpGrainGuid<'S,'M>` | `Guid -> GrainDefinition<'S,'M> -> MockGrainFactory -> MockGrainFactory` | The same, by GUID key |
| `GrainMock.withFSharpGrainInt<'S,'M>` | `int64 -> GrainDefinition<'S,'M> -> MockGrainFactory -> MockGrainFactory` | The same, by int64 key |
| `TestHarness.getFSharpGrain<'S,'M>` | `TestHarness -> string -> FSharpGrainHandle<'S,'M>` | Handle from a test cluster, by string key |
| `TestHarness.getFSharpGrainGuid<'S,'M>` | `TestHarness -> Guid -> FSharpGrainGuidHandle<'S,'M>` | The same, by GUID key |
| `TestHarness.getFSharpGrainInt<'S,'M>` | `TestHarness -> int64 -> FSharpGrainIntHandle<'S,'M>` | The same, by int64 key |

### Other compatibility helpers

The following helpers support C# CodeGen interfaces or the older class-based transactional model;
they are not part of the functional authoring surface.

| API | Purpose | Functional replacement |
|---|---|---|
| `GrainRef.ofString/ofGuid/ofInt64` and `GrainRef.invoke` | Wrap a generated C# grain interface | `FunctionalGrain.ref` / `rawRef` |
| `GrainState.read/write/clear/current` | Operate directly on an injected `IPersistentState` | `PersistentState.create`, `stateFrom`, and `context.persistentState` |
| `Observer.createRef/deleteRef/subscribe` and `FSharpObserverManager` | Generated C# observer interfaces | `observerContract`, `FunctionalObserver`, and `FunctionalObserverManager` |
| `Transactions.TransactionalState` | Wrap an injected `ITransactionalState` | `transactionalStateFrom` and `FunctionalTransactionalState` |

### CodeGen interface versioning

These settings apply to Legacy C# CodeGen grain interfaces. Current functional contracts use
`version`, `acceptsVersions`, and `sinceVersion` on `grainContract` instead.

| API | Members |
|---|---|
| `CompatibilityStrategy` | `BackwardCompatible`, `StrictVersion`, `AllVersions` |
| `VersionSelectorStrategy` | `AllCompatibleVersions`, `LatestVersion`, `MinimumVersion` |
| `Versioning` | `compatibilityStrategyName`, `versionSelectorStrategyName` |
| `TransactionalGrainDefinition`, `FSharpTransactionalGrain`, `AtmGrainDefinition`, `FSharpAtmGrain` | Class-based transactional grains | Contract-level `transactional` plus `transactionalStateFrom` |
