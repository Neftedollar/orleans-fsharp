<!--
  Sync Impact Report
  Version change: 1.0.0 → 1.1.0 (dependency governance added)
  Added sections:
    - Approved Dependencies (Tier 1, 2, 3 + Rejected)
  Modified sections: none
  Templates requiring updates:
    - .specify/templates/plan-template.md ⚠ pending (tech context defaults)
    - .specify/templates/spec-template.md ✅ compatible
    - .specify/templates/tasks-template.md ✅ compatible
  Follow-up TODOs: none

  Previous:
  Version change: 0.0.0 → 1.0.0 (initial ratification)
  Added principles:
    - I. Functional First
    - II. F# API over Orleans Core
    - III. Test-First (NON-NEGOTIABLE)
    - IV. Property-Based Testing
    - V. Observability
    - VI. Developer Experience
    - VII. Task-Based Concurrency
  Added sections:
    - Technology Stack
    - Development Workflow
    - Governance
  Templates requiring updates:
    - .specify/templates/plan-template.md ⚠ pending (tech context defaults)
    - .specify/templates/spec-template.md ✅ compatible
    - .specify/templates/tasks-template.md ✅ compatible
  Follow-up TODOs: none
-->

# Orlёans F# Constitution

## Core Principles

### I. Functional First

All application code MUST be written in idiomatic F#. Computation expressions,
discriminated unions, pattern matching, pipelines, and immutability are the
default tools — not afterthoughts. Code that reads like "C# in F# syntax" is a
defect. Mutable state is permitted only at the Orleans grain boundary where the
runtime demands it; business logic MUST remain pure.

### II. F# API over Orleans Core

Orleans is the distributed runtime — clustering, persistence, networking,
grain directory. We do NOT reimplement these. Instead we build an idiomatic F#
API layer on top:

- `grain { }` and other computation expressions for grain behavior definition
- Discriminated unions for grain state machines
- Type-safe grain references via generic wrappers
- F# module functions over class hierarchies wherever possible
- Orleans C# interfaces are an implementation detail, not the public API

The layering is strict:

```
F# Application Code  →  F# Idiomatic API  →  Orleans Core (C#)
```

No application code may reference Orleans internals directly; all access goes
through the F# API layer.

### III. Test-First (NON-NEGOTIABLE)

Every feature begins with a failing test. Red → Green → Refactor is the only
accepted development cycle. Code without tests MUST NOT be merged.

Coverage targets:
- Unit tests: all pure functions and state transitions
- Integration tests: grain-to-grain communication, persistence round-trips
- Contract tests: public API surface of the F# layer

Tests MUST be deterministic and fast. Flaky tests are treated as bugs with
highest priority.

### IV. Property-Based Testing

FsCheck is the primary property-based testing framework. Properties MUST be
written for:

- All state machine transitions (DU → DU)
- Serialization round-trips (serialize → deserialize = identity)
- Grain message handling (arbitrary valid commands produce valid states)
- Idempotency invariants where applicable

Traditional example-based tests complement properties but do NOT replace them.
When a property test finds a shrunk counterexample, that counterexample MUST be
added as a regression unit test.

### V. Observability

Structured logging via Microsoft.Extensions.Logging is mandatory for all grain
operations. Every grain activation, deactivation, state transition, and error
MUST produce a structured log event.

Requirements:
- Log levels MUST follow severity semantics (Debug/Info/Warning/Error/Critical)
- Correlation IDs MUST propagate across grain calls
- No string interpolation in log templates — use structured placeholders
- Logging MUST NOT alter control flow or throw exceptions

### VI. Developer Experience

The F# API MUST be discoverable and self-documenting through types. A developer
familiar with F# and the actor model MUST be able to define a grain, test it,
and run it locally within 15 minutes using only the API and XML doc comments.

Requirements:
- Computation expression builders MUST have XML documentation
- Error messages from the API MUST reference what the developer did wrong, not
  Orleans internals
- The solution MUST build with `dotnet build` and test with `dotnet test` — no
  extra tooling required
- F# scripting support (.fsx) for quick prototyping is a goal

### VII. Task-Based Concurrency

All asynchronous code MUST use `task { }` from FSharp.Control.Tasks (or the
built-in F# task CE in .NET 6+). The `async { }` CE is NOT permitted in
production code because Orleans is Task-native and wrapping introduces
unnecessary overhead and potential deadlocks.

Exceptions:
- Test helpers MAY use `async { }` if interacting with FsCheck's async
  generators
- Internal plumbing that genuinely needs cancellation semantics may use
  `Async` with documented justification

## Technology Stack

| Concern | Choice | Rationale |
|---------|--------|-----------|
| Language | F# (.NET 8+) | Primary language for all application code |
| Runtime | Microsoft Orleans 8+ | Proven virtual actor framework |
| Async | `task { }` (built-in / FSharp.Control.Tasks) | Zero-overhead Orleans interop |
| Testing | Expecto + Unquote + FsCheck (v2+); xUnit + Unquote + FsCheck (v1 legacy) | Tests-as-values, built-in perf testing, F#-first |
| Logging | Microsoft.Extensions.Logging + Serilog | Structured, filterable, sink-flexible |
| Serialization | Orleans default (System.Text.Json or MessagePack) | Interop with Orleans ecosystem |
| Build | dotnet CLI + SDK-style projects | No custom tooling |
| CI | GitHub Actions | Standard, free for OSS |

## Approved Dependencies

### Tier 1 — Core Runtime (approved, use freely)

| Package | Purpose |
|---------|---------|
| Microsoft.Orleans.Server / Client / SDK | The runtime |
| IcedTasks | ColdTask, CancellableTask, ValueTask CE extensions |
| FSharp.SystemTextJson | DU/Record/Option support for System.Text.Json |
| FSharp.Control.TaskSeq | `taskSeq {}` for Orleans streaming (IAsyncEnumerable) |
| FsToolkit.ErrorHandling | `taskResult {}` CE, Result/Option/Validation pipelines |
| Serilog + Serilog.Extensions.Logging | Structured logging sinks |
| Microsoft.Extensions.Logging | Logging abstraction |

### Tier 2 — Testing (approved, test projects only)

| Package | Purpose |
|---------|---------|
| Expecto + Expecto.FsCheck | Test runner for v2+ (tests-as-values, built-in perf testing) |
| xUnit + xunit.runner.visualstudio | Test runner for v1 legacy tests |
| FsCheck | Property-based testing (via Expecto.FsCheck or FsCheck.Xunit) |
| Unquote | F# quotation-based assertions (works with both Expecto and xUnit) |
| TypeShape | Auto-generation of FsCheck Arbitrary for grain state DUs |
| Microsoft.Orleans.TestingHost | Integration test silo |

### Tier 3 — Evaluate Per Feature (pull in when needed, document why)

| Package | When |
|---------|------|
| FsCodec | Event-sourced grains with versioned state |
| FSharp.UMX | Type-safe GrainId via Units of Measure |
| OpenTelemetry | Distributed tracing across grain calls |
| TypeShape | Generic programming for serialization codegen |
| Fantomas | CI formatting enforcement |
| FParsec | Runtime DSL parsing (stream filters, query language) |

### Rejected (do NOT add without constitution amendment)

| Package | Reason |
|---------|--------|
| FSharpPlus | Heavy transitive deps, learning curve, modern F# covers our needs |
| Akkling / Akka.NET | We build ON Orleans, not beside it |
| FluentValidation | C#-centric; F# Result + active patterns are superior |
| FSharpx.Extras | Overlap with modern F# standard library |
| Newtonsoft.Json | Legacy; System.Text.Json + FSharp.SystemTextJson |
| Paket | Unnecessary overhead; dotnet CLI + NuGet sufficient |
| Logary | Niche; Serilog has broader ecosystem |

## Development Workflow

### Branching

Feature branches follow `NNN-feature-name` sequential numbering. All work
happens on feature branches; `main` MUST always build and pass all tests.

### Commit Messages

Conventional commits format: `type: description`

Types: `feat`, `fix`, `test`, `refactor`, `docs`, `chore`, `perf`

No co-author trailers. No AI attribution in commits. The code speaks for
itself.

### Code Review Gates

Every PR MUST pass:
1. `dotnet build` with zero warnings (TreatWarningsAsErrors)
2. `dotnet test` with all tests green
3. FsCheck property tests included for new state transitions
4. Structured logging present for new grain operations
5. No direct Orleans API usage in application code (F# layer only)

### Project Structure

```
src/
├── Orlean.FSharp/              # F# API layer (computation expressions,
│   ├── GrainBuilder.fs         #   type-safe references, DU state mgmt)
│   ├── Streaming.fs
│   ├── Configuration.fs
│   └── Logging.fs
├── Orlean.FSharp.Runtime/      # Orleans silo hosting + configuration
└── Orlean.FSharp.Sample/       # Example grains demonstrating the API

tests/
├── Orlean.FSharp.Tests/        # Unit + property tests for the API layer
├── Orlean.FSharp.Integration/  # Grain-to-grain, persistence round-trips
└── Orlean.FSharp.Contract/     # Public API surface contract tests
```

## Governance

This constitution supersedes all other development practices for this
repository. Amendments require:

1. A proposal describing the change and its rationale
2. An updated constitution version following SemVer
3. Propagation check across all `.specify/` templates

Compliance is verified at every PR through the Code Review Gates above.
Use this constitution as the source of truth for architectural decisions.
The `CLAUDE.md` file (if present) provides runtime development guidance
but MUST NOT contradict this constitution.

**Version**: 1.1.0 | **Ratified**: 2026-04-02 | **Last Amended**: 2026-04-02
