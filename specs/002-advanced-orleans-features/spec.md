# Feature Specification: Advanced Orleans Features (v2)

**Feature Branch**: `002-advanced-orleans-features`
**Created**: 2026-04-02
**Status**: Draft
**Input**: "Reminders/Timers, Grain Observers, Reentrancy, Stateless Workers, Event Sourcing, F# Analyzer, dotnet template, .fsx scripting"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Reminders and Timers (Priority: P1)

An F# developer wants to schedule recurring work inside a grain using Orleans
reminders (persistent, survives grain deactivation) and timers (in-memory,
lost on deactivation). The developer registers reminders and timers through
the `grain { }` CE or module functions — not through Orleans base class methods
directly.

**Why this priority**: Reminders and timers are among the most commonly used
Orleans features after basic grain messaging. Many real-world grains need
periodic work (cleanup, polling, heartbeats).

**Independent Test**: A grain registers a reminder that fires every 5 seconds.
After grain deactivation and reactivation, the reminder still fires. A timer
is registered and verified to NOT survive deactivation.

**Acceptance Scenarios**:

1. **Given** a grain with a reminder registered via `onReminder` CE keyword,
   **When** the reminder period elapses, **Then** the handler fires and
   updates grain state.

2. **Given** a grain with a timer registered via `Timers.register`,
   **When** the timer interval elapses, **Then** the handler fires.

3. **Given** a grain with a reminder, **When** the grain deactivates and
   reactivates, **Then** the reminder resumes automatically.

4. **Given** a grain with a timer, **When** the grain deactivates and
   reactivates, **Then** the timer does NOT resume (expected behavior).

5. **Given** a developer, **When** they cancel a reminder or timer,
   **Then** it stops firing immediately.

---

### User Story 2 - Grain Observers (Priority: P2)

An F# developer wants to implement pub/sub between grains and external clients
using Orleans grain observers. The developer defines observer interfaces and
subscribes/unsubscribes through F#-idiomatic module functions, not through
raw Orleans observer factories.

**Why this priority**: Observers enable real-time notifications from grains to
clients — essential for dashboards, live updates, and event-driven architectures.

**Independent Test**: A client subscribes to a grain observer. When the grain
publishes a notification, the client receives it. When the client unsubscribes,
notifications stop.

**Acceptance Scenarios**:

1. **Given** a grain that publishes notifications, **When** a client subscribes
   via `Observer.subscribe`, **Then** the client receives all notifications.

2. **Given** a subscribed client, **When** the grain publishes 10 notifications,
   **Then** the client receives exactly 10 in order.

3. **Given** a subscribed client, **When** the client calls `Observer.unsubscribe`,
   **Then** no further notifications are received.

4. **Given** a subscribed client that disconnects, **When** the grain tries to
   notify, **Then** the grain handles the failure gracefully without crashing.

---

### User Story 3 - Reentrancy Support (Priority: P3)

An F# developer wants to mark grains as reentrant (allowing concurrent message
processing) or use interleaving on specific methods. This is configured through
the `grain { }` CE to avoid direct Orleans attribute usage.

**Why this priority**: Reentrancy is critical for performance in scenarios where
grains make outbound calls that result in callbacks. Without it, deadlocks occur.

**Independent Test**: A reentrant grain processes two messages concurrently
(verified via timing — both complete faster than sequential processing would).

**Acceptance Scenarios**:

1. **Given** a grain defined with `reentrant` keyword in the CE, **When** two
   messages arrive simultaneously, **Then** both are processed concurrently.

2. **Given** a non-reentrant grain (default), **When** two messages arrive,
   **Then** they are processed sequentially.

3. **Given** a grain with `interleave` on a specific handler, **When** that
   handler and another handler are called concurrently, **Then** only the
   interleaved handler runs concurrently.

---

### User Story 4 - Stateless Workers (Priority: P4)

An F# developer wants to define stateless worker grains that Orleans can
activate on every silo for load balancing. Defined via `statelessWorker { }` CE
or a keyword in the existing `grain { }` CE.

**Why this priority**: Stateless workers are Orleans' answer to horizontal
scaling for computation-heavy, stateless operations (validation, transformation,
routing).

**Independent Test**: A stateless worker grain is called 1000 times from a
client. Verify that activations exist on multiple silos (in a multi-silo test
cluster).

**Acceptance Scenarios**:

1. **Given** a grain defined with `statelessWorker` keyword, **When** it is
   activated, **Then** Orleans allows multiple activations on different silos.

2. **Given** a stateless worker, **When** it processes a message, **Then** it
   has no persistent state (state operations are not available).

3. **Given** a stateless worker with `maxActivations` set, **When** load
   increases, **Then** Orleans activates up to that many instances per silo.

---

### User Story 5 - Event Sourcing with Marten (Priority: P5)

An F# developer wants to define event-sourced grains where state is derived
from a sequence of events, persisted to PostgreSQL via Marten. A new
`eventSourcedGrain { }` CE defines event handlers (event -> state) and
command handlers (state -> command -> events). The event store is managed by
Marten; the grain never stores state directly.

**Why this priority**: Event sourcing is a natural fit for the actor model and
F# discriminated unions. Combined with Marten (PostgreSQL), it provides a
powerful, production-ready persistence model. However, it's a complex feature
that builds on all prior work.

**Independent Test**: An event-sourced grain processes commands, produces events,
rebuilds state from the event stream, and the event history is queryable via
Marten.

**Acceptance Scenarios**:

1. **Given** an event-sourced grain definition, **When** a command is received,
   **Then** events are produced and persisted to Marten.

2. **Given** a persisted event stream, **When** the grain reactivates, **Then**
   state is rebuilt by replaying all events in order.

3. **Given** an event-sourced grain, **When** queried for event history,
   **Then** all past events are returned in chronological order.

4. **Given** an arbitrary sequence of commands (FsCheck), **When** applied to
   the grain, **Then** the reconstructed state from events matches the expected
   state from the pure fold function.

---

### User Story 6 - F# Analyzer for async/task Enforcement (Priority: P6)

An F# developer wants a compile-time analyzer that warns when `async { }` is
used instead of `task { }` in the Orleans.FSharp project. This prevents
accidental async usage that violates the constitution.

**Why this priority**: A compile-time safety net is better than code review for
catching policy violations. But it's a tooling enhancement, not core functionality.

**Independent Test**: A test project with intentional `async { }` usage triggers
the analyzer warning. A project using only `task { }` produces no warnings.

**Acceptance Scenarios**:

1. **Given** F# code using `async { }` in a project referencing Orleans.FSharp,
   **When** the project compiles, **Then** the analyzer emits warning OF0001
   explaining to use `task { }` instead.

2. **Given** F# code using only `task { }`, **When** the project compiles,
   **Then** no analyzer warnings are produced.

3. **Given** the analyzer, **When** applied to test helper code marked with
   an opt-out attribute, **Then** the warning is suppressed.

---

### User Story 7 - dotnet new Template (Priority: P7)

An F# developer wants to scaffold a new Orleans.FSharp project using
`dotnet new orleans-fsharp` which generates the full solution structure
(Grains, Runtime, CodeGen, Tests) with a working counter grain example.

**Why this priority**: Templates dramatically reduce time-to-first-grain for
new users. But this is distribution/DX tooling, not library code.

**Independent Test**: Running `dotnet new orleans-fsharp -n MyApp` generates a
buildable solution that passes all tests out of the box.

**Acceptance Scenarios**:

1. **Given** the template is installed, **When** `dotnet new orleans-fsharp -n MyApp`
   is run, **Then** a complete solution is created with 4+ projects.

2. **Given** the generated solution, **When** `dotnet build` is run,
   **Then** it compiles with zero warnings.

3. **Given** the generated solution, **When** `dotnet test` is run,
   **Then** all template tests pass.

4. **Given** the generated solution, **When** the silo is started,
   **Then** the sample counter grain is accessible and functional.

---

### User Story 8 - Interactive .fsx Scripting (Priority: P8)

An F# developer wants to prototype grain definitions and test them interactively
in an F# script (.fsx) without setting up a full project. The library provides
a script-friendly API that starts an in-process silo and allows grain interaction
from the REPL.

**Why this priority**: Interactive exploration is one of F#'s strongest features.
Enabling it for Orleans grains makes the library more accessible for learning
and prototyping. Lowest priority because it requires careful API design.

**Independent Test**: An .fsx script references Orleans.FSharp, defines a grain,
starts a silo, calls the grain, and prints the result — all in ~20 lines.

**Acceptance Scenarios**:

1. **Given** an .fsx script with `#r "nuget: Orleans.FSharp"`, **When** the
   script defines a grain and starts a silo, **Then** the grain responds
   to messages.

2. **Given** the scripting API, **When** the developer calls
   `Scripting.quickStart()`, **Then** an in-process silo starts with sensible
   defaults (localhost, memory storage).

3. *(Stretch goal)* **Given** a running script silo, **When** the developer
   modifies a grain definition and re-evaluates, **Then** the new definition
   is picked up without restarting the silo. Note: Orleans registers grain
   types at silo startup; true hot-swap may not be feasible. Handler logic
   changes (via mutable closures) are the realistic scope.

---

### Edge Cases

- What happens when a reminder handler throws? The library MUST log the error
  and continue — the reminder should not be unregistered automatically.
- What happens when an observer client disconnects without unsubscribing?
  The library MUST detect the broken subscription and clean it up.
- What happens when a reentrant grain handler modifies shared state concurrently?
  The library MUST document that reentrancy requires the developer to manage
  state consistency (or provide immutable state patterns).
- What happens when the Marten PostgreSQL connection fails during event sourcing?
  The grain MUST enter a failed state and refuse commands until storage recovers.
- What happens when `dotnet new orleans-fsharp` is run with an existing directory?
  The template MUST refuse with a clear error (not overwrite).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The library MUST provide `onReminder` CE keyword and `Timers`
  module for scheduling periodic grain work.
- **FR-002**: Reminders MUST survive grain deactivation; timers MUST NOT.
- **FR-003**: The library MUST provide `Observer` module with `subscribe`,
  `unsubscribe`, and `notify` functions for grain-to-client pub/sub.
- **FR-004**: The library MUST provide `reentrant` and `interleave` keywords
  in the grain CE for concurrency control.
- **FR-005**: The library MUST provide `statelessWorker` keyword or CE for
  defining stateless worker grains with optional `maxActivations`.
- **FR-006**: The library MUST provide `eventSourcedGrain { }` CE with
  `apply` (event -> state) and `handle` (state -> command -> events) operations.
- **FR-007**: Event sourcing MUST integrate with Marten for PostgreSQL-backed
  event persistence.
- **FR-008**: The library MUST provide an F# analyzer that warns on `async { }`
  usage in Orleans.FSharp projects.
- **FR-009**: The library MUST provide a `dotnet new` template that scaffolds
  a complete Orleans.FSharp solution.
- **FR-010**: The library MUST provide scripting support via .fsx with a
  `Scripting.quickStart()` function for interactive exploration.
- **FR-011**: All new asynchronous APIs MUST return `Task<'T>`, never `Async<'T>`.
- **FR-012**: All new public types MUST have XML documentation.

### Key Entities

- **Reminder**: A persistent, periodic trigger registered with a grain.
  Survives deactivation. Backed by Orleans reminder infrastructure.
- **Timer**: An in-memory, periodic trigger. Lost on deactivation. Lighter
  weight than reminders.
- **Observer**: A client-side subscription to grain notifications. Uses
  Orleans observer pattern with F# type-safe wrappers.
- **Event**: An immutable fact (DU case) that represents something that happened.
  Persisted to event store. State is derived from event sequence.
- **Event-Sourced Grain**: A grain whose state is rebuilt from events, not
  stored directly. Commands produce events; events update state.
- **Analyzer Diagnostic**: A compile-time warning (OF0001) emitted when
  `async { }` is detected in Orleans.FSharp code.
- **Template**: A `dotnet new` template package that scaffolds a project.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer can register a reminder and see it fire after grain
  deactivation/reactivation within 5 minutes of reading the docs.
- **SC-002**: Event-sourced grains have 100% property test coverage for the
  "fold events = apply commands" invariant via FsCheck.
- **SC-003**: The F# analyzer catches 100% of `async { }` usages in scanned
  code (verified by test suite with known positives).
- **SC-004**: `dotnet new orleans-fsharp` generates a solution that builds and
  tests in under 30 seconds on a standard development machine.
- **SC-005**: All new features compile with zero warnings under
  TreatWarningsAsErrors.
- **SC-006**: The .fsx scripting quickstart works in under 20 lines of code.

## Assumptions

- Marten is available as a NuGet dependency (Tier 1 per constitution research).
  PostgreSQL is required only for event sourcing features — other features
  work without it.
- The F# analyzer targets the FSharp.Analyzers.SDK, which is the community
  standard for F# analyzers (not Roslyn analyzers).
- The `dotnet new` template is distributed as a NuGet template package, not
  as a standalone installer.
- .fsx scripting requires .NET 10 SDK and NuGet package references (`#r`).
  It does not support paket or other package managers.
- Reentrancy and stateless workers map directly to Orleans attributes
  (`[<Reentrant>]`, `[<StatelessWorker>]`) which the CodeGen C# bridge
  applies to generated grain classes.
