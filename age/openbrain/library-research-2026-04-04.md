# Orleans.FSharp — Complementary Library Research
**Date:** 2026-04-04
**Scope:** F# and .NET libraries that complement or enhance Orleans.FSharp
**Stored in openbrain:** category `library-research`, tags `["orleans-fsharp", "libraries", "research"]`

---

## Context

Orleans.FSharp is an F#-first API layer over Microsoft Orleans virtual actors. Current stack:

| Layer | Libraries |
|---|---|
| Core | F# 9+, .NET 10, Orleans 10.0.1 |
| Async | IcedTasks (ColdTask, CancellableTask, valueTask) |
| Serialization | FSharp.SystemTextJson v1.x |
| Streaming | FSharp.Control.TaskSeq |
| Logging | Serilog |
| Error handling | FsToolkit.ErrorHandling v5.2.0 |
| Event sourcing | Marten v8.x (via Orleans.FSharp.EventSourcing) |
| Testing | xUnit, FsCheck v3, Unquote v7, TypeShape v10 |
| Code gen | TypeShape, FSharp.Analyzers.SDK |

---

## 1. Validation Libraries

### Validus
| Field | Value |
|---|---|
| NuGet | `Validus` |
| Version | **4.1.4** |
| Repo | https://github.com/pimbrouwers/Validus |
| License | MIT |

**What it solves:** Pure F# validation with applicative semantics — collects *all* errors rather than short-circuiting on the first failure. Ships with built-in validators for strings, ints, floats, DateTimes, and optionals.

**API highlights:**
```fsharp
open Validus

// Applicative CE — collect ALL validation errors
let validateCommand (cmd: CreateOrderCommand) =
    validate {
        let! customerId = Check.String.notEmpty "CustomerId" cmd.CustomerId
        and! amount     = Check.Decimal.greaterThan 0m "Amount" cmd.Amount
        and! email      = Email.Of "Email" cmd.Email   // custom validator
        return { CustomerId = customerId; Amount = amount; Email = email }
    }

// Compose validators with <+> (AND) and >=> (THEN)
let emailValidator =
    Check.String.betweenLen 5 512
    <+> Check.WithMessage.String.pattern @"[^@]+@[^\.]+\..+" (sprintf "Please provide a valid %s")
```

**Fit for Orleans.FSharp:** Excellent. Fills a different role from FsToolkit.ErrorHandling — Validus handles the *validation layer* (DTO → domain type lifting, error accumulation), while FsToolkit handles *Result/Task chaining* downstream. They are composable: `validate {}` returns `Result<'T, ValidationErrors>`, which maps naturally into FsToolkit's `Result` CEs.

Ideal placement: inside grain message handlers before `apply`/`handle` dispatch.

**Verdict:** High-priority addition.

---

### FluentValidation
| Field | Value |
|---|---|
| NuGet | `FluentValidation` |
| Version | **12.1.1** |

**What it solves:** Fluent builder API for .NET validation, very popular in C# ecosystems.

**Fit for Orleans.FSharp:** Poor. Class-based `AbstractValidator<T>` is verbose from F# and no dedicated F# wrapper exists. Validus is strictly superior for this codebase's style requirements. Mention for completeness only.

**Verdict:** Not recommended.

---

## 2. Resilience / Retry

### Polly 8
| Field | Value |
|---|---|
| NuGet | `Polly` |
| Version | **8.x** (current) |
| Repo | https://github.com/App-vNext/Polly |
| License | BSD-3-Clause |

**What it solves:** Composable resilience strategies — Retry, CircuitBreaker, Timeout, Fallback, Hedging, RateLimiter — via a unified `ResiliencePipeline` builder.

**F# / IcedTasks integration:** Polly 8's async API is `ValueTask`-based. The Polly docs explicitly document F# + IcedTasks as the recommended pattern:

```fsharp
open IcedTasks
open Polly

let pipeline =
    ResiliencePipelineBuilder()
        .AddRetry(RetryStrategyOptions(
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(200)))
        .AddTimeout(TimeSpan.FromSeconds(10))
        .Build()

// Inside a grain method:
let! result =
    pipeline.ExecuteAsync(fun ct ->
        valueTask {
            let! response = externalHttpClient.GetAsync(url, ct)
            return! response.Content.ReadAsStringAsync()
        }, cancellationToken)
```

**Critical Orleans caveat:** Do NOT wrap grain-to-grain calls in Polly retry loops. Orleans has its own request retries and timeout infrastructure at the messaging layer. Polly belongs at the *external service boundary* inside grains — HTTP clients, database calls, third-party APIs, etc.

**Verdict:** High-priority addition for external I/O patterns. No F# wrapper needed.

---

## 3. Property-Based Testing: Hedgehog vs FsCheck

### FsCheck 3 (already in use)
| Field | Value |
|---|---|
| NuGet | `FsCheck`, `FsCheck.Xunit` |
| Version | **3.x** |

**Strengths:**
- Mature, stable, large ecosystem
- Deep xUnit integration via `[<Property>]` attribute — fits existing test structure
- `Arbitrary<'T>` type classes work with TypeShape-based `GrainArbitrary` in `Orleans.FSharp.Testing`
- `FsCheck.Experimental.StateMachine` for model-based testing (operation shrinking, `OperationResult` tracking)

**Weaknesses:**
- Shrinking is separate from generation — can miss minimal counterexamples for dependent variables
- `StateMachine` module is still marked Experimental

**Best for:** Existing grain Arbitrary generation, xUnit `[<Property>]` tests, `HandlerCompositionProperties`, `SerializationProperties`.

---

### Hedgehog
| Field | Value |
|---|---|
| NuGet | `Hedgehog` |
| Version | **0.x** (fsharp-hedgehog) |
| Repo | https://github.com/hedgehogqa/fsharp-hedgehog |
| License | Apache-2.0 |

**What it solves:** Integrated generation + shrinking via lazy Range-based trees. Finds smaller minimal counterexamples, especially for tests with dependent variables.

**API:**
```fsharp
open Hedgehog

let propFoldIsAssociative =
    property {
        let! events = Gen.list (Range.linear 0 50) genEvent
        let! chunks = Gen.int32 (Range.linear 1 10)
        // Hedgehog auto-shrinks both events and chunks together
        return applyFold events = applyFold (List.chunkBySize chunks events |> List.concat)
    }

Property.check propFoldIsAssociative
// With custom config:
PropertyConfig.defaults
|> PropertyConfig.withTests 500<tests>
|> PropertyConfig.withShrinks 200<shrinks>
|> Property.checkWith propFoldIsAssociative
```

**Weaknesses vs FsCheck:**
- No xUnit attribute integration — tests must be wrapped manually (`testCase "..." <| fun () -> Property.check prop`)
- Smaller ecosystem, less mature test runner integration
- Cannot reuse existing `GrainArbitrary` (TypeShape-based) directly

**Trade-off summary:**

| Dimension | FsCheck 3 | Hedgehog |
|---|---|---|
| Shrinking quality | Good (separate shrinker) | Excellent (integrated tree) |
| Dependent variable shrinking | Weaker | Stronger |
| xUnit integration | First-class `[<Property>]` | Manual only |
| TypeShape / GrainArbitrary reuse | Yes | No |
| State machine testing | `FsCheck.Experimental.StateMachine` | Manual with `property {}` |
| Ecosystem maturity | High | Medium |

**Recommendation:** Keep FsCheck as the primary framework. Introduce Hedgehog *selectively* for new property modules where shrinking quality matters most — specifically `GrainHandlerStateMachineProperties.fs` (already added to the test project) and complex event-sourcing fold invariants.

---

## 4. Observability

### OpenTelemetry .NET SDK
| Field | Value |
|---|---|
| NuGet | `OpenTelemetry`, `OpenTelemetry.Exporter.Console`, `OpenTelemetry.Extensions.Hosting` |
| Version | **1.15.0** |
| Repo | https://github.com/open-telemetry/opentelemetry-dotnet |

**What it solves:** Industry-standard distributed tracing, metrics, and logs via `ActivitySource` (traces) and `Meter` (metrics). No F# wrapper needed — the .NET API surface is clean.

**Orleans 10 integration:** Orleans 10 emits metrics via a `Meter` named `"Microsoft.Orleans"`. Subscribe in silo configuration:
```fsharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(fun m ->
        m.AddMeter("Microsoft.Orleans")
         .AddMeter("Orleans.FSharp")   // project-level meter
         .AddConsoleExporter() |> ignore)
    .WithTracing(fun t ->
        t.AddSource("Orleans.FSharp")
         .AddConsoleExporter() |> ignore)
|> ignore
```

**F# helper pattern for Orleans.FSharp.Telemetry:**
```fsharp
// Grain-scoped activity helper
let private source = ActivitySource("Orleans.FSharp", "1.0.0")

let inline withActivity name (tags: (string * obj) list) (work: unit -> Task<'T>) =
    task {
        use activity = source.StartActivity(name, ActivityKind.Internal)
        tags |> List.iter (fun (k, v) -> activity |> Option.iter (fun a -> a.SetTag(k, v) |> ignore))
        let! result = work ()
        activity |> Option.iter (fun a -> a.SetStatus(ActivityStatusCode.Ok) |> ignore)
        return result
    }
```

**Verdict:** No new package required beyond what Orleans Runtime already pulls in. The value is in building F#-idiomatic helper wrappers inside `Orleans.FSharp.Telemetry`.

---

### Serilog.Sinks.OpenTelemetry
| Field | Value |
|---|---|
| NuGet | `Serilog.Sinks.OpenTelemetry` |
| Version | **4.2.0** (May 2025) |
| Repo | https://github.com/serilog/serilog-sinks-opentelemetry |
| License | Apache-2.0 |

**What it solves:** Sends Serilog log events to any OTLP endpoint (Jaeger, Grafana, OTEL Collector, etc.) over gRPC or HTTP. One-to-one mapping: Serilog properties → OTel log attributes. No OTel SDK dependency — just the exporter protocol.

**Why it matters for this project:** Serilog is already the project's logging library. This sink connects it into the full OTel pipeline without changing the logging API surface.

```fsharp
Log.Logger <-
    LoggerConfiguration()
        .WriteTo.OpenTelemetry(fun opts ->
            opts.Endpoint <- "http://localhost:4318/v1/logs"
            opts.Protocol <- OtlpProtocol.HttpProtobuf)
        .CreateLogger()
```

**Verdict:** High-value, low-friction addition.

---

### prometheus-net / Prometheus exporters
| Field | Value |
|---|---|
| NuGet | `prometheus-net` |
| Version | **8.2.1** |

**Modern approach:** Rather than using the outdated `Orleans.TelemetryConsumers.Prometheus` (v0.0.3, abandoned) or `Orleans.AspNetCore.Prometheus` (v0.1.3), use the OTel Prometheus exporter:

```
OpenTelemetry.Exporter.Prometheus.AspNetCore   v1.15.x (beta)
```

This routes Orleans metrics through the OTel pipeline to a `/metrics` scrape endpoint. The beta status is the only concern.

**Verdict:** Use the OTel exporter approach rather than direct prometheus-net integration.

---

## 5. Serialization

### FSharp.SystemTextJson (already in use)
Remains the primary recommendation. Handles F# DUs, records, options, and lists natively. Strong integration with Orleans grain state serialization.

---

### MemoryPack
| Field | Value |
|---|---|
| NuGet | `MemoryPack` |
| Version | **0.9.x** |
| Repo | https://github.com/Cysharp/MemoryPack |
| License | MIT |

**What it solves:** Zero-encoding binary serialization — no conversion overhead, direct memory copy for unmanaged types. Benchmarks show 10–50x faster than JSON for hot paths.

**F# situation:** MemoryPack's source generator (`[MemoryPackable]`) only works with C# partial classes. For F# types, custom formatters are required:

```fsharp
// For each F# DU, write a custom MemoryPackFormatter:
type GrainStateFormatter() =
    inherit MemoryPackFormatter<GrainState>()

    override _.Serialize(writer: byref<_>, value: byref<GrainState option>) =
        match value with
        | None -> writer.WriteNullObjectHeader()
        | Some state ->
            writer.WriteObjectHeader(2uy)
            writer.WriteValue(state.Count)
            writer.WriteValue(state.Version)

    override _.Deserialize(reader: byref<_>, value: byref<GrainState option>) =
        if not (reader.TryReadObjectHeader(out var count)) then
            value <- None
        else
            value <- Some { Count = reader.ReadValue<int>(); Version = reader.ReadValue<int>() }

// Register at startup
MemoryPackFormatterProvider.Register(GrainStateFormatter())
```

For DU unions, `DynamicUnionFormatter<'T>` can be used:
```csharp
// Must be C# (source generator limitation)
var formatter = new DynamicUnionFormatter<IGrainCommand>(
    (0, typeof(Increment>)),
    (1, typeof(Reset)))
MemoryPackFormatterProvider.Register(formatter)
```

**Assessment:** High effort for F# types — every DU needs a hand-written formatter. Not a general replacement. Suitable only for specific high-throughput grain state types where binary performance is critical (counters, rate limiters, caches).

**Verdict:** Selective use only. Not a default replacement for FSharp.SystemTextJson.

---

### protobuf-net + protobuf-net-fsharp
| Field | Value |
|---|---|
| NuGet | `protobuf-net` + (companion) `protobuf-net-fsharp` |
| Version | `protobuf-net` v3.x |
| Repo | https://github.com/protobuf-net/protobuf-net, https://github.com/mvkara/protobuf-net-fsharp |

**What it solves:** Protocol Buffer binary serialization with schema evolution (field numbers, forward/backward compatibility).

**F# DU support:** Native protobuf-net does not support F# DUs without manual model registration. The `protobuf-net-fsharp` companion adds:
- `registerUnionIntoModel` for DU registration
- Record type support
- `Option<'T>` field support

**When to consider:** For event payloads in `Orleans.FSharp.EventSourcing` where cross-language compatibility or strict schema versioning (proto files) is required. The schema evolution story is stronger than JSON-based approaches.

**Verdict:** Niche use — event store payloads needing wire-format stability across language boundaries. Not a general serialization upgrade.

---

## 6. Event Sourcing

### Marten 8 (already in use)
| Field | Value |
|---|---|
| NuGet | `Marten` |
| Version | **8.x** |
| Repo | https://github.com/JasperFx/marten |

**Status in project:** Already integrated in `Orleans.FSharp.EventSourcing` via `MartenConfig.fs` wrapper. The C# API (`session.Events.StartStream`, `session.Events.Append`, inline/async projections) works cleanly with F# records and DUs.

**Enhancement opportunity:** The current `MartenConfig.fs` is a configuration bridge. Consider adding a CE-based session helper:

```fsharp
// Proposed idiomatic F# wrapper
let appendEvents (session: IDocumentSession) streamId events = task {
    session.Events.Append(streamId, events |> Seq.map box |> Seq.toArray)
    do! session.SaveChangesAsync()
}
```

---

### Equinox
| Field | Value |
|---|---|
| NuGet | `Equinox`, `Equinox.Core`, `Equinox.MemoryStore`, `Equinox.EventStoreDb`, `Equinox.CosmosStore` |
| Version | **4.1.0** (updated 2025-06-12) |
| Repo | https://github.com/jet/equinox |
| Language | F# primary |
| License | Apache-2.0 |

**What it solves:** A pure-F# stream-level event sourcing library. The core abstraction is `Decider<'event, 'state>`:
- Handles optimistic concurrency (read → decide → write with version check)
- Integrates with EventStoreDB, CosmosDB, DynamoDB, SqlStreamStore, MemoryStore
- Cleanly expresses the fold pattern: `fold : 'state -> 'event -> 'state`

**Architecture fit with Orleans.FSharp.EventSourcing:**

| Layer | Orleans.FSharp | Equinox |
|---|---|---|
| Placement & activation | Orleans grain system | — |
| Event stream I/O | Marten / Orleans JournaledGrain | EventStoreDB / CosmosDB / MemoryStore |
| Optimistic concurrency | JournaledGrain built-in | `Decider` runner |
| State fold | `eventSourcedGrain {}` CE | `Fold.fold` function |
| Test backend | In-memory cluster | `Equinox.MemoryStore` |

**Interesting integration pattern:** Use Equinox's `Decider` as the *state machine layer* inside an `eventSourcedGrain {}` CE, with Orleans managing placement and Equinox managing the optimistic concurrency + fold against an EventStoreDB backend (complementing or replacing Marten for the event stream layer).

**Key benefit for testing:** `Equinox.MemoryStore` provides a fast, in-memory event store backend — ideal for unit testing event-sourced grains without PostgreSQL.

**Verdict:** Strong candidate for `Orleans.FSharp.EventSourcing` enrichment, especially for projects wanting EventStoreDB as an alternative to Marten/PostgreSQL.

---

## 7. Workflow / Saga Patterns

### Orleans.Sagas
| Field | Value |
|---|---|
| NuGet | `Orleans.Sagas` |
| Version | **0.0.45-pre** (Feb 2023) |

**Status:** Pre-release, unmaintained, targets older Orleans versions. Not compatible with Orleans 10 without significant porting work.

**Verdict:** Do not use.

---

### Idiomatic Orleans.FSharp saga pattern

The right approach for Orleans.FSharp is to model sagas as a grain with a DU state machine. The project already has all required primitives:

```fsharp
// Saga grain using existing Orleans.FSharp primitives
type SagaState =
    | Pending of OrderId
    | PaymentRequested of OrderId * PaymentRef
    | Shipped of OrderId * ShippingRef
    | Compensating of OrderId * reason: string
    | Completed of OrderId

grain {
    state (SagaState.Pending orderId)

    handle (fun (StartSaga cmd) state -> task {
        // Idiomatic compensation via DU state + reminders for timeout
        do! grainRef.RequestPayment(cmd.Amount)
        return { state with Status = PaymentRequested(cmd.OrderId, paymentRef) }, []
    })

    // Timeout compensation via reminder
    onReminder "saga-timeout" (fun state -> task {
        return { state with Status = Compensating(orderId, "timeout") }, [Compensate]
    })
}
```

**Verdict:** No external library needed — model sagas as DU state machines using existing `grain {}`, reminders, and timers.

---

## 8. Testing Utilities

### NBomber
| Field | Value |
|---|---|
| NuGet | `NBomber` |
| Version | **6.1.2** (Oct 2025) |
| Repo | https://github.com/PragmaticFlow/NBomber |
| License | Commercial (org use requires license from v5+) |

**What it solves:** Distributed load testing for .NET, with F# scenario API. Tests any pull/push system (HTTP, gRPC, WebSockets, databases).

**Fit:** Load testing Orleans clusters — grain throughput benchmarks, streaming throughput, silo scale-out behavior.

**Licensing caveat:** Free for personal/OSS use; organizational use requires a commercial license.

**Verdict:** Useful for performance characterization of Orleans.FSharp sample applications and benchmarks. License terms need evaluation.

---

### FSharp.Control.Reactive
| Field | Value |
|---|---|
| NuGet | `FSharp.Control.Reactive` |
| Version | **6.1.2** |
| Repo | https://github.com/fsprojects/FSharp.Control.Reactive |
| License | Apache-2.0 |

**What it solves:** F# wrapper for Rx.NET with `observe {}` and `rxquery {}` computation expressions. Provides idiomatic Observable combinators.

**Fit for Orleans.FSharp:** Potential complement to `FSharp.Control.TaskSeq` for Orleans streaming scenarios. Could bridge `IAsyncObservable` patterns with Orleans stream providers. However, the project already uses `FSharp.Control.TaskSeq` for async sequences, which covers most streaming needs.

**Verdict:** Situational — useful if reactive (hot, push-based) observable patterns are needed on top of Orleans streams.

---

### FSharp.Data.Adaptive
| Field | Value |
|---|---|
| NuGet | `FSharp.Data.Adaptive` |
| Version | **1.2.18** |
| Repo | https://github.com/fsprojects/FSharp.Data.Adaptive |
| License | MIT |

**What it solves:** Incremental/adaptive computation for F# — `aval<'T>` (adaptive values), `aset<'T>`, `alist<'T>`, `amap<K,V>`. Values recompute only when dependencies change.

**Fit for Orleans.FSharp:** Interesting but niche. Could model grain-derived read model projections as adaptive computations. Overlaps conceptually with Orleans streaming projections. Not a direct testing utility.

**Verdict:** Interesting for future exploration in read-side projection patterns but not an immediate priority.

---

## Prioritized Recommendations

| Priority | Library | NuGet Package | Why |
|---|---|---|---|
| 1 — High | **Validus** | `Validus` v4.1.4 | F#-native grain command validation, composable with FsToolkit |
| 2 — High | **Polly 8** | `Polly` v8.x | External service resilience from grains, native IcedTasks valueTask {} |
| 3 — High | **Serilog.Sinks.OpenTelemetry** | `Serilog.Sinks.OpenTelemetry` v4.2.0 | Connect existing Serilog to full OTel pipeline, zero API change |
| 4 — Medium | **Equinox** | `Equinox` + `Equinox.MemoryStore` v4.1.0 | Enrich EventSourcing with Decider pattern + fast test backend |
| 5 — Medium | **Hedgehog** | `Hedgehog` (fsharp-hedgehog) | Better shrinking for GrainHandlerStateMachineProperties and fold tests |
| 6 — Low | **MemoryPack** | `MemoryPack` v0.9.x | Selective use for hot-path grain state; requires custom F# formatters |
| 7 — Low | **protobuf-net-fsharp** | `protobuf-net` v3.x | Event payload schema stability if cross-language wire format needed |
| Skip | **FluentValidation** | — | C#-oriented, Validus is strictly better for this codebase |
| Skip | **Orleans.Sagas** | — | Pre-release, unmaintained, not compatible with Orleans 10 |
| Skip | **NBomber** | — | Useful but commercial license; evaluate separately |

---

## Key Integration Notes

### Polly + IcedTasks: the official pattern

The Polly 8 docs explicitly cover F# + IcedTasks integration. `pipeline.ExecuteAsync` accepts a `Func<CancellationToken, ValueTask<'T>>`, which maps to `fun ct -> valueTask { ... }`.

### Validus + FsToolkit: layered validation

```fsharp
// Layer 1: Validus validates the command shape
let validateCommand cmd =
    validate {
        let! amount = Check.Decimal.greaterThan 0m "Amount" cmd.Amount
        return { cmd with Amount = amount }
    }

// Layer 2: FsToolkit chains async business logic
let handleCommand cmd = taskResult {
    let! validCmd = validateCommand cmd |> Result.mapError DomainError.Validation
    let! grain = GrainRef.resolve validCmd.GrainId
    return! grain.Execute(validCmd)
}
```

### OTel: Orleans built-in meter

Orleans 10 emits metrics via `Meter("Microsoft.Orleans")`. The project's `Telemetry.fs` already exists — it should register this meter in the OTel pipeline and expose typed F# wrappers for custom grain metrics.

### Equinox.MemoryStore for tests

`Equinox.MemoryStore` provides a thread-safe in-memory event store that is ideal for `Orleans.FSharp.Integration` tests that don't need a real PostgreSQL/EventStoreDB instance.

---

*Research conducted: 2026-04-04. Sources: Context7 documentation, NuGet Gallery, GitHub repositories, web search.*
