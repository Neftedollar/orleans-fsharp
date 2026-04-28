# Changelog

## [Unreleased]

## [2.0.0-alpha.1] - 2026-04-28

First 2.0.0 preview. API may still shift before the stable 2.0.0 release. Install with `--prerelease` from NuGet. Headline themes:

- **Universal Grain Pattern** — call any registered F# grain without per-grain C# stubs. New `Orleans.FSharp.Abstractions` package hosts `IFSharpGrain` / `IFSharpGrainWithGuidKey` / `IFSharpGrainWithIntKey`. Register once with `services.AddFSharpGrain<State, Command>(grainDef)` and dispatch via `FSharpGrain.ref` / `send` / `ask` / `post`. Works with string, GUID, and integer keys.
- **Handler matrix completed** — 12 `handle*` CE variants covering every combination of state-only / typed result / context / cancellation: `handleState`, `handleTyped`, `handleStateWithContext`, `handleTypedWithContext`, `handleStateCancellable`, `handleTypedCancellable`, `handleStateWithContextCancellable`, `handleTypedWithContextCancellable`, plus the existing four. `getCancellableContextHandler` is the universal dispatch fallback.
- **Behavior pattern adapters** — `Behavior.run` and `Behavior.runWithContext` plug behavior handlers directly into `handleState` / `handleStateWithContext` without manual `BehaviorResult` unwrapping. `runWithContext` calls `ctx.DeactivateOnIdle()` on `Stop`.
- **`ask` / `askGuid` / `askInt`** — typed result access for handlers that return a value distinct from the state.
- **`Orleans.FSharp.Analyzers`** — new package shipping `OF0001` (warns on `async {}` in grain code) with `[<AllowAsync>]` opt-out.
- **Safer state migrations** — `StateMigration.tryApplyMigrations` returns `Result<'T, string list>` instead of throwing. `GrainContext.empty` for unit tests.
- **Auto-registered F# binary serializer** — `AddFSharpGrain` wires `FSharpBinaryCodec` automatically; no manual `addToSerializerBuilder` call needed for the universal pattern.
- **Test growth** — ~1500 tests (unit + integration), including 27 new FsCheck property suites across `StateMigration`, `SchemaEvolution`, `GrainRef`, `InputValidation`, and the full handler matrix.
- **MinVer-driven release** — version is now derived from the `v*` git tag; CI publishes on tag push.

### Deprecations

The following 7 `grain { }` CE keywords are now compile-time warnings (not errors) and are non-functional in the universal grain pattern. They remain in the API for source compatibility but produce no runtime effect, since all F# grains share `FSharpGrainImpl` and cannot carry per-grain class or per-method attributes:

- `reentrant`
- `statelessWorker`
- `maxActivations`
- `mayInterleave`
- `interleave`
- `oneWay`
- `readOnly`

To apply class-level (`[Reentrant]`, `[StatelessWorker]`, `[MayInterleave]`) or per-method (`[AlwaysInterleave]`, `[ReadOnly]`, `[OneWay]`) attributes, write a per-grain C# stub manually using the legacy `Orleans.FSharp.CodeGen` pattern. Existing callers will see warnings but continue to compile.

### Other breaking changes

- `IUniversalGrainHandler.Handle` signature widened from 2 to 4 parameters (`serviceProvider`, `grainFactory` added). Pass `null` in tests that do not exercise context.
- `IFSharpGrain` no longer inherits `IRemindable`. `IRemindable` is implemented directly by `FSharpGrain<'S,'M>` in `Orleans.FSharp.Runtime`. This avoids pulling the `Microsoft.Orleans.Reminders` source generator into the Abstractions project.

### Migration from 1.0.0

1. Add `Orleans.FSharp.Abstractions` to your silo project.
2. Register grains: `services.AddFSharpGrain<State, Command>(myGrainDef)`.
3. Call grains: `FSharpGrain.ref<State, Command> factory "key" |> FSharpGrain.send MyCommand`.
4. Replace any uses of the 7 deprecated CE keywords if you need their effect — write a per-grain C# stub via `Orleans.FSharp.CodeGen`.
5. Update `IUniversalGrainHandler.Handle` callers to pass the new `serviceProvider` and `grainFactory` parameters.

### Detailed change list

### `StateMigration.tryApplyMigrations` — safe Result-based migration

A new function that validates the migration chain before applying it, returning
`Result<'T, string list>` instead of throwing on an invalid chain:

```fsharp
match StateMigration.tryApplyMigrations<StateV3> migrations 1 (box oldState) with
| Ok newState -> // use newState
| Error errs  -> for e in errs do log.LogError("Migration error: {Error}", e)
```

Compared to `applyMigrations` (which throws on gaps/duplicates), `tryApplyMigrations`
is the preferred choice for production grain activation paths where you want to surface
migration errors through structured logging rather than runtime exceptions.

Also fixes a dead-code `List.iter` call in `StateMigration.validate` that was a no-op.

### `Behavior.run` and `Behavior.runWithContext` adapters

Two new adapter functions in the `Behavior` module eliminate the need to manually unwrap
`BehaviorResult` inside handler lambdas:

```fsharp
// Before — manual unwrap inside handleState
handleState (fun state cmd -> task {
    let! result = myBehaviorHandler state cmd
    return Behavior.unwrap state result
})

// After — plug the behavior handler directly
handleState (Behavior.run myBehaviorHandler)

// With context + deactivation on Stop
handleStateWithContext (Behavior.runWithContext myContextBehaviorHandler)
```

`Behavior.runWithContext` calls `ctx.DeactivateOnIdle()` automatically when the handler
returns `Stop`, so the grain is scheduled for deactivation without any extra code in the handler.

### `GrainContext.empty` convenience value

A pre-built empty `GrainContext` for use in unit tests where the handler does not interact
with the grain factory or service provider:

```fsharp
let handler = GrainDefinition.getContextHandler myGrain
let! ns, _ = handler GrainContext.empty initialState myCmd
```

### Testing guide expanded

`docs/testing.md` now covers direct handler testing for all 12 CE variants including
`getCancellableContextHandler` as the universal dispatch fallback, with a complete
score-tracker FsCheck property test example.

### FsCheck property test expansion

Generative property tests added to cover invariants across multiple modules:

- **StateMigration** (6 properties): `applyMigrations` with empty list, idempotency, `validate` for
  contiguous chains of any length, determinism, gap detection for any non-adjacent pair, identity
  migration preserves content
- **SchemaEvolution** (9 properties): JSON roundtrips for all V2 type variants, serialization
  determinism, backward-compatible case deserialization across versions
- **GrainRef** (5 properties): key roundtrip for string and int64, `invoke` dispatch for any key
  and payload, `unwrap` returns responsive grain
- **InputValidation** (7 properties): exhaustive whitespace rejection for `persist`, `clusterId`,
  `addMemoryStorage`; acceptance of any non-whitespace name

### Test coverage

- 12 new unit tests for `Behavior.run` / `Behavior.runWithContext` (including 2 FsCheck properties)
- 8 integration tests for the Behavior pattern grain (`TestGrains14`, `WorkflowGrain`)
- 27 new FsCheck property tests across StateMigration, SchemaEvolution, GrainRef, InputValidation
- Total: **~1500 tests** (unit + integration)

---

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
- 7 integration tests for `handleCancellable` (raw cancellable handler with manual box)
- 8 integration tests for `handleTypedCancellable` (typed result + CancellationToken, uses `ask`)
- 10 integration tests for `handleWithContextCancellable` (CtxCancAcc grain — mixed pure accumulation + grain-to-grain via ctx.GrainFactory)
- 9 integration tests for `handleState` (score accumulator grain)
- 8 integration tests for `handleStateWithContext` (state-only return + ctx.GrainFactory)
- 8 integration tests for `handleTypedWithContext` (typed result + ctx, uses `ask`)
- `HandlerCompositionProperties.fs` — 25 FsCheck property tests for handler invariants
  (added 11 new: hasAnyHandler for all variants, handleTypedCancellable result+token,
  handleWithContextCancellable ctx+token, handleStateWithContext ctx threading,
  handleTypedWithContext result typing)
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
- `GrainHandlerStateMachineProperties.fs` — 11 FsCheck properties for score-tracker grain
  testing actual handler pipeline (handleState, handleTyped, handleStateCancellable):
  net-score invariant, Reset to zero, N-wins, GetScore idempotency, Win+Lose symmetry,
  handleState/handleStateCancellable equivalence
### New CE operations: `handleStateWithContextCancellable` / `handleTypedWithContextCancellable`

Two new CE keywords completing the handler variant matrix — the final combination of
context + cancellation + convenience return style:

- `handleStateWithContextCancellable` — `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State>` (no manual `box`)
- `handleTypedWithContextCancellable` — `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State * 'Result>` (no manual `box`)
- Aliases: `handleStateWithServicesCancellable`, `handleTypedWithServicesCancellable`

Both store in `CancellableContextHandler` (the same slot as `handleWithContextCancellable`) and are
reachable through the full fallback chain in `getCancellableContextHandler`.

- 6 integration tests for `handleStateWithContextCancellable` (state-only + ctx.GrainFactory)
- 6 integration tests for `handleTypedWithContextCancellable` (typed result via `ask`)
- 5 FsCheck properties (ctx+token threading, equivalence, hasAnyHandler)
- Total: **1176 unit + 238 integration = 1414 tests**

### Documentation
- Rewrote `docs/getting-started.md` to lead with the universal grain pattern (no attributes, no C# stubs)
- Added `ask` to getting-started quick-reference table
- Added auto-registration callout to `docs/serialization.md`
- Added `ask`/`askGuid`/`askInt` entries to `docs/api-reference.md` FSharpGrain table
- Expanded `docs/testing.md` with direct handler testing and universal pattern test examples
- Added `handleState`/`handleTyped` documentation to `docs/grain-definition.md`

### Deprecations

Seven `grain { }` CE keywords are now marked `[<Obsolete>]` (compile warnings, not errors) because
they are non-functional under the universal F# grain pattern, where all grains share `FSharpGrainImpl`:

- Class-level attributes (cannot be applied to a shared impl class): `reentrant`, `statelessWorker`, `maxActivations`, `mayInterleave`
- Per-method attributes (the universal pattern exposes a single `HandleMessage(object)` entry point): `interleave`, `oneWay`, `readOnly`

Existing call sites continue to compile. To get the underlying Orleans behavior, write a per-grain
C# stub manually using `Orleans.FSharp.CodeGen`.

### Breaking changes
- `IFSharpGrain` no longer inherits `IRemindable`. `IRemindable` is implemented directly by `FSharpGrain<'S,'M>` in `Orleans.FSharp.Runtime`. This avoids pulling the `Microsoft.Orleans.Reminders` source generator into the Abstractions project.
- `IUniversalGrainHandler.Handle` signature changed from 2 to 4 parameters (added `serviceProvider`, `grainFactory`).

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

[Unreleased]: https://github.com/Neftedollar/orleans-fsharp/compare/v2.0.0-alpha.1...HEAD
[2.0.0-alpha.1]: https://github.com/Neftedollar/orleans-fsharp/releases/tag/v2.0.0-alpha.1
[1.0.0]: https://github.com/Neftedollar/orleans-fsharp/releases/tag/v1.0.0
