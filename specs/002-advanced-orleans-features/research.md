# Research: Advanced Orleans Features (v2)

**Date**: 2026-04-03
**Branch**: `002-advanced-orleans-features`

## R-001: Reminders API

**Decision**: Wrap `IRemindable.ReceiveReminder` + `RegisterOrUpdateReminder` via
`onReminder` CE keyword and `Reminder` module functions.

**Rationale**: Reminders require grains to implement `IRemindable` interface and
override `ReceiveReminder(name, status)`. The F# CE can register a reminder handler
and the CodeGen C# grain class will implement `IRemindable`, delegating to the handler.

**Key constraints**:
- Minimum period: 1 minute (Orleans enforced)
- Reminder handles are NOT valid across activations — must re-fetch via `GetReminder`
- Requires reminder storage provider (memory for dev, ADO.NET/Azure for prod)

**API shape**:
```fsharp
// In grain CE:
grain {
    defaultState initialState
    handle (fun s m -> ...)
    onReminder "cleanupReminder" (fun state reminderName status -> task {
        return newState
    })
}

// Module functions:
Reminder.register : grainContext -> name:string -> dueTime:TimeSpan -> period:TimeSpan -> Task<unit>
Reminder.unregister : grainContext -> name:string -> Task<unit>
```

---

## R-002: Timers API

**Decision**: Provide `Timers` module with `register`, `change`, `dispose` functions.
NOT a CE keyword because timers are runtime-managed, not grain-definition-level.

**Rationale**: Timers use `Grain.RegisterGrainTimer` which returns `IGrainTimer` (IDisposable).
They need runtime grain context. CE is for declarative definition; timers are imperative.

**Key API**:
```fsharp
module Timers =
    val register : grain:IGrainBase -> callback:(CancellationToken -> Task<unit>)
                   -> dueTime:TimeSpan -> period:TimeSpan -> IGrainTimer
    val registerWithState : grain:IGrainBase -> callback:('TState -> CancellationToken -> Task<unit>)
                   -> state:'TState -> dueTime:TimeSpan -> period:TimeSpan -> IGrainTimer
```

---

## R-003: Grain Observers

**Decision**: Provide `Observer` module wrapping `IGrainObserver` + `ObserverManager<T>`.
F# developers define observer interfaces and use module functions for subscribe/notify.

**Rationale**: Observer pattern in Orleans uses `CreateObjectReference` to turn local
objects into grain references. `ObserverManager` handles subscription cleanup. Both
need F#-friendly wrappers.

**Key constraint**: Must call `DeleteObjectReference` to prevent memory leaks.
The F# API should handle cleanup automatically via IDisposable pattern.

**API shape**:
```fsharp
module Observer =
    val createRef<'T when 'T :> IGrainObserver> : factory:IGrainFactory -> observer:'T -> 'T
    val deleteRef<'T when 'T :> IGrainObserver> : factory:IGrainFactory -> observerRef:'T -> unit
    val subscribe<'T when 'T :> IGrainObserver> : factory:IGrainFactory -> observer:'T -> IDisposable
```

---

## R-004: Reentrancy

**Decision**: Add `reentrant` and `interleave` CE keywords that set flags on
GrainDefinition. The C# CodeGen grain class applies `[Reentrant]` or
`[AlwaysInterleave]` based on these flags.

**Rationale**: Reentrancy is a grain-level setting (class attribute). The CE
keyword sets a flag; the CodeGen bridge applies the actual C# attribute.

**Impact**: Requires updating the CodeGen C# grain class template to conditionally
add `[Reentrant]` attribute. Per-method `[AlwaysInterleave]` maps to specific
handler methods on the interface.

---

## R-005: Stateless Workers

**Decision**: Add `statelessWorker` keyword to grain CE (or separate
`statelessWorkerGrain { }` CE). Sets a flag + optional `maxActivations` parameter.
CodeGen applies `[StatelessWorker(n)]`.

**Rationale**: Stateless workers have no persistent state — the CE should enforce
this (no `persist` keyword allowed). `maxActivations` defaults to CPU core count.

---

## R-006: Event Sourcing

**Decision**: Provide `eventSourcedGrain { }` CE that wraps `JournaledGrain<TState, TEvent>`.
Orleans 10 FULLY supports event sourcing via JournaledGrain — it's not removed.

**Rationale**: JournaledGrain uses `RaiseEvent` + `ConfirmEvents` pattern with
state auto-updated via `Apply` methods. The F# CE can define apply functions
(event -> state -> state) and command handlers (state -> command -> events).

**Key API**:
```fsharp
let orderGrain = eventSourcedGrain {
    defaultState OrderState.Empty
    apply (fun state event ->
        match event with
        | OrderPlaced o -> { state with Order = Some o }
        | OrderShipped -> { state with Shipped = true })
    handle (fun state cmd ->
        match cmd with
        | PlaceOrder o -> [OrderPlaced o]
        | ShipOrder -> [OrderShipped])
}
```

**Marten integration**: Use Interflare.Orleans.Marten for PostgreSQL-backed
storage OR Orleans built-in LogStorage/StateStorage for simpler scenarios.
Marten is optional — not required for core event sourcing.

---

## R-007: F# Analyzer

**Decision**: Use FSharp.Analyzers.SDK to build a custom analyzer that detects
`async { }` usage. Distributed as a separate NuGet package
`Orleans.FSharp.Analyzers`.

**Rationale**: FSharp.Analyzers.SDK is the community standard. It runs via
`dotnet fsharp-analyzers` CLI or IDE integration. The analyzer scans the
AST for `SynExpr.ComputationExpr` with `async` builder.

---

## R-008: dotnet new Template

**Decision**: Create a template package `Orleans.FSharp.Templates` distributed
via NuGet. Template ID: `orleans-fsharp`. Generates 4 projects (Grains, Silo,
CodeGen, Tests) with a working counter grain.

**Rationale**: Standard .NET template authoring via `template.json` in
`.template.config/` directory. Published as NuGet package.
