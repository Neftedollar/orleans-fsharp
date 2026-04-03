# API Reference

**Quick reference for all public modules, types, and functions in Orleans.FSharp.**

---

## Orleans.FSharp (Core)

### Types

| Type | Description |
|---|---|
| `GrainDefinition<'State, 'Message>` | Immutable record describing a grain's behavior |
| `GrainContext` | Provides access to grain factory, service provider, and named states |
| `GrainRef<'TInterface, 'TKey>` | Type-safe reference to an Orleans grain |
| `CompoundGuidKey` | Compound key: GUID + string extension |
| `CompoundIntKey` | Compound key: int64 + string extension |
| `PlacementStrategy` | DU: Default, PreferLocal, Random, HashBased, ActivationCountBased, ResourceOptimized, SiloRoleBased, Custom |
| `AdditionalStateSpec` | Named additional persistent state specification |
| `Immutable<'T>` | Alias for `Orleans.Concurrency.Immutable<'T>` for zero-copy passing |
| `FSharpIncomingFilter` | Wraps an F# function as `IIncomingGrainCallFilter` |
| `FSharpOutgoingFilter` | Wraps an F# function as `IOutgoingGrainCallFilter` |
| `Migration<'TOld, 'TNew>` | State migration definition from one version to another |
| `AssemblyMarker` | Marker type for assembly discovery |

### Computation Expressions

| CE | Builder | Description |
|---|---|---|
| `grain { }` | `GrainBuilder` | Define grain behavior declaratively |

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

#### `GrainRef`

| Function | Signature | Description |
|---|---|---|
| `ofString<'T>` | `IGrainFactory -> string -> GrainRef<'T, string>` | Create ref by string key |
| `ofGuid<'T>` | `IGrainFactory -> Guid -> GrainRef<'T, Guid>` | Create ref by GUID key |
| `ofInt64<'T>` | `IGrainFactory -> int64 -> GrainRef<'T, int64>` | Create ref by int64 key |
| `ofGuidCompound<'T>` | `IGrainFactory -> Guid -> string -> GrainRef<'T, CompoundGuidKey>` | Compound GUID key |
| `ofIntCompound<'T>` | `IGrainFactory -> int64 -> string -> GrainRef<'T, CompoundIntKey>` | Compound int64 key |
| `invoke` | `GrainRef -> ('T -> Task<'R>) -> Task<'R>` | Call a grain method |
| `invokeOneWay` | `GrainRef -> ('T -> Task) -> Task` | Fire-and-forget call |
| `invokeWithTimeout` | `GrainRef -> TimeSpan -> ('T -> Task<'R>) -> Task<'R>` | Call with timeout |
| `unwrap` | `GrainRef -> 'T` | Get the underlying grain proxy |
| `key` | `GrainRef -> 'TKey` | Get the primary key |

#### `Filter`

| Function | Signature | Description |
|---|---|---|
| `incoming` | `(IIncomingGrainCallContext -> Task<unit>) -> IIncomingGrainCallFilter` | Create incoming filter |
| `outgoing` | `(IOutgoingGrainCallContext -> Task<unit>) -> IOutgoingGrainCallFilter` | Create outgoing filter |
| `incomingWithAround` | `before -> after -> IIncomingGrainCallFilter` | Before/after incoming filter |
| `outgoingWithAround` | `before -> after -> IOutgoingGrainCallFilter` | Before/after outgoing filter |

#### `FilterContext`

| Function | Signature | Description |
|---|---|---|
| `methodName` | `IIncomingGrainCallContext -> string` | Get called method name |
| `interfaceType` | `IIncomingGrainCallContext -> Type` | Get grain interface type |
| `grainInstance` | `IIncomingGrainCallContext -> obj option` | Get grain instance |

#### `RequestCtx`

| Function | Signature | Description |
|---|---|---|
| `set` | `string -> obj -> unit` | Set a request context value |
| `get<'T>` | `string -> 'T option` | Get a typed context value |
| `getOrDefault<'T>` | `string -> 'T -> 'T` | Get with fallback |
| `remove` | `string -> unit` | Remove a context value |
| `withValue<'T>` | `string -> obj -> (unit -> Task<'T>) -> Task<'T>` | Scoped context value |

#### `Log`

| Function | Signature | Description |
|---|---|---|
| `logInfo` | `ILogger -> string -> obj[] -> unit` | Log informational message |
| `logWarning` | `ILogger -> string -> obj[] -> unit` | Log warning message |
| `logError` | `ILogger -> exn -> string -> obj[] -> unit` | Log error with exception |
| `logDebug` | `ILogger -> string -> obj[] -> unit` | Log debug message |
| `withCorrelation` | `string -> (unit -> Task<'T>) -> Task<'T>` | Scoped correlation ID |
| `currentCorrelationId` | `unit -> string option` | Get current correlation ID |

#### `GrainState`

| Function | Signature | Description |
|---|---|---|
| `read<'T>` | `IPersistentState<'T> -> Task<'T>` | Read from storage |
| `write<'T>` | `IPersistentState<'T> -> 'T -> Task<unit>` | Write to storage |
| `clear<'T>` | `IPersistentState<'T> -> Task<unit>` | Clear storage |
| `current<'T>` | `IPersistentState<'T> -> 'T` | Get in-memory value |

#### `Reminder`

| Function | Signature | Description |
|---|---|---|
| `register` | `Grain -> string -> TimeSpan -> TimeSpan -> Task<IGrainReminder>` | Register/update reminder |
| `unregister` | `Grain -> string -> Task<unit>` | Unregister reminder |
| `get` | `Grain -> string -> Task<IGrainReminder option>` | Get reminder by name |

#### `Timers`

| Function | Signature | Description |
|---|---|---|
| `register` | `Grain -> (CT -> Task<unit>) -> TimeSpan -> TimeSpan -> IGrainTimer` | Register timer |
| `registerWithState<'T>` | `Grain -> ('T -> CT -> Task<unit>) -> 'T -> TimeSpan -> TimeSpan -> IGrainTimer` | Timer with state |

#### `Observer`

| Function | Signature | Description |
|---|---|---|
| `createRef<'T>` | `IGrainFactory -> 'T -> 'T` | Create observer reference |
| `deleteRef<'T>` | `IGrainFactory -> 'T -> unit` | Delete observer reference |
| `subscribe<'T>` | `IGrainFactory -> 'T -> IDisposable` | Subscribe with auto-cleanup |

#### `Telemetry`

| Constant/Function | Value/Signature | Description |
|---|---|---|
| `runtimeActivitySourceName` | `"Microsoft.Orleans.Runtime"` | Runtime tracing source |
| `applicationActivitySourceName` | `"Microsoft.Orleans.Application"` | App tracing source |
| `meterName` | `"Microsoft.Orleans"` | Metrics meter name |
| `activitySourceNames` | `string list` | Both source names |
| `enableActivityPropagation` | `ISiloBuilder -> ISiloBuilder` | Enable tracing propagation |

#### `Shutdown`

| Function | Signature | Description |
|---|---|---|
| `configureGracefulShutdown` | `TimeSpan -> IHostBuilder -> IHostBuilder` | Set drain timeout |
| `stopHost` | `IHost -> Task<unit>` | Stop host gracefully |
| `onShutdown` | `(CT -> Task<unit>) -> IHostBuilder -> IHostBuilder` | Register shutdown handler |

#### `StateMigration`

| Function | Signature | Description |
|---|---|---|
| `migration<'TOld, 'TNew>` | `int -> int -> ('TOld -> 'TNew) -> Migration<obj, obj>` | Define a migration |
| `applyMigrations<'T>` | `Migration list -> int -> obj -> 'T` | Apply migration chain |
| `validate` | `Migration list -> string list` | Validate migration chain |

#### `Serialization`

| Function | Signature | Description |
|---|---|---|
| `fsharpJsonOptions` | `JsonSerializerOptions` | Pre-configured F# JSON options |
| `addFSharpConverters` | `JsonSerializerOptions -> JsonSerializerOptions` | Add F# converters |
| `withConverters` | `JsonConverter list -> JsonSerializerOptions` | Create options with extras |

#### `TaskHelpers`

| Function | Signature | Description |
|---|---|---|
| `taskResult` | `'T -> Task<Result<'T, 'E>>` | Wrap as Ok |
| `taskError` | `'E -> Task<Result<'T, 'E>>` | Wrap as Error |
| `taskMap` | `('T -> 'U) -> Task<Result<'T, 'E>> -> Task<Result<'U, 'E>>` | Map Ok value |
| `taskBind` | `('T -> Task<Result<'U, 'E>>) -> Task<Result<'T, 'E>> -> Task<Result<'U, 'E>>` | Bind Ok value |

#### `FSharpGrain` — Universal Grain Pattern

Zero C# stubs. Register once with `AddFSharpGrain`, call from anywhere with `FSharpGrain.ref`.

| Type | Description |
|---|---|
| `FSharpGrainHandle<'S,'M>` | Zero-alloc struct handle for a string-keyed grain |
| `FSharpGrainGuidHandle<'S,'M>` | Zero-alloc struct handle for a GUID-keyed grain |
| `FSharpGrainIntHandle<'S,'M>` | Zero-alloc struct handle for an int64-keyed grain |

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

#### Other modules

| Module | Key Function | Description |
|---|---|---|
| `GrainExtension.getExtension<'T>` | `IAddressable -> 'T` | Get grain extension reference |
| `GrainServices.addGrainService<'T>` | `ISiloBuilder -> ISiloBuilder` | Register grain service |
| `FSharpSerialization.addFSharpSerialization` | `ISiloBuilder -> ISiloBuilder` | Orleans native F# serializer |
| `FSharpBinaryCodecRegistration.addToSerializerBuilder` | `ISerializerBuilder -> ISerializerBuilder` | Register FSharpBinaryCodec manually |
| `immutable` | `'T -> Immutable<'T>` | Wrap as immutable |
| `unwrapImmutable` | `Immutable<'T> -> 'T` | Unwrap immutable |

---

## Orleans.FSharp.Streaming

| Type | Description |
|---|---|
| `StreamRef<'T>` | Typed reference to an Orleans stream |
| `StreamSubscription<'T>` | Active stream subscription handle |

#### `Stream`

| Function | Signature | Description |
|---|---|---|
| `getStream<'T>` | `IStreamProvider -> string -> string -> StreamRef<'T>` | Get stream reference |
| `publish<'T>` | `StreamRef<'T> -> 'T -> Task<unit>` | Publish event |
| `subscribe<'T>` | `StreamRef<'T> -> ('T -> Task<unit>) -> Task<StreamSubscription<'T>>` | Subscribe with callback |
| `asTaskSeq<'T>` | `StreamRef<'T> -> TaskSeq<'T>` | Pull-based consumption |
| `subscribeFrom<'T>` | `StreamRef<'T> -> StreamSequenceToken -> ('T -> Task<unit>) -> Task<StreamSubscription<'T>>` | Subscribe from token |
| `unsubscribe<'T>` | `StreamSubscription<'T> -> Task<unit>` | Cancel subscription |
| `getSubscriptions<'T>` | `StreamRef<'T> -> Task<StreamSubscription<'T> list>` | List subscriptions |
| `resumeAll<'T>` | `StreamRef<'T> -> ('T -> Task<unit>) -> Task<unit>` | Resume all subscriptions |
| `getSequenceToken<'T>` | `StreamSubscription<'T> -> StreamSequenceToken option` | Get current token |

---

## Orleans.FSharp.BroadcastChannel

| Type | Description |
|---|---|
| `BroadcastChannelRef<'T>` | Typed reference to a broadcast channel |

#### `BroadcastChannel`

| Function | Signature | Description |
|---|---|---|
| `getChannel<'T>` | `IBroadcastChannelProvider -> string -> string -> BroadcastChannelRef<'T>` | Get channel reference |
| `publish<'T>` | `BroadcastChannelRef<'T> -> 'T -> Task<unit>` | Publish to all subscribers |

---

## Orleans.FSharp.StreamProviders

#### `StreamProviders`

| Function | Signature | Description |
|---|---|---|
| `addEventHubStreams` | `string -> string -> string -> ISiloBuilder -> ISiloBuilder` | Event Hubs provider |
| `addAzureQueueStreams` | `string -> string -> ISiloBuilder -> ISiloBuilder` | Azure Queue provider |

---

## Orleans.FSharp.Versioning

| Type | Description |
|---|---|
| `CompatibilityStrategy` | BackwardCompatible, StrictVersion, AllVersions |
| `VersionSelectorStrategy` | AllCompatibleVersions, LatestVersion, MinimumVersion |

#### `Versioning`

| Function | Signature | Description |
|---|---|---|
| `compatibilityStrategyName` | `CompatibilityStrategy -> string` | Convert to Orleans name |
| `versionSelectorStrategyName` | `VersionSelectorStrategy -> string` | Convert to Orleans name |

---

## Orleans.FSharp.GrainDirectory

| Type | Description |
|---|---|
| `GrainDirectoryProvider` | Default, Redis, AzureStorage, Custom |

#### `GrainDirectory`

| Function | Signature | Description |
|---|---|---|
| `configure` | `GrainDirectoryProvider -> ISiloBuilder -> ISiloBuilder` | Configure grain directory |

---

## Orleans.FSharp.Kubernetes

#### `Kubernetes`

| Function | Signature | Description |
|---|---|---|
| `useKubernetesClustering` | `ISiloBuilder -> ISiloBuilder` | Enable K8s clustering |
| `useKubernetesClusteringWithNamespace` | `string -> ISiloBuilder -> ISiloBuilder` | K8s with custom namespace |

---

## Orleans.FSharp.Transactions

| Type | Description |
|---|---|
| `TransactionOption` | Create, Join, CreateOrJoin, Supported, NotAllowed, Suppress |

#### `TransactionalState`

| Function | Signature | Description |
|---|---|---|
| `read<'T>` | `ITransactionalState<'T> -> Task<'T>` | Read transactional state |
| `update<'T>` | `('T -> 'T) -> ITransactionalState<'T> -> Task<unit>` | Update transactional state |
| `performRead<'T, 'R>` | `('T -> 'R) -> ITransactionalState<'T> -> Task<'R>` | Read with projection |

---

## Orleans.FSharp.Runtime

### Types

| Type | Description |
|---|---|
| `SiloConfig` | Immutable silo configuration record |
| `ClientConfig` | Immutable client configuration record |
| `ClusteringMode` | Localhost, RedisClustering, AzureTableClustering, AdoNetClustering, CustomClustering |
| `ClientClusteringMode` | Localhost, StaticGateway, Custom |
| `StorageProvider` | Memory, RedisStorage, AzureBlobStorage, AzureTableStorage, AdoNetStorage, CosmosStorage, DynamoDbStorage, CustomStorage |
| `StreamProvider` | MemoryStream, PersistentStream, CustomStream |
| `ReminderProvider` | MemoryReminder, RedisReminder, CustomReminder |
| `TlsConfig` | TlsSubject, TlsCertificate, MutualTlsSubject, MutualTlsCertificate |
| `DashboardConfig` | DashboardDefaults, DashboardWithOptions |

### Computation Expressions

| CE | Builder | Description |
|---|---|---|
| `siloConfig { }` | `SiloConfigBuilder` | Configure an Orleans silo |
| `clientConfig { }` | `ClientConfigBuilder` | Configure an Orleans client |

#### `SiloConfig`

| Function | Signature | Description |
|---|---|---|
| `Default` | `SiloConfig` | Empty default configuration |
| `validate` | `SiloConfig -> string list` | Validate configuration |
| `applyToSiloBuilder` | `SiloConfig -> ISiloBuilder -> unit` | Apply to silo builder |
| `applyToHost` | `SiloConfig -> HostApplicationBuilder -> unit` | Apply to host |

#### `ClientConfig`

| Function | Signature | Description |
|---|---|---|
| `Default` | `ClientConfig` | Empty default configuration |
| `validate` | `ClientConfig -> string list` | Validate configuration |
| `applyToBuilder` | `ClientConfig -> IClientBuilder -> unit` | Apply to client builder |
| `applyToHost` | `ClientConfig -> HostApplicationBuilder -> unit` | Apply to host |
| `build` | `ClientConfig -> IHost * IClusterClient` | Build and return client |

---

## Orleans.FSharp.EventSourcing

### Types

| Type | Description |
|---|---|
| `EventSourcedGrainDefinition<'State, 'Event, 'Command>` | Event-sourced grain specification |
| `IEventStoreContext<'Event>` | Event store abstraction for C# CodeGen bridge |

### Computation Expressions

| CE | Builder | Description |
|---|---|---|
| `eventSourcedGrain { }` | `EventSourcedGrainBuilder` | Define event-sourced grain behavior |

#### `EventSourcedGrainDefinition`

| Function | Signature | Description |
|---|---|---|
| `foldEvents` | `definition -> 'State -> 'Event list -> 'State` | Replay events onto state |
| `handleCommand` | `definition -> 'State -> 'Command -> 'State * 'Event list` | Process command |

#### `EventStore`

| Function | Signature | Description |
|---|---|---|
| `processCommand` | `definition -> 'State -> 'Command -> 'Event list` | Produce events from command |
| `applyEvent` | `definition -> 'State -> 'Event -> 'State` | Apply single event |
| `replayEvents` | `definition -> 'State -> 'Event list -> 'State` | Replay event list |

---

## Orleans.FSharp.Testing

### Types

| Type | Description |
|---|---|
| `TestHarness` | Test cluster wrapper with log capture |
| `MockGrainFactory` | Mock IGrainFactory for unit tests |
| `CapturingLogger` | In-memory ILogger |
| `CapturingLoggerFactory` | Factory for CapturingLogger instances |
| `CapturedLogEntry` | Captured structured log entry |

#### `TestHarness`

| Function | Signature | Description |
|---|---|---|
| `createTestCluster` | `unit -> Task<TestHarness>` | Create default test cluster |
| `createTestClusterWith` | `SiloConfig -> Task<TestHarness>` | Create with custom config |
| `getGrainByString<'T>` | `TestHarness -> string -> GrainRef<'T, string>` | Get grain by string key |
| `getGrainByInt64<'T>` | `TestHarness -> int64 -> GrainRef<'T, int64>` | Get grain by int64 key |
| `getGrainByGuid<'T>` | `TestHarness -> Guid -> GrainRef<'T, Guid>` | Get grain by GUID key |
| `captureLogs` | `TestHarness -> CapturedLogEntry list` | Get all captured logs |
| `reset` | `TestHarness -> Task<unit>` | Clear captured logs |
| `dispose` | `TestHarness -> Task<unit>` | Stop and dispose cluster |

#### `GrainMock`

| Function | Signature | Description |
|---|---|---|
| `create` | `unit -> MockGrainFactory` | Create empty mock factory |
| `withGrain<'T>` | `obj -> 'T -> MockGrainFactory -> MockGrainFactory` | Register mock grain |

#### `GrainArbitrary`

| Function | Signature | Description |
|---|---|---|
| `forState<'T>` | `unit -> Arbitrary<'T>` | Auto-generate Arbitrary for state type |
| `forCommands<'T>` | `unit -> Arbitrary<'T list>` | Auto-generate Arbitrary for command sequences |

#### `FsCheckHelpers`

| Function | Signature | Description |
|---|---|---|
| `commandSequenceArb<'T>` | `unit -> Arbitrary<'T list>` | Non-empty command list Arbitrary |
| `stateMachineProperty` | `'State -> ('State -> 'Cmd -> 'State) -> ('State -> bool) -> 'Cmd list -> bool` | State machine invariant check |

#### `LogCapture`

| Function | Signature | Description |
|---|---|---|
| `create` | `unit -> CapturingLoggerFactory` | Create capturing factory |
| `captureLogs` | `CapturingLoggerFactory -> CapturedLogEntry list` | Get all entries |
