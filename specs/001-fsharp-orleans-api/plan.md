# Implementation Plan: F# Idiomatic API Layer for Orleans

**Branch**: `001-fsharp-orleans-api` | **Date**: 2026-04-02 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-fsharp-orleans-api/spec.md`

## Summary

Build an idiomatic F# API layer on top of Microsoft Orleans runtime. The layer
provides computation expressions for grain definition (`grain {}`), DU-based
state machines, type-safe grain references, TaskSeq-based streaming, structured
logging with correlation, and an FsCheck-powered testing toolkit. Orleans handles
clustering, persistence, and networking — the F# layer provides developer
ergonomics.

## Technical Context

**Language/Version**: F# 8+ / .NET 8 (LTS)
**Primary Dependencies**: Microsoft Orleans 8+, IcedTasks, FSharp.SystemTextJson,
  FSharp.Control.TaskSeq, Serilog
**Storage**: In-memory (dev), pluggable Orleans providers (production)
**Testing**: xUnit + FsCheck + Unquote + Orleans.TestingHost
**Target Platform**: .NET 8+ (cross-platform: Linux, macOS, Windows)
**Project Type**: Library (NuGet packages)
**Performance Goals**: <5% overhead vs direct Orleans C# grain calls
**Constraints**: No Async<T> in public API, all Task-based
**Scale/Scope**: 7 user stories, 4 NuGet packages, ~3K LOC estimated

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
|-----------|--------|----------|
| I. Functional First | PASS | All API uses CE, DU, pipes, immutability |
| II. F# API over Orleans Core | PASS | Strict layering: App → F# API → Orleans |
| III. Test-First | PASS | FsCheck properties + xUnit for all stories |
| IV. Property-Based Testing | PASS | FsCheck generators for state machines, serialization |
| V. Observability | PASS | Structured logging with correlation IDs |
| VI. Developer Experience | PASS | 15-min quickstart, XML docs, CE IntelliSense |
| VII. Task-Based Concurrency | PASS | task {} only, no Async in public API |
| Approved Dependencies | PASS | All deps from Tier 1/2. C# CodeGen project justified (R-008) |

## Project Structure

### Documentation (this feature)

```text
specs/001-fsharp-orleans-api/
├── plan.md              # This file
├── research.md          # Phase 0: 8 research decisions
├── data-model.md        # Phase 1: core types and relationships
├── quickstart.md        # Phase 1: 15-min getting started guide
├── contracts/
│   └── public-api.md    # Phase 1: full public API surface
└── tasks.md             # Phase 2: task breakdown (created by /speckit.tasks)
```

### Source Code (repository root)

```text
Orleans.FSharp.sln

src/
├── Orleans.FSharp/                    # Core F# API library (NuGet: Orleans.FSharp)
│   ├── Orleans.FSharp.fsproj
│   ├── AssemblyInfo.fs
│   ├── GrainBuilder.fs                # grain { } computation expression
│   ├── GrainState.fs                  # Immutable state wrapper over IPersistentState
│   ├── GrainRef.fs                    # Type-safe grain references
│   ├── Streaming.fs                   # TaskSeq-based streaming API
│   ├── Logging.fs                     # Structured logging with correlation
│   └── Prelude.fs                     # Common types, helpers, reexports
│
├── Orleans.FSharp.Runtime/            # Silo hosting + configuration (NuGet: Orleans.FSharp.Runtime)
│   ├── Orleans.FSharp.Runtime.fsproj
│   ├── SiloConfigBuilder.fs           # siloConfig { } CE
│   ├── GrainDiscovery.fs              # Registers F# grain definitions with Orleans
│   └── SerilogIntegration.fs          # Serilog sink wiring
│
├── Orleans.FSharp.CodeGen/            # C# bridge for Orleans source generators
│   ├── Orleans.FSharp.CodeGen.csproj  # THE ONLY C# PROJECT
│   └── AssemblyAttributes.cs          # [GenerateCodeForDeclaringAssembly]
│
├── Orleans.FSharp.Testing/            # Test harness (NuGet: Orleans.FSharp.Testing)
│   ├── Orleans.FSharp.Testing.fsproj
│   ├── TestHarness.fs                 # InProcessTestCluster wrapper
│   ├── LogCapture.fs                  # Captured log assertions
│   └── FsCheckIntegration.fs          # Arbitrary instances, property helpers
│
└── Orleans.FSharp.Sample/             # Example grains (not published)
    ├── Orleans.FSharp.Sample.fsproj
    ├── CounterGrain.fs
    ├── OrderGrain.fs                  # DU state machine example
    └── Program.fs                     # Sample silo host

tests/
├── Orleans.FSharp.Tests/              # Unit + property tests
│   ├── Orleans.FSharp.Tests.fsproj
│   ├── GrainBuilderTests.fs
│   ├── GrainStateTests.fs
│   ├── GrainRefTests.fs
│   ├── StreamingTests.fs
│   ├── LoggingTests.fs
│   ├── SerializationProperties.fs     # FsCheck: serialize/deserialize roundtrips
│   └── StateMachineProperties.fs      # FsCheck: DU state invariants
│
└── Orleans.FSharp.Integration/        # Integration tests (require TestCluster)
    ├── Orleans.FSharp.Integration.fsproj
    ├── ClusterFixture.fs              # Shared xUnit fixture
    ├── GrainLifecycleTests.fs
    ├── PersistenceRoundtripTests.fs
    ├── StreamingIntegrationTests.fs
    ├── CorrelationTests.fs
    └── SiloConfigTests.fs
```

**Structure Decision**: Multi-project library layout. 4 NuGet packages
(`Orleans.FSharp`, `Orleans.FSharp.Runtime`, `Orleans.FSharp.Testing`,
`Orleans.FSharp.CodeGen`) + 1 sample + 2 test projects. The C# CodeGen project
is required by Orleans source generators (see research.md R-008) and is the only
C# code in the solution. Justified complexity: each package has a distinct
deployment target (grain authors vs silo hosts vs test writers).

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| C# CodeGen project | Orleans Roslyn source generators don't work on F# projects | Manual serializer registration is error-prone and defeats codegen perf |
| 4 NuGet packages | Grain authors shouldn't depend on silo runtime; test authors shouldn't ship test harness to prod | Single package forces all transitive deps on all consumers |
