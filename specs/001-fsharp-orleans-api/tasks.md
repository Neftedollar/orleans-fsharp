# Tasks: F# Idiomatic API Layer for Orleans

**Input**: Design documents from `/specs/001-fsharp-orleans-api/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/public-api.md, quickstart.md

**Tests**: Included — constitution mandates TDD (Principle III) and property-based testing (Principle IV).

**Organization**: Tasks grouped by user story for independent implementation and testing.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story (US1–US7)
- Exact file paths included in all tasks

## Path Conventions

- Source: `src/<ProjectName>/<File>.fs`
- Tests: `tests/<ProjectName>/<File>.fs`
- C# CodeGen: `src/Orleans.FSharp.CodeGen/<File>.cs`

---

## Phase 1: Setup

**Purpose**: Solution structure, project files, dependencies

- [x] T001 Create solution file `Orleans.FSharp.sln` at repository root
- [x] T002 [P] Create F# class library project `src/Orleans.FSharp/Orleans.FSharp.fsproj` with dependencies: Microsoft.Orleans.Core.Abstractions, IcedTasks, FSharp.SystemTextJson, FSharp.Control.TaskSeq, Microsoft.Extensions.Logging.Abstractions
- [x] T003 [P] Create F# class library project `src/Orleans.FSharp.Runtime/Orleans.FSharp.Runtime.fsproj` with dependencies: Microsoft.Orleans.Server, Serilog.Extensions.Logging; project reference to Orleans.FSharp
- [x] T004 [P] Create C# class library project `src/Orleans.FSharp.CodeGen/Orleans.FSharp.CodeGen.csproj` with dependency: Microsoft.Orleans.Sdk; project reference to Orleans.FSharp
- [x] T005 [P] Create F# class library project `src/Orleans.FSharp.Testing/Orleans.FSharp.Testing.fsproj` with dependencies: Microsoft.Orleans.TestingHost, FsCheck, xunit; project references to Orleans.FSharp, Orleans.FSharp.Runtime
- [x] T006 [P] Create F# xUnit test project `tests/Orleans.FSharp.Tests/Orleans.FSharp.Tests.fsproj` with dependencies: FsCheck.Xunit, Unquote, xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk; project reference to Orleans.FSharp, Orleans.FSharp.Testing
- [x] T007 [P] Create F# xUnit test project `tests/Orleans.FSharp.Integration/Orleans.FSharp.Integration.fsproj` with dependencies: FsCheck.Xunit, Unquote, xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk; project references to Orleans.FSharp, Orleans.FSharp.Runtime, Orleans.FSharp.Testing, Orleans.FSharp.CodeGen
- [x] T008 Add all projects to `Orleans.FSharp.sln` and verify `dotnet build` succeeds with zero warnings
- [x] T009 [P] Create `.editorconfig` at repository root with F# formatting rules and `TreatWarningsAsErrors=true` in `Directory.Build.props`; set `<TargetFramework>net8.0</TargetFramework>` minimum in `Directory.Build.props` so targeting .NET < 8 fails with clear restore error
- [x] T010 [P] Create `.gitignore` with standard .NET entries (bin/, obj/, .vs/, *.user)
- [x] T011a [P] Create F# console project `src/Orleans.FSharp.Sample/Orleans.FSharp.Sample.fsproj` with project references to Orleans.FSharp, Orleans.FSharp.Runtime, Orleans.FSharp.CodeGen

**Checkpoint**: Solution builds, all projects resolve dependencies, zero warnings.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core types and plumbing that ALL user stories depend on

**CRITICAL**: No user story work can begin until this phase is complete.

- [x] T011 Implement `src/Orleans.FSharp/Prelude.fs` — common Result types, TaskHelpers (`task { }` utilities), reexport Orleans core types needed by the API (IGrain, IGrainWithStringKey, IGrainWithGuidKey, IGrainWithIntegerKey, IGrainFactory)
- [x] T012 [P] Create stub `src/Orleans.FSharp/AssemblyInfo.fs` with InternalsVisibleTo for test projects and XML doc generation enabled
- [x] T013 [P] Create `src/Orleans.FSharp.CodeGen/AssemblyAttributes.cs` with `[assembly: GenerateCodeForDeclaringAssembly]` referencing Orleans.FSharp assembly types
- [x] T014 [P] Write unit tests `tests/Orleans.FSharp.Tests/PreludeTests.fs` — verify TaskHelper functions (taskResult, taskMap, taskBind) work correctly with Unquote assertions
- [x] T014b [P] Write reflection-based contract test `tests/Orleans.FSharp.Tests/ApiSurfaceTests.fs` — scan all public types and methods in Orleans.FSharp assembly; assert none return `Async<'T>` or `FSharpAsync`; assert all public types have XML doc summary (FR-009, FR-011)
- [x] T014c [P] Write error message test `tests/Orleans.FSharp.Tests/ErrorMessageTests.fs` — trigger known error conditions (missing handler in CE, invalid config); assert error messages contain F# type names (e.g., DU case names, module paths), NOT Orleans internal types (FR-010)
- [x] T015 Verify `dotnet build` and `dotnet test` pass after foundational phase

**Checkpoint**: Foundation ready — core types available, codegen bridge wired, user story work can begin.

---

## Phase 3: User Story 1 — Grain Builder CE (Priority: P1) MVP

**Goal**: Developer defines grain behavior using `grain { }` CE, hosts it in a silo, and invokes it.

**Independent Test**: Counter grain (increment, decrement, get value) runs in test silo and responds correctly.

### Tests for User Story 1

> **Write these FIRST, ensure they FAIL before implementation**

- [x] T016 [P] [US1] Write property test `tests/Orleans.FSharp.Tests/GrainBuilderTests.fs` — verify GrainBuilder CE produces a valid GrainDefinition with defaultState, handler, persist fields set; verify missing handler produces compile-time or immediate runtime error
- [x] T017 [P] [US1] Write property test `tests/Orleans.FSharp.Tests/StateMachineProperties.fs` — FsCheck property: arbitrary command sequences applied to a counter state model always produce valid states (state >= 0)
- [x] T018 [P] [US1] Write integration test `tests/Orleans.FSharp.Integration/GrainLifecycleTests.fs` — start TestCluster, activate counter grain, send Increment, verify GetValue returns 1; test deactivation/reactivation preserves state

### Implementation for User Story 1

- [x] T019 [US1] Implement `src/Orleans.FSharp/GrainBuilder.fs` — GrainBuilder<'State,'Message> CE with `defaultState`, `handle`, `persist`, `onActivate`, `onDeactivate` custom operations; produces GrainDefinition<'State,'Message> record; XML docs on all public members
- [x] T020 [US1] Implement `src/Orleans.FSharp.Runtime/GrainDiscovery.fs` — extension method on ISiloBuilder to register F# GrainDefinitions; generates Orleans-compatible grain classes at silo startup via reflection + TypeBuilder or pre-registration pattern
- [x] T021 [US1] Implement `src/Orleans.FSharp.Sample/CounterGrain.fs` — sample counter grain using `grain { }` CE with DU state (Zero | Count of int) and commands (Increment | Decrement | GetValue)
- [x] T022 [US1] Implement `tests/Orleans.FSharp.Integration/ClusterFixture.fs` — shared xUnit IAsyncLifetime fixture using InProcessTestClusterBuilder with in-memory storage, references GrainDiscovery registration
- [x] T022b [US1] Write integration test `tests/Orleans.FSharp.Integration/GrainLifecycleTests.fs` (append) — register two grain definitions with same interface key, assert silo startup fails with descriptive error naming the conflicting types (Edge Case 5)
- [x] T023 [US1] Verify all US1 tests pass: `dotnet test --filter "FullyQualifiedName~GrainBuilder|FullyQualifiedName~StateMachine|FullyQualifiedName~GrainLifecycle"`

**Checkpoint**: A developer can define a grain via CE, run it in a test silo, and verify behavior. MVP is functional.

---

## Phase 4: User Story 2 — DU State Machines (Priority: P2)

**Goal**: Grain state modeled as discriminated union with exhaustive pattern matching for transitions.

**Independent Test**: Order-processing grain with 4 states and 5 transitions, FsCheck verifies all valid command sequences produce valid states.

### Tests for User Story 2

- [x] T024 [P] [US2] Write property test `tests/Orleans.FSharp.Tests/GrainStateTests.fs` — FsCheck: for any DU state value, `write` then `read` returns the same value (serialization roundtrip); Unquote assertions on state transitions
- [x] T025 [P] [US2] Write property test `tests/Orleans.FSharp.Tests/SerializationProperties.fs` — FsCheck: arbitrary DU values (nested records, option fields, lists) survive System.Text.Json serialize/deserialize roundtrip via FSharp.SystemTextJson

### Implementation for User Story 2

- [x] T026 [US2] Implement `src/Orleans.FSharp/GrainState.fs` — GrainState module with `read`, `write`, `clear`, `current` functions wrapping IPersistentState<'T>; handles mutable Orleans state bridge for immutable F# DUs; XML docs
- [x] T027 [US2] Configure FSharp.SystemTextJson in `src/Orleans.FSharp/Prelude.fs` — register JsonFSharpConverter as default for Orleans serialization; support DU, Record, Option, ValueOption, list, map types
- [x] T028 [US2] Implement `src/Orleans.FSharp.Sample/OrderGrain.fs` — order grain with DU state `Idle | Processing of OrderId | Completed of Result | Failed of Error` and transitions: Place, Confirm, Ship, Cancel, GetStatus
- [x] T029 [US2] Write integration test `tests/Orleans.FSharp.Integration/PersistenceRoundtripTests.fs` — activate order grain, transition through states, deactivate, reactivate, verify state survives roundtrip in TestCluster with memory storage
- [x] T029b [US2] Write deserialization error test `tests/Orleans.FSharp.Tests/SerializationProperties.fs` (append) — serialize a DU value, manually corrupt the case discriminator in JSON, attempt deserialize, assert error message contains the state type name and raw payload snippet (Edge Case 2)
- [x] T030 [US2] Verify all US2 tests pass: `dotnet test --filter "FullyQualifiedName~GrainState|FullyQualifiedName~Serialization|FullyQualifiedName~Persistence"`

**Checkpoint**: DU state machines work end-to-end with persistence. Serialization is verified by property tests.

---

## Phase 5: User Story 3 — Type-Safe Grain References (Priority: P3)

**Goal**: Fully typed grain references that prevent incorrect message types at compile time.

**Independent Test**: Two grains communicate — Producer calls Consumer — both references fully typed.

### Tests for User Story 3

- [x] T031 [P] [US3] Write unit tests `tests/Orleans.FSharp.Tests/GrainRefTests.fs` — verify `GrainRef.ofString`, `ofGuid`, `ofInt64` produce correct reference types; verify `invoke` signature enforces type constraints; test cast between interfaces

### Implementation for User Story 3

- [x] T032 [US3] Implement `src/Orleans.FSharp/GrainRef.fs` — GrainRef<'TInterface,'TKey> type, `ofString`, `ofGuid`, `ofInt64` factory functions constrained to IGrainWithXKey; `invoke` function wrapping grain method calls; XML docs on all members
- [x] T033 [US3] Add grain-to-grain reference support in GrainBuilder — `getGrain` custom operation inside `grain { }` CE that returns typed GrainRef from within a grain's handler context
- [x] T034 [US3] Write integration test `tests/Orleans.FSharp.Integration/GrainLifecycleTests.fs` (append) — test two grains communicating: ProducerGrain calls ConsumerGrain via typed reference within TestCluster
- [x] T035 [US3] Verify all US3 tests pass: `dotnet test --filter "FullyQualifiedName~GrainRef"`

**Checkpoint**: Grain-to-grain communication is type-safe. No casts, no string-based lookups.

---

## Phase 6: User Story 4 — Silo Configuration DSL (Priority: P4)

**Goal**: F# builder for Orleans silo configuration — composable, discoverable, clear errors.

**Independent Test**: Configure and start local dev silo with in-memory storage using F# builder.

### Tests for User Story 4

- [x] T036 [P] [US4] Write unit tests `tests/Orleans.FSharp.Tests/SiloConfigTests.fs` — verify SiloConfigBuilder CE produces correct SiloConfig record; test composition (later overrides earlier); test invalid config detection
- [x] T037 [P] [US4] Write integration test `tests/Orleans.FSharp.Integration/SiloConfigTests.fs` — start TestCluster using siloConfig CE, verify silo accepts grain calls

### Implementation for User Story 4

- [x] T038 [US4] Implement `src/Orleans.FSharp.Runtime/SiloConfigBuilder.fs` — SiloConfigBuilder CE with `useLocalhostClustering`, `addMemoryStorage`, `addAzureBlobStorage`, `addAdoNetStorage`, `addMemoryStreams`, `useSerilog`, `configureServices`; `applyToHost` function that applies config to HostApplicationBuilder; XML docs
- [x] T039 [US4] Implement `src/Orleans.FSharp.Sample/Program.fs` — sample silo host using `siloConfig { }` CE with localhost clustering and memory storage; verify it starts and serves counter grain
- [x] T040 [US4] Verify all US4 tests pass: `dotnet test --filter "FullyQualifiedName~SiloConfig"`

**Checkpoint**: Developer can configure and start a silo with pure F# — no C# extension method chains.

---

## Phase 7: User Story 5 — Streaming via TaskSeq (Priority: P5)

**Goal**: Produce and consume Orleans streams using TaskSeq and standard F# sequence operations.

**Independent Test**: Producer emits 100 events, consumer filters and counts — exact count verified.

### Tests for User Story 5

- [x] T041 [P] [US5] Write unit tests `tests/Orleans.FSharp.Tests/StreamingTests.fs` — verify StreamRef construction, subscribe/unsubscribe lifecycle, asTaskSeq type signature
- [x] T042 [P] [US5] Write integration test `tests/Orleans.FSharp.Integration/StreamingIntegrationTests.fs` — producer grain emits 100 events on memory stream, consumer grain applies TaskSeq.filter and TaskSeq.length, verify count matches expected

### Implementation for User Story 5

- [x] T043 [US5] Implement `src/Orleans.FSharp/Streaming.fs` — StreamRef<'T> type wrapping IAsyncStream<'T>; `getStream`, `publish`, `subscribe`, `asTaskSeq`, `unsubscribe` functions; push-to-pull bridge via Channel<'T> with bounded capacity for `asTaskSeq` (backpressure via BoundedChannelFullMode.Wait); XML docs documenting buffering semantics
- [x] T044 [US5] Wire stream provider plumbing in `src/Orleans.FSharp.Runtime/SiloConfigBuilder.fs` — `addMemoryStreams` CE keyword delegates to Orleans `AddMemoryStreams` extension; distinct from the CE keyword definition in T038 (this task wires the Orleans provider internals)
- [x] T044b [US5] Write backpressure test `tests/Orleans.FSharp.Integration/StreamingIntegrationTests.fs` (append) — producer emits 10K events with no delay, consumer applies artificial 1ms delay per event; verify no events lost and Channel backpressure engages (Edge Case 3)
- [x] T045 [US5] Verify all US5 tests pass: `dotnet test --filter "FullyQualifiedName~Streaming"`

**Checkpoint**: Streams work as TaskSeq — developers use map/filter/iter on grain event streams.

---

## Phase 8: User Story 6 — Structured Logging with Correlation (Priority: P6)

**Goal**: Automatic structured logging on all grain operations with correlation IDs across grain call chains.

**Independent Test**: Chain of 3 grain calls — all logs share correlation ID, contain grain type/id.

### Tests for User Story 6

- [x] T046 [P] [US6] Write unit tests `tests/Orleans.FSharp.Tests/LoggingTests.fs` — verify logInfo/logWarning/logError/logDebug produce structured entries; verify withCorrelation sets and propagates correlation ID; verify currentCorrelationId returns expected value
- [x] T047 [P] [US6] Write integration test `tests/Orleans.FSharp.Integration/CorrelationTests.fs` — grain A calls grain B calls grain C in TestCluster; capture logs; verify all entries share same correlation ID and contain GrainType + GrainId fields

### Implementation for User Story 6

- [x] T048 [US6] Implement `src/Orleans.FSharp/Logging.fs` — logInfo, logWarning, logError, logDebug functions with structured templates; withCorrelation scope using AsyncLocal<string>; currentCorrelationId accessor; automatic grain context enrichment (GrainType, GrainId); XML docs
- [x] T049 [US6] Implement `src/Orleans.FSharp.Runtime/SerilogIntegration.fs` — wire Serilog as ILoggerFactory in silo; enrich all log entries with grain context properties from Logging module
- [x] T050 [US6] Integrate logging into GrainBuilder — auto-emit log entries on grain activation, deactivation, message handling (update `src/Orleans.FSharp/GrainBuilder.fs` and `src/Orleans.FSharp.Runtime/GrainDiscovery.fs`)
- [x] T051 [US6] Implement `src/Orleans.FSharp.Testing/LogCapture.fs` — in-memory Serilog sink that captures CapturedLogEntry records; `captureLogs` function for test assertions
- [x] T052 [US6] Verify all US6 tests pass: `dotnet test --filter "FullyQualifiedName~Logging|FullyQualifiedName~Correlation"`

**Checkpoint**: Every grain operation produces structured logs. Cross-grain correlation works.

---

## Phase 9: User Story 7 — Testing Toolkit with FsCheck (Priority: P7)

**Goal**: In-process grain testing without full silo + FsCheck generators for state machine properties.

**Independent Test**: Property test generates 1000 random command sequences, applies to grain, invariant always holds.

### Tests for User Story 7

- [x] T053 [P] [US7] Write unit tests `tests/Orleans.FSharp.Tests/TestHarnessTests.fs` — verify createTestCluster returns working harness; verify getGrain returns typed ref; verify captureLogs returns entries; verify reset clears state; verify dispose cleans up
- [x] T054 [P] [US7] Write property tests `tests/Orleans.FSharp.Tests/FsCheckIntegrationTests.fs` — verify commandSequenceArb generates non-empty lists of valid commands; verify stateMachineProperty detects intentionally broken invariants

### Implementation for User Story 7

- [x] T055 [US7] Implement `src/Orleans.FSharp.Testing/TestHarness.fs` — TestHarness type wrapping InProcessTestCluster; createTestCluster, createTestClusterWith, getGrain, reset, dispose functions; default in-memory config with LogCapture sink pre-wired
- [x] T056 [US7] Implement `src/Orleans.FSharp.Testing/FsCheckIntegration.fs` — commandSequenceArb<'Command> Arbitrary using FsCheck.Gen; stateMachineProperty<'State,'Command> helper that folds commands over state and checks invariant; XML docs with usage examples
- [x] T057 [US7] Write end-to-end property test using TestHarness — test counter grain with FsCheck-generated command sequences through TestHarness, verify state matches pure model after each command sequence (append to `tests/Orleans.FSharp.Integration/GrainLifecycleTests.fs`)
- [x] T058 [US7] Verify all US7 tests pass: `dotnet test --filter "FullyQualifiedName~TestHarness|FullyQualifiedName~FsCheck"`

**Checkpoint**: Developers can write property-based grain tests with minimal boilerplate. Full testing toolkit operational.

---

## Phase 10: Polish & Cross-Cutting Concerns

**Purpose**: Final quality pass across all user stories

- [x] T059 [P] Verify XML documentation coverage — all public types and functions in src/Orleans.FSharp/, src/Orleans.FSharp.Runtime/, src/Orleans.FSharp.Testing/ have XML doc comments
- [x] T060 [P] Run full test suite `dotnet test` and verify zero failures, zero warnings
- [x] T061 [P] Add BenchmarkDotNet performance test `tests/Orleans.FSharp.Tests/Benchmarks.fs` — measure grain call overhead vs direct Orleans C# grain call; assert overhead < 5% (SC-003) and fail CI if threshold exceeded
- [x] T062 Complete `src/Orleans.FSharp.Sample/` — ensure CounterGrain, OrderGrain, and Program.fs work end-to-end as a runnable sample silo
- [x] T063 Validate quickstart.md — follow the quickstart guide from scratch in a temporary directory, verify all steps work; time the walkthrough end-to-end and assert completion under 15 minutes (SC-001)
- [x] T064 [P] Final `dotnet build` with TreatWarningsAsErrors across all projects — zero warnings, zero errors

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Phase 2 — first MVP deliverable
- **US2 (Phase 4)**: Depends on Phase 2; benefits from US1 (GrainBuilder) but state module is independent
- **US3 (Phase 5)**: Depends on Phase 2; uses GrainBuilder from US1
- **US4 (Phase 6)**: Depends on Phase 2; unit tests independent of US1–US3; integration test (T037) uses a minimal inline grain, not US1 CE — soft-dependency only
- **US5 (Phase 7)**: Depends on US1 (grain CE) + US4 (silo config for stream providers)
- **US6 (Phase 8)**: Depends on US1 (integrates into GrainBuilder); US4 (Serilog config)
- **US7 (Phase 9)**: Depends on US1 + US4 + US6 (wraps TestCluster with log capture)
- **Polish (Phase 10)**: Depends on all user stories complete

### Recommended Execution Order

```
Phase 1 → Phase 2 → US1 (MVP!) → US2 → US3 → US4 → US5 → US6 → US7 → Polish
                                   ↑      ↑
                                   └──────┘ US2 and US3 can run in parallel
```

### Within Each User Story

1. Tests written FIRST and verified to FAIL
2. Implementation until tests pass
3. Checkpoint validation
4. Move to next story

### Parallel Opportunities

```bash
# Phase 1 — all project files in parallel:
T002, T003, T004, T005, T006, T007, T009, T010

# Phase 2 — stubs in parallel:
T012, T013, T014

# US2 + US3 — can run in parallel after US1:
US2 (T024–T030) || US3 (T031–T035)

# US4 — independent, can run after Phase 2 (but benefits from US1 for testing):
US4 (T036–T040)
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001–T010)
2. Complete Phase 2: Foundational (T011–T015)
3. Complete Phase 3: US1 — Grain Builder CE (T016–T023)
4. **STOP and VALIDATE**: Counter grain works end-to-end
5. This is a shippable MVP — a developer can define and test grains

### Incremental Delivery

1. Setup + Foundational → builds
2. US1 → grain CE works → **MVP**
3. US2 → DU states persist
4. US3 → grain-to-grain typed
5. US4 → silo configurable
6. US5 → streaming works
7. US6 → observability complete
8. US7 → test toolkit ships
9. Polish → release-ready

---

## Notes

- [P] tasks = different files, no dependencies
- [US*] label maps to spec.md user stories
- Constitution mandates TDD — all test tasks MUST precede implementation
- `task { }` only in all code — no `async { }` (Principle VII)
- No Co-Authored-By in commits (constitution)
- Commit after each task or logical group: `feat:`, `test:`, `chore:` prefixes
