# Feature Specification: F# Idiomatic API Layer for Orleans

**Feature Branch**: `001-fsharp-orleans-api`
**Created**: 2026-04-02
**Status**: Draft
**Input**: User description: "F# API over Orleans core with CE, DU state machines, type-safe refs, streaming, logging, testing toolkit"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Define and Run a Grain via Computation Expression (Priority: P1)

An F# developer wants to define a grain's behavior using a `grain { }` computation
expression instead of inheriting from an Orleans `Grain` base class. The developer
describes message handlers, state transitions, and lifecycle hooks declaratively.
The grain is hosted in an Orleans silo and invoked through a type-safe reference,
all without writing any C# code or referencing Orleans types directly.

**Why this priority**: This is the foundational interaction — if a developer cannot
define and invoke a grain idiomatically, the entire library has no value. Every
other story builds on this capability.

**Independent Test**: A developer creates a single-grain "counter" that increments,
decrements, and returns its current value. The grain runs in a test silo and
responds correctly to all three operations.

**Acceptance Scenarios**:

1. **Given** an empty F# project with the library referenced, **When** the developer
   writes a grain definition using `grain { }` CE with three message handlers,
   **Then** the project compiles without referencing any Orleans namespace directly.

2. **Given** a compiled grain definition, **When** the developer starts a test silo
   and sends an Increment message followed by a GetValue query, **Then** the grain
   returns 1.

3. **Given** a grain with state, **When** the silo deactivates and reactivates the
   grain, **Then** the grain's persisted state is restored correctly.

4. **Given** a grain CE definition, **When** the developer omits a required handler,
   **Then** the compiler produces a clear error indicating what is missing.

---

### User Story 2 - Model Grain State as Discriminated Union (Priority: P2)

An F# developer wants to represent grain state as a discriminated union to leverage
exhaustive pattern matching for state transitions. The developer defines states
(e.g., `Idle | Processing of Request | Completed of Result | Failed of Error`) and
the API ensures that messages are dispatched based on current state, invalid
transitions are caught at compile time where possible, and state is serialized
correctly for persistence.

**Why this priority**: DU-based state machines are the primary reason to use F# over
C# for Orleans. Without this, the library is just syntax sugar.

**Independent Test**: A developer defines an order-processing grain with 4 states
and 5 transitions. FsCheck property tests verify that all valid command sequences
produce valid states and all invalid transitions are rejected.

**Acceptance Scenarios**:

1. **Given** a grain with DU state `Idle | Active of int | Done`, **When** the
   developer sends a Start command while in Idle state, **Then** the grain
   transitions to `Active 0`.

2. **Given** a grain in `Active` state, **When** the developer sends a Complete
   command, **Then** the grain transitions to `Done` and the state persists.

3. **Given** a grain in `Done` state, **When** the developer sends a Start command,
   **Then** the grain returns an error result without changing state.

4. **Given** an arbitrary sequence of valid commands (FsCheck generated), **When**
   applied to a grain starting from `Idle`, **Then** the grain is always in a
   valid state defined by the DU.

---

### User Story 3 - Type-Safe Grain References (Priority: P3)

An F# developer wants to obtain a reference to a grain and call its methods with
full type safety and IDE autocompletion. The developer should never need to
cast, use string-based identifiers without type context, or call untyped
`IGrainFactory` methods directly.

**Why this priority**: Without type-safe references, grain-to-grain communication
is error-prone and the developer experience degrades to "C# with worse syntax."

**Independent Test**: A developer writes two grains that communicate — grain A
calls grain B. Both references are fully typed and the interaction compiles and
runs without any string-based lookups or casts.

**Acceptance Scenarios**:

1. **Given** two grain definitions (Producer and Consumer), **When** the developer
   obtains a reference to Consumer from within Producer, **Then** the reference
   exposes only the methods defined on Consumer's grain interface.

2. **Given** a grain reference, **When** the developer passes an incorrect message
   type, **Then** the compiler rejects the call.

3. **Given** a grain identifier, **When** the developer requests a reference with
   the wrong grain type, **Then** the system produces a compile-time error.

---

### User Story 4 - Silo Configuration DSL (Priority: P4)

An F# developer wants to configure an Orleans silo (clustering, persistence,
logging) using an F# builder pattern instead of C# extension method chains.
The configuration should be composable, discoverable, and produce clear error
messages for invalid configurations.

**Why this priority**: Configuration is the first thing a developer touches when
starting a project. A bad configuration experience creates an immediate negative
impression of the library.

**Independent Test**: A developer configures and starts a local development silo
with in-memory storage and localhost clustering using the F# builder. The silo
starts and accepts grain calls.

**Acceptance Scenarios**:

1. **Given** the F# configuration DSL, **When** the developer writes a minimal
   silo configuration, **Then** the silo starts with sensible defaults for local
   development.

2. **Given** a silo configuration, **When** the developer adds persistence with
   an invalid provider name, **Then** the system reports the error at startup
   with a human-readable message.

3. **Given** multiple configuration fragments, **When** the developer composes
   them, **Then** later fragments override earlier ones predictably.

---

### User Story 5 - Orleans Streaming via TaskSeq (Priority: P5)

An F# developer wants to produce and consume Orleans stream events using
`taskSeq { }` and standard F# sequence operations (map, filter, iter). The
streaming API should feel like working with any other F# sequence, not like
managing Orleans subscription handles.

**Why this priority**: Streaming is a core Orleans capability and many real-world
use cases depend on it. However, it builds on grains (P1) and references (P3),
making it a later priority.

**Independent Test**: A developer creates a producer grain that emits 100 events
and a consumer that filters and counts them. The test verifies the consumer
receives exactly the expected count.

**Acceptance Scenarios**:

1. **Given** a stream producer grain, **When** it emits events using the F# API,
   **Then** consumers receive all events in order.

2. **Given** a stream consumer, **When** the developer applies `TaskSeq.filter`
   and `TaskSeq.map` to the stream, **Then** only matching events are processed.

3. **Given** a stream subscription, **When** the consumer grain deactivates and
   reactivates, **Then** the subscription resumes from where it left off.

---

### User Story 6 - Structured Logging with Correlation (Priority: P6)

An F# developer wants grain operations to automatically produce structured log
events with correlation IDs that trace a request across multiple grain calls.
The developer should be able to add custom log entries that inherit the
correlation context without manual plumbing.

**Why this priority**: Observability is mandatory per the constitution. However,
it is cross-cutting and can be layered on after the core grain mechanics work.

**Independent Test**: A developer triggers a chain of 3 grain calls. The test
captures log output and verifies all entries share the same correlation ID and
contain expected structured fields.

**Acceptance Scenarios**:

1. **Given** a grain that processes a message, **When** the message is handled,
   **Then** activation, processing, and deactivation each produce a structured
   log entry with grain type and ID.

2. **Given** grain A calling grain B calling grain C, **When** A initiates a
   request, **Then** all log entries across A, B, and C share the same
   correlation ID.

3. **Given** a developer adding a custom log line inside a grain handler,
   **When** the log is written, **Then** it automatically includes the grain
   context (type, ID, correlation) without the developer specifying them.

---

### User Story 7 - Grain Testing Toolkit with FsCheck (Priority: P7)

An F# developer wants to test grains without starting a full Orleans silo. The
testing toolkit provides in-process grain activation, message dispatch, and state
inspection. FsCheck generators are provided for common grain patterns (command
sequences, state machines) so the developer can write property-based tests with
minimal boilerplate.

**Why this priority**: Testing is mandatory per the constitution, but the toolkit
is a developer experience enhancement that builds on all previous stories.

**Independent Test**: A developer writes a property test that generates random
command sequences, applies them to a grain, and asserts that the grain's state
invariant always holds. The test runs without a silo.

**Acceptance Scenarios**:

1. **Given** the testing toolkit, **When** a developer instantiates a grain in a
   test, **Then** the grain processes messages without requiring network or silo.

2. **Given** an FsCheck generator for command sequences, **When** 1000 random
   sequences are applied, **Then** the grain never enters an invalid state.

3. **Given** a grain with persistence, **When** the test simulates deactivation
   and reactivation, **Then** state is preserved using an in-memory test store.

4. **Given** a grain test, **When** the test inspects emitted log entries,
   **Then** all structured fields are accessible for assertion.

---

### Edge Cases

- What happens when a grain CE defines no message handlers? The library MUST
  produce a compile-time error, not a runtime failure.
- What happens when DU state serialization encounters an unknown case (e.g.,
  after a schema evolution)? The library MUST surface a clear deserialization
  error with the state type and raw payload.
- What happens when a stream producer emits faster than a consumer can process?
  The library MUST support backpressure or documented buffering semantics.
- What happens when the developer targets a .NET version below .NET 8? The
  library MUST fail at package restore with a clear minimum version message.
- What happens when two grain definitions use the same grain interface key?
  The library MUST detect the conflict at silo startup, not at runtime dispatch.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The library MUST provide a `grain { }` computation expression that
  defines grain behavior without inheriting from Orleans base classes directly.
- **FR-002**: The library MUST support discriminated union types as grain state
  with automatic serialization via System.Text.Json + FSharp.SystemTextJson.
- **FR-003**: The library MUST provide type-safe grain references that prevent
  sending incorrect message types at compile time.
- **FR-004**: The library MUST provide an F# builder/DSL for Orleans silo and
  client configuration.
- **FR-005**: The library MUST wrap Orleans streaming with TaskSeq-compatible
  producers and consumers.
- **FR-006**: The library MUST automatically attach structured log entries with
  correlation IDs to all grain lifecycle and message handling operations.
- **FR-007**: The library MUST provide a test harness that activates grains
  without a full Orleans silo for unit and property-based testing.
- **FR-008**: The library MUST provide FsCheck generators and Arbitrary instances
  for common grain testing patterns (command sequences, state assertions).
- **FR-009**: All asynchronous operations in the public API MUST return
  `Task<'T>` or `ValueTask<'T>`, never `Async<'T>`.
- **FR-010**: Error messages from the library MUST reference F# types and
  concepts, not Orleans internals.
- **FR-011**: The library MUST produce XML documentation for all public types
  and computation expression builders.
- **FR-012**: The library MUST support grain persistence with pluggable storage
  providers (in-memory for development, ADO.NET/Azure for production).

### Key Entities

- **Grain Definition**: A declarative description of a grain's message handlers,
  state type, lifecycle hooks, and persistence configuration. Created via CE.
- **Grain State (DU)**: A discriminated union representing all possible states
  of a grain. Serializable, versionable, exhaustively matchable.
- **Grain Reference**: A typed handle to a remote grain. Exposes only the
  operations defined by the grain definition. Obtained via factory functions.
- **Stream**: A typed, ordered sequence of events produced by one grain and
  consumed by others. Wrapped as TaskSeq for F# ergonomics.
- **Silo Configuration**: A composable description of clustering, persistence,
  and logging settings. Built via F# builder pattern.
- **Test Harness**: An in-process grain host for testing. Provides state
  inspection, log capture, and FsCheck integration.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer familiar with F# and the actor model can define a
  grain, test it, and run it locally within 15 minutes using only the library
  API and documentation.
- **SC-002**: All state machine transitions for grain DU states are covered by
  FsCheck property tests with 100% case coverage.
- **SC-003**: The library introduces less than 5% overhead on grain call latency
  compared to equivalent direct Orleans C# code.
- **SC-004**: Every grain operation produces at least one structured log entry
  with correlation ID, grain type, and grain ID.
- **SC-005**: The library compiles with zero warnings under TreatWarningsAsErrors
  on .NET 8+.
- **SC-006**: The public API surface has 100% XML documentation coverage.
- **SC-007**: A developer can write and run a grain property test without
  starting a network-capable silo, completing the test cycle in under 5 seconds.

## Assumptions

- Target users are F# developers with basic familiarity with the actor model
  but not necessarily prior Orleans experience.
- The minimum target platform is .NET 8 (LTS). .NET 9+ is supported but not
  required for any feature.
- Orleans 8.x is the minimum supported runtime version. The library tracks
  Orleans major versions but does not guarantee backward compatibility with
  Orleans 7.x or earlier.
- The library is distributed as NuGet packages. No custom tooling, IDE plugins,
  or dotnet CLI extensions are required.
- In-memory clustering and storage are sufficient for development and testing.
  Production clustering (Azure, Redis, etc.) is configured by the user through
  the configuration DSL but implemented by Orleans providers, not this library.
- The library does not replace Orleans — it wraps it. All Orleans-native features
  (dashboards, monitoring, provider ecosystem) remain accessible to advanced users
  who choose to "drop down" to the Orleans layer.
