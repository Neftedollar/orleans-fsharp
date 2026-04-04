# Changelog

## [Unreleased]

### `handleWithContext` — grain-to-grain calls via `IUniversalGrainHandler`

`IUniversalGrainHandler.Handle` now accepts `IServiceProvider` and `IGrainFactory` parameters,
enabling grains defined with `handleWithContext` (or `handleWithServices`) to make grain-to-grain
calls and resolve DI services when dispatched through the universal `AddFSharpGrain` pattern:

```fsharp
// Relay grain: on ForwardPing, calls a peer PingGrain via ctx.GrainFactory
let relay =
    grain {
        defaultState { PingsSent = 0; LastPeerCount = 0 }
        handleWithContext (fun ctx state cmd ->
            task {
                match cmd with
                | ForwardPing peerKey ->
                    let peer = FSharpGrain.ref<PingState, PingCommand> ctx.GrainFactory peerKey
                    let! peerState = FSharpGrain.send Ping peer
                    return { PingsSent = state.PingsSent + 1; LastPeerCount = peerState.Count }, box ()
                | ...
            })
    }

// Register with AddFSharpGrain and call as usual — context is threaded automatically
siloBuilder.Services.AddFSharpGrain<RelayState, RelayCommand>(relay) |> ignore
```

**Breaking change:** `IUniversalGrainHandler.Handle` signature changed from 2 to 4 parameters.
Callers of `Handle` must pass `serviceProvider` and `grainFactory` (use `null` in tests that do not exercise context).

### Sample: `LeaderboardGrain`

New sample grain in `Orleans.FSharp.Sample` demonstrating the `handleWithContext` pattern
for grain-to-grain fan-out: a leaderboard grain queries multiple player-score grains in
parallel via `Task.WhenAll`, sorts by score, and caches the snapshot.

### Test coverage

- 7 integration tests for `handleWithContext` (relay grain, grain-to-grain forwarding, isolation)
- 7 integration tests for `handleStateCancellable` (cancellable accumulator grain)
- 10 integration tests for `handleWithContextCancellable` (CtxCancAcc grain — mixed pure accumulation + grain-to-grain via ctx.GrainFactory)
- 9 integration tests for `handleState` (score accumulator grain)
- `HandlerCompositionProperties.fs` — 14 FsCheck property tests for handler invariants
- Expanded `ErrorMessageTests.fs` — error paths for context-only, CancellableContextHandler-only,
  and empty definitions; strengthened assertions to use `&&` instead of `||`
- `AnalyzerTests.fs` — `use!` binding, while/for loop tests; 23 total analyzer tests

### Bug fixes

- `AsyncUsageAnalyzer` — remove phantom `LetOrUseBang` case (merged into `LetOrUse(isBang=true)` in FCS 43.10+)
- `GrainBuilderTests` — FsCheck persist-name property: `IsNullOrEmpty` → `IsNullOrWhiteSpace` for tab characters
- `GrainMockTests` — fix spurious test that discarded `s1.Total` rather than asserting it

### New Package: `Orleans.FSharp.Analyzers`

Compile-time F# analyzer for Orleans grain code:

- **OF0001** — warns when `async { }` is used instead of `task { }` in Orleans grain handlers and Task-returning methods
- `[<AllowAsync>]` attribute suppresses OF0001 on a specific binding when `async { }` is genuinely required
- `AstWalker.collectAsyncRanges` walks the full untyped F# AST (LetOrUse, Match, Lambda, If/Then/Else, TryWith, TryFinally, nested modules, class methods, record fields)
- 20 unit tests covering detection, suppression, structural nesting, and attribute mechanics
- Add to your project: `dotnet add package Orleans.FSharp.Analyzers`

### New Packages
- **`Orleans.FSharp.Abstractions`** — New C# shim package hosting `IFSharpGrain`, `IFSharpGrainWithGuidKey`, and `IFSharpGrainWithIntKey` interfaces. Orleans source generators run on this project and produce public `Proxy_IFSharpGrain*` classes in the same assembly. Reference this from your silo instead of `Orleans.FSharp.CodeGen`.

### Universal Grain Pattern — code-gen-free grain calls

A brand-new way to define and call F# grains with zero per-grain C# stubs:

```fsharp
// Register at silo startup
siloBuilder.Services.AddFSharpGrain<PingState, PingCommand>(pingGrain)

// Call from any F# code — no ICounterGrain interface needed
let handle = FSharpGrain.ref<PingState, PingCommand> factory "grain-id"
let! state = handle |> FSharpGrain.send Ping

// GUID and integer keys also supported
let handle = FSharpGrain.refGuid<MyState, MyCmd> factory (Guid.NewGuid())
let handle = FSharpGrain.refInt<MyState, MyCmd> factory 42L
```

**Implementation:**
- `FSharpGrainImpl` — concrete `Grain` class for string-keyed grains (in Abstractions, auto-discovered by Orleans)
- `FSharpGrainGuidImpl` — concrete `Grain` class for GUID-keyed grains
- `FSharpGrainIntImpl` — concrete `Grain` class for integer-keyed grains
- `UniversalGrainHandlerRegistry` — routes messages to registered F# handlers by DU type name
- `IUniversalGrainHandler` / `GrainDispatchResult` — C#-to-F# dispatch interface
- **Correct F# DU dispatch:** nullary DU cases in mixed DUs compile to private `_CaseName` nested types; the registry uses `BindingFlags.Public | BindingFlags.NonPublic` when scanning nested types so all case variants are routed correctly

### New: Observer integration tests

Full end-to-end integration test suite for `FSharpObserverManager<'T>` running in a real `TestCluster`:
- `Observer.createRef` / `Observer.deleteRef` lifecycle
- `Observer.subscribe` IDisposable pattern
- Single and multiple observers, fan-out to N subscribers
- `Unsubscribe` stops notification delivery
- Empty broadcast completes without error

### New: `ask` / `askGuid` / `askInt` — typed result access

New variants in `FSharpGrain` module that return a separately-specified result type `'R`
instead of the grain state. Use these with `handleTyped` grains or any handler that
returns a value different from the state:

```fsharp
// Handler defined with handleTyped — result is int, not CalcState
let handle = FSharpGrain.ref<CalcState, CalcCommand> factory "calc-1"
let! result: int = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(3, 4))
// result = 7

// Also available for GUID and integer key grains
let! label: string = guidHandle |> FSharpGrain.askGuid<S, C, string> GetLabel
let! count: int64  = intHandle  |> FSharpGrain.askInt<S, C, int64> GetCount
```

### New: `handleTyped` end-to-end integration tests

Added `CalcGrain` (registered in `ClusterFixture`) that uses `handleTyped` to define a
calculator without any manual `box` calls. 8 integration tests cover `AddValues`,
`MultiplyValues`, `OpCount`, and the `post+ask` pattern.

### Improvements
- `GrainDefinition.invokeReminderHandler` — new C#-callable function for delegating to F# reminder handlers by name; used internally by backward-compat grain stubs
- **`AddFSharpGrain` auto-registers `FSharpBinaryCodec`** — no manual `FSharpBinaryCodecRegistration.addToSerializerBuilder` call needed on the silo side when using the universal pattern. Registration is idempotent across multiple `AddFSharpGrain<_,_>` calls.
- **XML remarks on `FSharpGrain` module** clarify when to use `send` vs `ask` vs `post`
- 30 new integration tests (universal pattern string/GUID/int keys + observers)
- 54 new unit tests: GrainDispatchResult, impl class metadata, registry dispatch, FsCheck properties, `AddFSharpGrain` DI wiring (14 new)
- 6 FsCheck property tests for `handleState`/`handleTyped` CE variants in `GrainBuilderTests`
- 8 `ask`/`askGuid`/`askInt` integration tests with `QueryGrain`
- 8 `handleTyped` integration tests with `CalcGrain`
- Total: **1149 unit + 192 integration = 1341 tests**

### Documentation
- Rewrote `docs/getting-started.md` to lead with the universal grain pattern (no attributes, no C# stubs)
- Added `ask` to getting-started quick-reference table
- Added auto-registration callout to `docs/serialization.md`
- Added `ask`/`askGuid`/`askInt` entries to `docs/api-reference.md` FSharpGrain table
- Expanded `docs/testing.md` with direct handler testing and universal pattern test examples
- Added `handleState`/`handleTyped` documentation to `docs/grain-definition.md`

### Breaking changes
- `IFSharpGrain` no longer inherits `IRemindable`. `IRemindable` is implemented directly by `FSharpGrain<'S,'M>` in `Orleans.FSharp.Runtime`. This avoids pulling the `Microsoft.Orleans.Reminders` source generator into the Abstractions project.

### Migration
From `Orleans.FSharp.CodeGen` (per-grain stubs) to universal `IFSharpGrain` pattern:
1. Add `Orleans.FSharp.Abstractions` to your silo project
2. Register grains: `services.AddFSharpGrain<State, Command>(myGrainDef)`
3. Call grains: `FSharpGrain.ref<State, Command> factory "key" |> FSharpGrain.send MyCommand`
4. `Orleans.FSharp.CodeGen` is still available for backward compatibility (per-grain C# stubs)

---

## [1.0.0] - 2026-04-03

### First stable release — full Orleans 10.0.1 parity from F#

804 tests (718 unit + 86 integration), zero warnings, zero `Unchecked.defaultof` in source.

### Core (`Orleans.FSharp`)

#### Grain Definition — `grain { }` CE (31 keywords)
- `defaultState`, `handle`, `handleWithContext`, `handleWithServices` — basic grain definition
- `handleCancellable`, `handleWithContextCancellable`, `handleWithServicesCancellable` — CancellationToken support
- `persist`, `additionalState` — single and multiple named persistent states
- `onActivate`, `onDeactivate`, `onLifecycleStage` — lifecycle hooks
- `onReminder`, `onTimer` — declarative reminders and timers
- `reentrant`, `interleave`, `readOnly`, `mayInterleave` — concurrency control
- `statelessWorker`, `maxActivations` — stateless worker grains
- `implicitStreamSubscription` — automatic stream subscriptions
- `oneWay`, `grainType`, `deactivationTimeout` — method and type annotations
- 7 placement strategies: `preferLocalPlacement`, `randomPlacement`, `hashBasedPlacement`, `activationCountPlacement`, `resourceOptimizedPlacement`, `siloRolePlacement`, `customPlacement`

#### Modules
- **GrainRef** — type-safe grain references: `ofString`, `ofGuid`, `ofInt64`, `ofGuidCompound`, `ofIntCompound`, `invoke`, `invokeOneWay`, `invokeWithTimeout`
- **GrainState** — immutable state wrapper: `read`, `write`, `clear`, `current`
- **GrainContext** — DI access from handlers: `getService<'T>`, `getState<'T>`, `grainId`, `primaryKeyString/Guid/Int64`, `deactivateOnIdle`, `delayDeactivation`
- **Stream** — Orleans streaming with TaskSeq: `getStream`, `publish`, `subscribe`, `asTaskSeq`, `unsubscribe`, `subscribeFrom`, `getSubscriptions`, `resumeAll`
- **BroadcastChannel** — fan-out pub/sub: `getChannel`, `publish`
- **StreamProviders** — `addEventHubStreams`, `addAzureQueueStreams`
- **Reminder** — persistent reminders: `register`, `unregister`, `get`
- **Timers** — in-memory timers: `register`, `registerWithState`
- **Observer** — grain observers: `createRef`, `deleteRef`, `subscribe` + `FSharpObserverManager<'T>`
- **Filter** — call interceptors: `incoming`, `outgoing`, `incomingWithAround`, `outgoingWithAround`
- **FilterContext** — introspect grain calls: `methodName`, `interfaceType`, `grainInstance`
- **RequestCtx** — propagate context across calls: `set`, `get`, `getOrDefault`, `remove`, `withValue`
- **Log** — structured logging with correlation: `logInfo`, `logWarning`, `logError`, `logDebug`, `withCorrelation`, `currentCorrelationId`
- **Transactions** — `TransactionalState.read`, `update`, `performRead` + `TransactionOption` DU
- **Versioning** — `CompatibilityStrategy`, `VersionSelectorStrategy`
- **Telemetry** — OpenTelemetry: `runtimeActivitySourceName`, `meterName`, `enableActivityPropagation`
- **GrainDirectory** — `Default`, `Redis`, `AzureStorage`, `Custom`
- **GrainServices** — `addGrainService<'T>`
- **GrainExtension** — `getExtension<'T>`
- **Kubernetes** — `useKubernetesClustering`, `useKubernetesClusteringWithNamespace`
- **Shutdown** — `configureGracefulShutdown`, `stopHost`, `onShutdown`
- **StateMigration** — typed migrations: `migration`, `applyMigrations`, `validate`
- **Serialization** — `fsharpJsonOptions`, `addFSharpConverters`, `withConverters`
- **FSharpSerialization** — `addFSharpSerialization` (native Orleans serializer)
- **Scripting** — `quickStart`, `getGrain`, `shutdown` for .fsx REPL
- **Immutable<'T>** — `immutable`, `unwrapImmutable` for zero-copy

### Silo Configuration (`Orleans.FSharp.Runtime`)

#### `siloConfig { }` CE (39 keywords)
- **Clustering**: `useLocalhostClustering`, `addRedisClustering`, `addAzureTableClustering`, `addAdoNetClustering`
- **Storage**: `addMemoryStorage`, `addRedisStorage`, `addAzureBlobStorage`, `addAzureTableStorage`, `addAdoNetStorage`, `addCosmosStorage`, `addDynamoDbStorage`, `addCustomStorage`
- **Streaming**: `addMemoryStreams`, `addPersistentStreams`, `addBroadcastChannel`
- **Reminders**: `addMemoryReminderService`, `addRedisReminderService`, `addCustomReminderService`
- **Security**: `useTls`, `useTlsWithCertificate`, `useMutualTls`, `useMutualTlsWithCertificate`
- **Infrastructure**: `addDashboard`, `addDashboardWithOptions`, `enableHealthChecks`, `addStartupTask`, `addGrainService`
- **Filters**: `addIncomingFilter`, `addOutgoingFilter`
- **Identity**: `clusterId`, `serviceId`, `siloName`, `siloPort`, `gatewayPort`, `advertisedIpAddress`
- **Tuning**: `grainCollectionAge`, `useGrainVersioning`, `useSerilog`, `configureServices`

#### `clientConfig { }` CE (11 keywords)
- `useLocalhostClustering`, `useStaticClustering`, `clusterId`, `serviceId`
- `useTls`, `useTlsWithCertificate`, `useMutualTls`
- `addMemoryStreams`, `configureServices`
- `gatewayListRefreshPeriod`, `preferredGatewayIndex`

### Event Sourcing (`Orleans.FSharp.EventSourcing`)
- `eventSourcedGrain { }` CE: `defaultState`, `apply`, `handle`, `logConsistencyProvider`
- `EventStore` module: wraps JournaledGrain methods
- `MartenConfig`: placeholder for PostgreSQL event store integration

### Testing (`Orleans.FSharp.Testing`)
- **TestHarness** — in-process test cluster: `createTestCluster`, `getGrain`, `captureLogs`, `reset`, `dispose`
- **GrainMock** — mock factory for unit tests: `create`, `withGrain`, `createMockContext`
- **GrainArbitrary** — TypeShape-based auto FsCheck Arbitrary: `forState<'T>`, `forCommands<'T>`
- **FsCheckHelpers** — `stateMachineProperty`, `commandSequenceArb`
- **LogCapture** — `CapturedLogEntry`, `CapturingLoggerFactory`

### Analyzers (`Orleans.FSharp.Analyzers`)
- **OF0001**: Warns on `async { }` usage — suggests `task { }` for Orleans compatibility
- Supports `[<AllowAsync>]` opt-out attribute

### CodeGen (`Orleans.FSharp.CodeGen`)
- C# bridge project for Orleans Roslyn source generators
- Required because Orleans source generators only work on C# projects

### Infrastructure
- `.NET 10` / `F# 9+` / `Orleans 10.0.1`
- `FsToolkit.ErrorHandling 5.2.0` — `taskResult { }` CE available
- `TypeShape` — auto FsCheck Arbitrary generation
- `FSharp.SystemTextJson` — DU/Record serialization
- `FSharp.Control.TaskSeq` — streaming with `taskSeq { }`
- `IcedTasks` — ColdTask, CancellableTask CE extensions
- `Serilog` — structured logging
- GitHub Actions CI with Gitleaks security scanning
- NuGet trusted publisher (OIDC)
- Source Link + symbol packages (snupkg)
- `dotnet new orleans-fsharp` project template
- Allocation benchmarks: GrainRef struct confirmed zero-alloc
- Input validation on all CE string parameters (35 tests)
- TLS/mTLS security warnings in XML docs

### Documentation
- 10 comprehensive guides (3,800+ lines)
- Per-package NuGet README files
- CONTRIBUTING.md, CODE_OF_CONDUCT.md, SECURITY.md
- 3 sample patterns: CQRS, Saga, Rate Limiter
- Complete API reference

[1.0.0]: https://github.com/Neftedollar/orleans-fsharp/releases/tag/v1.0.0
