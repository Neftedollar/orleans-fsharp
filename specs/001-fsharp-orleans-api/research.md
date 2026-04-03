# Research: F# Idiomatic API Layer for Orleans

**Date**: 2026-04-02
**Branch**: `001-fsharp-orleans-api`

## R-001: Orleans 8.x Grain Definition Model

**Decision**: Wrap Orleans grain base classes via F# computation expression that
emits a class inheriting `Grain` / implementing `IGrainWithXKey`.

**Rationale**: Orleans discovers grains via concrete classes + interfaces at silo
startup. There is no dynamic/runtime grain registration API. The F# CE must
produce a type the Orleans runtime can discover.

**Alternatives considered**:
- Source generator approach (F# has limited source gen support — rejected)
- Type provider approach (too fragile, poor IDE support — rejected)
- Runtime IL emit (too complex, breaks AOT — rejected)

**Key constraint**: Orleans `[GenerateSerializer]` uses Roslyn source generators
which **do not work in F# projects**. Workaround: a thin C# "CodeGen" project
that references F# types and triggers Orleans code generation.

**Sources**: DejanMilicic/OrleansFsharp, Orleans docs

---

## R-002: Orleans Serialization + F# Types

**Decision**: Use `[<GenerateSerializer>]` + `[<Id(n)>]` on F# records and DUs.
Add FSharp.SystemTextJson as a fallback converter for cases where Orleans codegen
doesn't handle F# unions natively.

**Rationale**: Orleans 8 uses its own serialization with `[GenerateSerializer]`.
F# records work well. F# DUs with data require extra care — the C# codegen
project must reference F# assemblies to generate serializers for DU types.

**Alternatives considered**:
- MessagePack (requires separate attribute set — double annotation burden)
- Newtonsoft.Json (legacy, slower, no codegen — rejected per constitution)
- Pure System.Text.Json without Orleans codegen (loses Orleans optimizations)

---

## R-003: Grain State Persistence Pattern

**Decision**: Wrap `IPersistentState<T>` behind an F# module that works with
immutable DU state. The wrapper provides `readState`, `writeState`, `clearState`
functions that operate on the DU and handle the mutable `IPersistentState.State`
property internally.

**Rationale**: `IPersistentState<T>` is injected via `[PersistentState]` attribute
on constructor parameters. F# can use this via primary constructors. The `.State`
property is mutable (set to trigger Orleans dirty tracking), so the wrapper
must bridge immutable F# state to Orleans' mutable model.

**Key API**:
```fsharp
// Orleans C# pattern:
//   _state.State = newState; await _state.WriteStateAsync()
// F# wrapper:
//   do! GrainState.write grainState newDuValue
```

---

## R-004: Grain Reference Type Safety

**Decision**: Provide generic `GrainRef<'TInterface>` wrapper that constrains to
grain interfaces and wraps `IGrainFactory.GetGrain<T>()`. Use F# SRTPs or
interface constraints to enforce compile-time safety.

**Rationale**: Direct `IGrainFactory.GetGrain<T>(key)` is already generic, but
the key type (string/Guid/int) depends on which `IGrainWithXKey` the interface
inherits. The wrapper can encode this relationship in the type system.

**Alternatives considered**:
- Phantom types for grain IDs (elegant but confusing for newcomers)
- FSharp.UMX for typed IDs (Tier 3 — evaluate later if needed)

---

## R-005: Streaming API Wrapping

**Decision**: Wrap `IAsyncStream<T>` subscription as `IAsyncEnumerable<T>` →
`TaskSeq<T>`. Provide `Stream.subscribe` and `Stream.publish` functions that
handle `StreamSubscriptionHandle` lifecycle internally.

**Rationale**: Orleans streams use callback-based subscription
(`SubscribeAsync(onNextAsync, onErrorAsync)`). F# developers expect
pull-based iteration (`taskSeq { for item in stream do ... }`). The bridge
converts push to pull via `Channel<T>` internally.

**Key constraint**: Subscription persistence across grain deactivation requires
storing `StreamSubscriptionHandle` — this maps to grain state.

---

## R-006: Silo Configuration

**Decision**: Provide `siloBuilder { }` CE that wraps `ISiloBuilder` calls.
Offer preset configurations for common scenarios (localhost-dev, Azure, etc.)

**Rationale**: Orleans silo configuration uses C# extension method chains on
`ISiloBuilder`. An F# CE builder calls these methods internally, providing
type-safe configuration with IntelliSense on the CE members.

**Key API shape**:
```fsharp
let silo = siloBuilder {
    useLocalhostClustering()
    addMemoryGrainStorage "Default"
    addMemoryGrainStorage "PubSubStore"
    addMemoryStreams "MemoryStreams"
    useSerilog()
}
```

---

## R-007: Testing Strategy

**Decision**: Two-tier testing:
1. **Unit tests**: Pure F# functions (state transitions) + OrleansTestKit for
   isolated grain testing without silo
2. **Integration tests**: `InProcessTestClusterBuilder` with shared xUnit fixture

**Rationale**: `InProcessTestCluster` is the recommended Orleans 8 test API.
It starts fast (seconds) and supports shared fixtures across tests. For pure
state machine logic, no silo is needed — test DU transitions as plain functions.

**FsCheck integration**: Use `[<Property>]` attribute from FsCheck.Xunit.
Generate random command sequences, apply to state machine model, verify invariants.
Shrunk counterexamples become regression unit tests.

**Key pattern**:
```fsharp
// Model-based: test pure state transitions
[<Property>]
let ``state machine invariant`` (ops: Command list) =
    let finalState = ops |> List.fold applyCommand initialState
    isValidState finalState

// Integration: test through silo
[<Fact>]
let ``grain round-trip`` () = task {
    let grain = fixture.Cluster.Client.GetGrain<ICounterGrain>(0L)
    do! grain.Increment()
    let! value = grain.GetValue()
    test <@ value = 1 @>
}
```

---

## R-008: C# CodeGen Project Requirement

**Decision**: Include a minimal C# project (`Orleans.FSharp.CodeGen`) that
references F# assemblies and contains `[assembly: GenerateCodeForDeclaringAssembly]`
to trigger Orleans source generators for F# types.

**Rationale**: Orleans' Roslyn-based source generators cannot run on F# projects.
The established workaround (DejanMilicic/OrleansFsharp, official Orleans samples)
is a C# "bridge" project that exists solely for code generation.

**Alternatives considered**:
- Manual serializer registration (tedious, error-prone — rejected)
- Runtime reflection serializer (slow, defeats Orleans optimization — rejected)

**Impact on project structure**: Adds one C# project to the solution. This is
the ONLY C# code in the project. The C# project contains no logic — only
assembly attributes and type references.
