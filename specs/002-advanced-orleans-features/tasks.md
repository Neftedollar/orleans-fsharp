# Tasks: Advanced Orleans Features (v2)

**Input**: Design documents from `/specs/002-advanced-orleans-features/`
**Prerequisites**: plan.md, spec.md, research.md
**Tests**: Included — constitution mandates TDD and property-based testing.
**Organization**: Tasks grouped by user story for independent implementation.

## Format: `[ID] [P?] [Story] Description`

---

## Phase 1: Setup (New Projects + Dependencies)

**Purpose**: Create new projects, add dependencies for v2 features

- [x] V001 Create F# class library `src/Orleans.FSharp.EventSourcing/Orleans.FSharp.EventSourcing.fsproj` with dependencies: Marten, Interflare.Orleans.Marten; verify exact Orleans event sourcing NuGet package name (Microsoft.Orleans.EventSourcing or Microsoft.Orleans.Streaming.EventHubs — check NuGet); project refs: Orleans.FSharp, Orleans.FSharp.Runtime
- [x] V002 [P] Create F# class library `src/Orleans.FSharp.Analyzers/Orleans.FSharp.Analyzers.fsproj` with dependency: FSharp.Analyzers.SDK
- [x] V003 [P] Create `templates/` directory stub (empty placeholder — full template created in Phase 8)
- [x] V004 Add all new projects to `Orleans.FSharp.slnx`; add Orleans.Reminders.Memory package to Orleans.FSharp.Runtime for reminder storage; verify `dotnet build` succeeds
- [x] V005 [P] Add reminder storage configuration to ClusterFixture — `AddMemoryReminderService` for integration tests
- [x] V005b [P] Add reflection-based API surface test `tests/Orleans.FSharp.Tests/ApiSurfaceV2Tests.fs` — scan all new v2 public types/methods across Orleans.FSharp, Orleans.FSharp.EventSourcing, Orleans.FSharp.Analyzers; assert none return Async<'T> (FR-011)

**Checkpoint**: New projects build, all existing 183 tests still pass.

---

## Phase 2: User Story 1 — Reminders and Timers (Priority: P1)

**Goal**: Persistent reminders + in-memory timers via F# module functions and CE keywords.

**Independent Test**: Grain registers reminder, deactivates, reactivates, reminder still fires.

### Tests (FIRST!)

- [x] V006 [P] [US1] Write unit tests `tests/Orleans.FSharp.Tests/ReminderTests.fs` — test onReminder CE keyword adds handler to GrainDefinition; test Reminder.register/unregister type signatures; Unquote assertions
- [x] V007 [P] [US1] Write unit tests `tests/Orleans.FSharp.Tests/TimerTests.fs` — test Timers.register returns IGrainTimer; test timer callback signature; test disposal pattern
- [x] V008 [P] [US1] Write integration test `tests/Orleans.FSharp.Integration/ReminderIntegrationTests.fs` — register reminder on grain, wait for fire, verify state updated; test reminder survives deactivation/reactivation; test timer does NOT survive deactivation; test cancel stops firing
- [x] V008b [P] [US1] Write FsCheck property test: random sequences of activate/deactivate always preserve reminder registration (FR-002 persistence invariant)
- [x] V008c [P] [US1] Write integration test: reminder handler throws exception → log error and continue, reminder still fires next period (Edge Case 1)

### Implementation

- [x] V009 [US1] Implement `src/Orleans.FSharp/Reminders.fs` — `Reminder` module with `register`, `unregister`, `get` functions; `onReminder` data for GrainDefinition; XML docs
- [x] V010 [US1] Implement `src/Orleans.FSharp/Timers.fs` — `Timers` module with `register`, `registerWithState` functions returning IGrainTimer; XML docs
- [x] V011 [US1] Update `src/Orleans.FSharp/GrainBuilder.fs` — add `onReminder` custom operation to grain CE; add `ReminderHandlers` field to GrainDefinition record
- [x] V012 [US1] Update `src/Orleans.FSharp.Runtime/GrainDiscovery.fs` — FSharpGrain implements `IRemindable`; `ReceiveReminder` delegates to registered handlers by name; expose timer registration through GrainContext
- [x] V013 [US1] Update `src/Orleans.FSharp.Runtime/SiloConfigBuilder.fs` — add `addMemoryReminderService` CE keyword
- [x] V014 [US1] Verify all US1 tests pass

**Checkpoint**: Reminders persist across deactivation. Timers work in-memory. Both idiomatic F#.

---

## Phase 3: User Story 2 — Grain Observers (Priority: P2)

**Goal**: Pub/sub between grains and clients via F# Observer module.

**Independent Test**: Client subscribes, grain notifies, client receives. Unsubscribe stops delivery.

### Tests (FIRST!)

- [x] V015 [P] [US2] Write unit tests `tests/Orleans.FSharp.Tests/ObserverTests.fs` — test Observer.createRef/deleteRef type signatures; test subscribe returns IDisposable; Unquote assertions
- [x] V016 [P] [US2] Write integration test `tests/Orleans.FSharp.Integration/ObserverIntegrationTests.fs` — subscribe to grain, receive 10 notifications in order; unsubscribe stops delivery; test ObserverManager auto-expires stale subscription after timeout (simulate disconnect by deleting object reference without unsubscribe); verify grain notify does not crash on expired observer (Edge Case 2)

### Implementation

- [x] V017 [US2] Implement `src/Orleans.FSharp/Observers.fs` — `Observer` module with `createRef`, `deleteRef`, `subscribe` (returns IDisposable that auto-calls deleteRef); `ObserverManager` F# wrapper; XML docs
- [x] V018 [US2] Create sample observer grain in `src/Orleans.FSharp.Sample/ChatGrain.fs` — chat grain with pub/sub notifications; C# CodeGen bridge in `src/Orleans.FSharp.CodeGen/ChatGrainImpl.cs`
- [x] V019 [US2] Verify all US2 tests pass

**Checkpoint**: Pub/sub works with automatic cleanup on unsubscribe/disconnect.

---

## Phase 4: User Story 3 — Reentrancy (Priority: P3)

**Goal**: `reentrant` and `interleave` CE keywords for concurrency control.

**Independent Test**: Reentrant grain processes two messages concurrently (timing-verified).

### Tests (FIRST!)

- [x] V020 [P] [US3] Write unit tests `tests/Orleans.FSharp.Tests/ReentrancyTests.fs` — test `reentrant` CE keyword sets flag on GrainDefinition; test `interleave` keyword; Unquote assertions
- [x] V021 [P] [US3] Write integration test `tests/Orleans.FSharp.Integration/ReentrancyIntegrationTests.fs` — reentrant grain processes two concurrent messages faster than sequential; non-reentrant grain processes sequentially

### Implementation

- [x] V022 [US3] Update `src/Orleans.FSharp/GrainBuilder.fs` — add `reentrant` and `interleave` custom operations; add `IsReentrant` and `InterleavedMethods` fields to GrainDefinition
- [x] V023 [US3] Update C# CodeGen pattern — grain classes conditionally get `[Reentrant]` attribute; interleaved methods get `[AlwaysInterleave]`; document pattern in `src/Orleans.FSharp.CodeGen/`
- [x] V024 [US3] Create sample reentrant grain + CodeGen bridge
- [x] V025 [US3] Verify all US3 tests pass

**Checkpoint**: Reentrancy configurable via CE. Concurrent processing verified.

---

## Phase 5: User Story 4 — Stateless Workers (Priority: P4)

**Goal**: `statelessWorker` keyword for load-balanced stateless grains.

**Independent Test**: Stateless worker called 1000 times; multiple activations exist.

### Tests (FIRST!)

- [x] V026 [P] [US4] Write unit tests `tests/Orleans.FSharp.Tests/StatelessWorkerTests.fs` — test `statelessWorker` keyword sets flag; test maxActivations parameter; test persist keyword is rejected
- [x] V027 [P] [US4] Write integration test `tests/Orleans.FSharp.Integration/StatelessWorkerIntegrationTests.fs` — create separate 2-silo TestCluster fixture (InitialSilosCount=2); stateless worker processes messages; verify multiple activations via activation count or silo placement

### Implementation

- [x] V028 [US4] Update `src/Orleans.FSharp/GrainBuilder.fs` — add `statelessWorker` custom operation with optional `maxActivations` parameter; validate no `persist` keyword with stateless workers
- [x] V029 [US4] Update C# CodeGen pattern — stateless worker grain classes get `[StatelessWorker(n)]` attribute
- [x] V030 [US4] Verify all US4 tests pass

**Checkpoint**: Stateless workers scalable across silos. No-state enforcement works.

---

## Phase 6: User Story 5 — Event Sourcing + Marten (Priority: P5)

**Goal**: `eventSourcedGrain { }` CE wrapping Orleans JournaledGrain with Marten persistence.

**Independent Test**: Event-sourced grain processes commands, persists events, rebuilds state from replay. FsCheck verifies fold invariant.

### Tests (FIRST!)

- [x] V031 [P] [US5] Write unit tests `tests/Orleans.FSharp.Tests/EventSourcingTests.fs` — test CE produces valid EventSourcedGrainDefinition; test apply function folds events correctly; FsCheck property: fold(events) = sequential apply; test command handler produces expected events
- [x] V032 [P] [US5] Write integration test `tests/Orleans.FSharp.Integration/EventSourcingIntegrationTests.fs` — event-sourced grain processes commands, events persisted; deactivate/reactivate, state rebuilt from events; query event history; FsCheck: arbitrary command sequences produce consistent state
- [x] V032b [P] [US5] Write integration test: simulate storage failure during event sourcing (invalid connection string or mock), verify grain returns error and refuses further commands until storage recovers (Edge Case 4)

### Implementation

- [x] V033 [US5] Implement `src/Orleans.FSharp.EventSourcing/EventSourcedGrainBuilder.fs` — `eventSourcedGrain { }` CE with `defaultState`, `apply` (event -> state -> state), `handle` (state -> command -> event list), `logConsistencyProvider`; EventSourcedGrainDefinition record; XML docs
- [x] V034 [US5] Implement `src/Orleans.FSharp.EventSourcing/EventStore.fs` — F# wrapper over JournaledGrain methods: raiseEvent, confirmEvents, retrieveEvents, getVersion; connection issue handling
- [x] V035 [US5] Implement `src/Orleans.FSharp.EventSourcing/MartenConfig.fs` — Marten silo configuration helpers: `addMartenStorage`, `addMartenClustering`, `addMartenReminders`; XML docs
- [x] V036 [US5] Create C# CodeGen for JournaledGrain — EventSourcedFSharpGrain<TState, TEvent> dispatcher inheriting JournaledGrain; Apply methods delegate to F# apply function
- [x] V037 [US5] Create sample event-sourced grain — BankAccountGrain with Deposit/Withdraw/GetBalance; C# CodeGen bridge
- [x] V038 [US5] Verify all US5 tests pass

**Checkpoint**: Event sourcing works end-to-end. State derived from events. FsCheck verifies invariants.

---

## Phase 7: User Story 6 — F# Analyzer (Priority: P6)

**Goal**: Compile-time warning OF0001 when `async { }` is used instead of `task { }`.

**Independent Test**: Test project with async {} triggers warning. Project with only task {} is clean.

### Tests (FIRST!)

- [x] V039 [P] [US6] Write tests `tests/Orleans.FSharp.Tests/AnalyzerTests.fs` — test analyzer detects `async { }` in sample code; test analyzer ignores `task { }`; test opt-out attribute suppresses warning

### Implementation

- [x] V040 [US6] Implement `src/Orleans.FSharp.Analyzers/AsyncUsageAnalyzer.fs` — FSharp.Analyzers.SDK analyzer scanning for `SynExpr.ComputationExpr` with async builder; emit diagnostic OF0001; support `[<AllowAsync>]` opt-out attribute; XML docs
- [x] V041 [US6] Verify analyzer tests pass

**Checkpoint**: async {} detected at compile-time in Orleans.FSharp projects.

---

## Phase 8: User Story 7 — dotnet new Template (Priority: P7)

**Goal**: `dotnet new orleans-fsharp -n MyApp` scaffolds a complete working project.

**Independent Test**: Generated project builds and tests pass out of the box.

### Tests (FIRST!)

- [x] V042 [P] [US7] Write test `tests/Orleans.FSharp.Integration/TemplateTests.fs` — install template, generate project in temp dir, run dotnet build, run dotnet test, verify zero warnings and all tests pass; also test: run template into existing non-empty dir → verify non-zero exit code with clear error (Edge Case 5)

### Implementation

- [x] V043 [US7] Create template at `templates/orleans-fsharp/` — MyApp.Grains (with counter grain), MyApp.Silo (with siloConfig CE), MyApp.CodeGen (C# bridge), MyApp.Tests (FsCheck property tests); solution file; Directory.Build.props
- [x] V044 [US7] Create `templates/orleans-fsharp/.template.config/template.json` — template metadata: shortName "orleans-fsharp", identity, defaultName, sourceName for substitution
- [x] V045 [US7] Package template as NuGet: create `Orleans.FSharp.Templates.csproj` with `<PackageType>Template</PackageType>`
- [x] V046 [US7] Verify template tests pass

**Checkpoint**: `dotnet new orleans-fsharp` produces buildable, testable project.

---

## Phase 9: User Story 8 — .fsx Scripting (Priority: P8)

**Goal**: Interactive grain prototyping in F# scripts with `Scripting.quickStart()`.

**Independent Test**: .fsx script defines grain, starts silo, calls grain in ~20 lines.

### Tests (FIRST!)

- [x] V047 [P] [US8] Write test `tests/Orleans.FSharp.Integration/ScriptingTests.fs` — test Scripting.quickStart returns working silo; test grain can be called from script context; test silo shutdown cleans up

### Implementation

- [x] V048 [US8] Implement `src/Orleans.FSharp/Scripting.fs` — `Scripting` module with `quickStart` (starts in-process silo with localhost + memory defaults), `getGrain`, `shutdown`; designed for .fsx REPL usage; XML docs
- [x] V049 [US8] Create example script `samples/quickstart.fsx` — complete working example with `#r "nuget:"` references, grain definition, silo start, grain call, print result
- [x] V049b [US8] Verify `samples/quickstart.fsx` is ≤ 20 lines of code excluding comments and blank lines (SC-006)
- [x] V050 [US8] Verify scripting tests pass

**Checkpoint**: .fsx scripting works for interactive grain exploration.

---

## Phase 10: Polish & Cross-Cutting

**Purpose**: Final quality pass

- [x] V051 [P] Verify XML documentation coverage on all new public types and functions
- [x] V052 [P] Run full test suite `dotnet test` — verify zero failures, zero warnings
- [x] V053 [P] Update `specs/002-advanced-orleans-features/quickstart.md` with examples for each new feature
- [x] V054 Final `dotnet build` with TreatWarningsAsErrors — zero warnings

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately
- **US1 Reminders/Timers (Phase 2)**: Depends on Phase 1 — MVP for v2
- **US2 Observers (Phase 3)**: Depends on Phase 1; independent of US1
- **US3 Reentrancy (Phase 4)**: Depends on Phase 1; independent of US1-US2
- **US4 Stateless Workers (Phase 5)**: Depends on Phase 1; independent of US1-US3
- **US5 Event Sourcing (Phase 6)**: Depends on Phase 1 (new project); heavy, run last of core features
- **US6 Analyzer (Phase 7)**: Depends on Phase 1; fully independent of US1-US5
- **US7 Template (Phase 8)**: Depends on US1-US5 completion (template should include all features)
- **US8 Scripting (Phase 9)**: Depends on Phase 1; independent of most features
- **Polish (Phase 10)**: Depends on all

### Recommended Execution Order

```
Phase 1 → US1 (Reminders) → US2 (Observers) → US3+US4 (parallel) → US5 (EventSourcing) → US6+US8 (parallel) → US7 (Template) → Polish
```

### Parallel Opportunities

```
# US1 + US2 can run in parallel (different files)
# US3 + US4 can run in parallel (GrainBuilder: different fields; CodeGen: different attributes — no conflict)
# US6 + US8 can run in parallel (analyzer + scripting are independent projects)
```

---

## Notes

- All existing 183 tests MUST continue passing throughout
- v1.0 API is frozen — no breaking changes to existing modules
- C# CodeGen updates must preserve existing grain classes
- Marten integration is optional — event sourcing works with Orleans built-in providers too
- Template should reflect final state of all v2 features
