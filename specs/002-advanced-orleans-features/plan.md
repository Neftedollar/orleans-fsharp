# Implementation Plan: Advanced Orleans Features (v2)

**Branch**: `002-advanced-orleans-features` | **Date**: 2026-04-03 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/002-advanced-orleans-features/spec.md`

## Summary

Extend the Orleans.FSharp API layer with advanced Orleans features: reminders,
timers, observers, reentrancy, stateless workers, event sourcing (with Marten),
F# analyzer, dotnet template, and .fsx scripting. All features build on the
existing v1.0 foundation (183 tests, 7 projects, 73 source files).

## Technical Context

**Language/Version**: F# 9+ / .NET 10
**Primary Dependencies**: Orleans 10.0.1 (existing), Marten (new for event sourcing),
  FSharp.Analyzers.SDK (new for analyzer), Interflare.Orleans.Marten (new)
**Storage**: In-memory (dev), PostgreSQL via Marten (event sourcing), pluggable
**Testing**: xUnit + FsCheck 3.x + Unquote + Orleans.TestingHost (existing)
**Target Platform**: .NET 10 (cross-platform)
**Project Type**: Library extension (NuGet packages)
**Constraints**: No Async<T> in public API, all Task-based

## Constitution Check

| Principle | Status | Evidence |
|-----------|--------|----------|
| I. Functional First | PASS | CE keywords, DU events, module functions |
| II. F# API over Orleans Core | PASS | All Orleans APIs wrapped |
| III. Test-First | PASS | TDD for all features |
| IV. Property-Based Testing | PASS | FsCheck for event sourcing invariants |
| V. Observability | PASS | Logging integrated into all new features |
| VI. Developer Experience | PASS | dotnet template, .fsx scripting, analyzer |
| VII. Task-Based Concurrency | PASS | task {} enforced by analyzer |

## Project Structure

### New Source Files

```text
src/Orleans.FSharp/
├── Reminders.fs              # US1: Reminder module + onReminder CE keyword
├── Timers.fs                 # US1: Timer module functions
├── Observers.fs              # US2: Observer subscribe/notify/cleanup
├── Scripting.fs              # US8: quickStart() for .fsx

src/Orleans.FSharp.Runtime/
├── GrainDiscovery.fs         # MODIFIED: IRemindable, [Reentrant], [StatelessWorker]
├── SiloConfigBuilder.fs      # MODIFIED: addMartenStorage, addReminderStorage

src/Orleans.FSharp.EventSourcing/  # NEW PROJECT (NuGet: Orleans.FSharp.EventSourcing)
├── Orleans.FSharp.EventSourcing.fsproj
├── EventSourcedGrainBuilder.fs   # US5: eventSourcedGrain { } CE
├── EventStore.fs                 # US5: Marten event store integration
└── MartenConfig.fs               # US5: Marten configuration helpers

src/Orleans.FSharp.Analyzers/     # NEW PROJECT (NuGet: Orleans.FSharp.Analyzers)
├── Orleans.FSharp.Analyzers.fsproj
└── AsyncUsageAnalyzer.fs         # US6: async {} detection

templates/orleans-fsharp/          # NEW: dotnet new template
├── .template.config/
│   └── template.json
├── MyApp.Grains/
├── MyApp.Silo/
├── MyApp.CodeGen/
└── MyApp.Tests/

tests/Orleans.FSharp.Tests/
├── ReminderTests.fs           # US1
├── TimerTests.fs              # US1
├── ObserverTests.fs           # US2
├── ReentrancyTests.fs         # US3
├── StatelessWorkerTests.fs    # US4
├── EventSourcingTests.fs      # US5
├── AnalyzerTests.fs           # US6

tests/Orleans.FSharp.Integration/
├── ReminderIntegrationTests.fs      # US1
├── ObserverIntegrationTests.fs      # US2
├── ReentrancyIntegrationTests.fs    # US3
├── StatelessWorkerIntegrationTests.fs # US4
├── EventSourcingIntegrationTests.fs # US5
├── TemplateTests.fs                 # US7
├── ScriptingTests.fs                # US8
```

### New NuGet Packages

| Package | Purpose |
|---------|---------|
| Orleans.FSharp.EventSourcing | Event sourcing CE + Marten integration |
| Orleans.FSharp.Analyzers | F# async usage analyzer |
| Orleans.FSharp.Templates | dotnet new template package |

## Complexity Tracking

| Decision | Why Needed | Simpler Alternative Rejected |
|----------|-----------|------------------------------|
| Separate EventSourcing project | Marten is heavy dependency, not everyone needs PostgreSQL | Adding Marten to core would bloat all consumers |
| Separate Analyzers project | Analyzer SDK has different lifecycle than runtime | Embedding in core would break analyzer distribution |
| C# CodeGen updates for Reentrant/StatelessWorker | Orleans attributes are C# only | No alternative — Orleans source generators are C#-only |
