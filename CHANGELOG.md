# Changelog

## [Unreleased]

### Changed

- **BREAKING — `grainContract`, `contract` and `observerContract` are type functions; drop the
  `()`.** The three computation-expression entry points are now explicit generic *values* of
  their builder type rather than functions of `unit`, so a contract expression reads
  `grainContract<RoomActor, RoomId, RoomApi> { ... }` and `observerContract<RoomObserver,
  RoomObserverApi> { ... }` — the type arguments are followed by the braces directly. Migration is
  one mechanical edit: delete the `()` between the type arguments and the `{`; nothing else about
  the surface, the sealed contract, or the wire changes. F# re-evaluates a type function at every
  mention, so each expression still opens on its own builder.
  **This is a source-breaking change shipped as a non-major version by explicit owner decision.**
  It lands one day after 4.0.0 (2026-08-19) and the same day as 4.0.1, while adoption of the
  functional runtime is still nil, and the owner would rather pay the syntax cost now than pin the
  parenthesis into the API for a major cycle. It is source-breaking only: the compiled IL is
  unchanged (both forms emit the same zero-argument generic static method), so an assembly built
  against 4.0.x keeps running against this version without recompiling — it is F# *source* that
  must drop the parens.

## [4.0.1] - 2026-08-20

A correctness release driven by the post-4.0.0 documentation verification pass: the
resilience deadline now actually fires (`TimeoutRejectedException`, with the in-flight call
abandoned — not cancelled — and new `withTimeoutCancellable` / `executeCancellable` for
operations that can honour a token), and the classic `Stream` module gained a real cursor
(`subscribeWithToken`; `getSequenceToken` deprecated). Details below.

### Added

- **`GrainResilience.executeCancellable` / `GrainResilience.withTimeoutCancellable`.** The same
  pipelines with a `CancellationToken -> Task<'T>` operation instead of `unit -> Task<'T>`: the
  deadline's token reaches the call, so an operation that can honour it stops rather than being
  abandoned. An `OperationCanceledException` raised in response still surfaces as
  `TimeoutRejectedException`, so both entry points fail the same way. Link a caller's own token
  inside the operation with `CancellationTokenSource.CreateLinkedTokenSource` — a cancel through
  that path stays an `OperationCanceledException` and is not rebranded as a timeout.
- **`Stream.subscribeWithToken` and `Stream.subscribeFromWithToken`.** The classic `Stream`
  module could not produce the cursor its own `subscribeFrom` requires: `subscribe` and
  `subscribeFrom` discarded the `StreamSequenceToken` their Orleans callback receives. Both new
  entry points take `'T -> StreamSequenceToken option -> Task<unit>` and hand the handler the
  cursor of the event it is processing — `Some` on a rewindable provider, `None` on one that
  supplies no cursor, matching how `context.streamSequenceToken` already reports it on the
  functional grain runtime. `subscribeFromWithToken` is the rewind that keeps checkpointing.
  Two behaviours are now documented from measurement rather than assumed: the rewind is
  **inclusive** of the checkpointed event, and a resumed subscription's backlog is delivered on
  the next delivery cycle for the stream (an idle one received nothing for 30 s; one further
  publish flushed the whole backlog).

### Deprecated

- **`Stream.getSequenceToken`** now carries `[<Obsolete>]` (a warning, not an error) and still
  returns `None`, so existing callers keep compiling and behaving identically. It was never a
  lookup — `StreamSubscriptionHandle` exposes no cursor — so the replacement is
  `Stream.subscribeWithToken` / `subscribeFromWithToken`, or `context.streamSequenceToken` inside
  an `onStream` hook.

### Fixed

- **A `GrainResilience` timeout now actually fires.** `execute` handed Polly an already-started
  `Task`, and Polly's timeout strategy can only await such a task: a 100 ms budget over an 800 ms
  call returned the result after ~810 ms and raised nothing. The in-flight task is now awaited
  under the pipeline's own cancellation token, so the deadline raises `TimeoutRejectedException`
  for the caller — ~101 ms for that same call. The deadline **abandons** the call rather than
  cancelling it: a `unit -> Task<'T>` takes no token, so the work runs to completion with its
  effects intact and only the result is discarded. An abandoned task that faults afterwards would
  raise `TaskScheduler.UnobservedTaskException`, so the abandoned task now carries a fault
  observer.
  Retry was never affected: each attempt re-invokes the operation (three invocations for
  `MaxRetryAttempts = 2`), now pinned by a test.
- **Two documentation claims about `GrainResilience` corrected.** `ResilienceOptions.Timeout` was
  described as a "per-attempt timeout" while `buildPipeline` adds it outermost: it is a deadline
  over the whole sequence — every attempt plus the delays between them — and one hung attempt
  therefore consumes the entire budget. For a per-attempt deadline, nest `withTimeout` inside the
  function passed to `retry`. Separately, `execute` builds a fresh pipeline on every call, so a
  circuit breaker configured through `ResilienceOptions` holds no state across calls and cannot
  trip; `buildPipeline` (built once and reused) or `circuitBreaker` is the shared-state path.
  Both facts are now pinned by tests as well as documented.
- **Two flaky wall-clock timeout tests replaced** (spec-003 backlog item 11). They asserted that a
  50 ms Polly budget was met, and drove a hand-built Polly pipeline rather than
  `GrainResilience` — so they passed while `withTimeout` could not time anything out. The
  replacements gate the protected call on a `TaskCompletionSource` and assert which task completed
  first under a 30 s net, which no machine load can turn red.

## [4.0.0] - 2026-08-19

The functional-era release: everything specs 003 and 004 delivered, in one version.
Highlights — the functional grain runtime (`grainContract` / `grainFor` / `journaledGrainFor`,
API records instead of interfaces, no code generation), full Orleans parity as first-class
operations (transactions, event sourcing, implicit stream subscriptions, `IAsyncEnumerable`
streaming replies, reentrancy policies, version-tolerant contracts, placement, lifecycle hooks),
a typed C# facade, and the classic `grain { }` surface deprecated with per-entry-point
replacement pointers. Details in the sections below (accumulated since 3.0.x).

### Added

- **`contract<'Key, 'Api>` — the short contract form.** The API record now serves as its own
  actor brand for the common one-record-one-grain case: `contract<RoomId, RoomApi> () { ... }`
  is exactly `grainContract<RoomApi, RoomId, RoomApi> () { ... }`. A separate brand (and the
  3-arity form) remains the tool when several grain types share one API record or when the
  record type must be replaceable without moving the grain's transport identity — see
  docs/functional-grains.md, "The short form: the API record as its own brand". The builders
  module's public entry points are now pinned by a surface test.

### Removed

- **The `Orleans.FSharp.EventSourcing.Marten` package.** It never contained a Marten
  integration: its three helpers forwarded to Orleans' own
  `AddLogStorageBasedLogConsistencyProvider*`, `addMartenEventStore` ignored its connection
  string, and the referenced Marten NuGet package was unused. Removed by owner decision —
  external-store adapters, if ever wanted, are a new optional package registering a named
  `ILogViewAdaptorFactory` (the shape `docs/event-sourcing.md` § "Bringing your own provider"
  documents and a hosting test pins). `examples/bank-account` now calls the Orleans
  extensions directly. The published `2.0.0-alpha.1` on nuget.org should be deprecated/unlisted
  separately.

### Fixed

- `Stream.asTaskSeq` no longer fires its stream subscription and forgets it: the subscription
  is awaited on the first pull, so a subscription failure surfaces to the consumer instead of
  leaving it pulling forever from a stream it was never subscribed to. The integration test
  that guessed a 500 ms subscription-setup delay now proves the subscription live with a
  re-published sentinel instead of sleeping. (Attribution correction: main's red
  full-integration job since 2026-08-12 was NOT this test's race — it was SDK 10.0.400
  reaching the CI runners and compiling `taskSeq` bodies down the dynamic resumable path
  TaskSeq 0.6.0 does not implement; the SDK is now pinned to 10.0.201. This fix stands on
  its own merits: a fire-and-forget subscription whose failure vanishes is a real defect
  regardless.)

### Changed

- **The declared Orleans dependency is now a floor of 10.1.0, not the newest release.**
  NuGet emits a bare `version="x"` in the nuspec as `>= x`, so whatever sits in
  `Directory.Packages.props` is the version every consumer is *forced* onto — and it
  can only be lowered by shipping a new release. It had been ratcheted to 10.2.1 by
  release-chasing rather than by need, which locked out anyone on Orleans 10.1.x or
  10.2.0 for no reason. 10.1.0 is the real floor: `JournaledGrain.ClearLogAsync`
  (dotnet/orleans#9849), used by `Orleans.FSharp.Abstractions`, does not exist in
  10.0.x. Established by building the solution against 10.0.1 (fails with CS0103),
  10.1.0, 10.2.0, 10.2.1 and 10.2.2 (all green), with the full test suite passing at
  both ends of the range. Consumers can still resolve any newer Orleans, unchanged.
- Orleans badge in `README.md` now states the supported range (10.1.0 – 10.2.2)
  instead of a single version.
- The `orleans-fsharp` template scaffolds against Orleans 10.2.2 — templates create
  new applications, where newest is the right default; this does not affect the
  library floor.

### Added

- **Server-streaming replies** (spec 004 item 6). An API record field may now be
  `'Arg -> IAsyncEnumerable<'Item>` instead of `'Arg -> Task<'Reply>`, bound with `handleStream`
  and consumed with `for … in` from F# or `await foreach` from C#. It is not a transport of ours:
  it rides Orleans' own `IAsyncEnumerableGrainExtension` — the extension a codegen grain method
  returning `IAsyncEnumerable<T>` uses, registered for every activation by `DefaultSiloServices`
  and auto-installed by `ActivationData` — so batching, long-poll heartbeats, cancel-on-dispose
  and abandoned-enumerator expiry are Orleans'. The functional side is a third request shape,
  `FunctionalStreamRequest : AsyncEnumerableRequest<FunctionalReply>`, beside the unary and
  transactional ones; because its element type is the same fixed reply, every item carries its own
  protocol token (new `stream-request`/`stream-item` directions) and its own payload limit.
  An open enumeration never blocks an ordinary call to the same activation, because every message
  of one carries Orleans' `[AlwaysInterleave]`. A streaming handler is state-neutral — it reads the
  snapshot taken when the enumeration started and publishes nothing — and the four admission
  policies (`readOnly`, `oneWay`, `alwaysInterleave`, `transactional`) plus `statelessWorker` are
  refused at sealing, each with the mechanism named. `operationId`, `sinceVersion`,
  `acceptsVersions`, placement, persistence and `journaledGrainFor` all compose. See
  [docs/streaming-replies.md](docs/streaming-replies.md).

- **Functional event sourcing** (spec 004 item 3). `journaledGrainFor contract { ... }` is a
  second definition kind over the same contract layer: `initialEventState` seeds the fold,
  `apply` is the pure replay fold, and a handler returns the events it raises together with its
  reply instead of a replacement state. The state lives in an Orleans log-consistency provider
  named by `logProvider` — the same machinery `JournaledGrain` uses, driven directly rather than
  by deriving from it, so `AddLogStorageBasedLogConsistencyProvider` and
  `AddStateStorageBasedLogConsistencyProvider` both work and so does any third-party
  `ILogViewAdaptorFactory` registered under a name. Confirmation is per turn: the runtime appends
  a handler's events atomically and waits for the provider to confirm them after the handler
  returns and before the reply leaves the activation, so a caller that got a reply is looking at
  confirmed state, and a handler that raises nothing performs no storage write at all. Unlike
  `JournaledGrain`, the replay is forced to completion during activation rather than left to the
  adaptor's batch worker, because a functional handler is handed its state as an argument. What a
  journal cannot honour is refused with its mechanism named: `transactional` (a log-view adaptor
  is not a transaction participant), `statelessWorker`, a derived grain type, and the durable
  state operations. There is deliberately no `snapshotEvery`: `ILogViewAdaptor` has no snapshot
  or truncate operation, so neither built-in provider could honour one. The classic
  `eventSourcedGrain { }` / `JournaledGrain` path is unchanged and still shipped, now documented
  as the deprecated half of `docs/event-sourcing.md`.

- **Distributed ACID transactions for the functional grain runtime** (spec 004 item 2).
  `transactionalStateFrom (TransactionalState.create<'S> name storage)` attaches an
  Orleans transactional facet, reached through `context.transactionalState`, and
  `transactional Orleans.TransactionOption.X (_.op)` declares an operation's policy.
  There is no separate transaction runtime: a transactional call is carried on Orleans'
  own `TransactionRequest<'T>` invokable base, which is where the whole protocol lives,
  so the ambient transaction is joined on the way out and created or joined on the way
  in exactly as it is for a `[Transaction]`-attributed CodeGen method. The state type
  may be an ordinary immutable F# record: the runtime stores Orleans' required
  `class, new()` instance itself and application code only ever sees `'State -> 'State`.
  Inside a transaction-scoped operation (`Create`, `CreateOrJoin`, `Join`) the
  transactional facet is the only durable effect available — the handler's replacement
  primary state is discarded and its persistent-state facades refuse every write, since
  nothing could roll either back. Orleans does **not** re-execute a handler when a
  transaction aborts; the normative re-execution semantics are in
  `docs/functional-grains.md`.
- **Functional twins for the two remaining classic-only examples.** `examples/bank-account`
  gains a `journaledGrainFor` twin over the same `AccountEvent` journal and the same
  `AccountState` view, and `examples/bank-transactions` gains a `grainFor` twin with a
  `transactionalStateFrom` facet under the same `("state", "TransactionStore")` identity
  plus a state-free orchestrator. Neither twin restates a business rule: `apply` *is*
  `AccountGrainDef.applyEvent`, the write handlers run `AccountGrainDef.handleCommand`,
  and the transactional updates are `AccountGrainDef.deposit` / `.withdraw` handed to
  Orleans as `'State -> 'State` functions — so each example's existing property tests are
  parity evidence for both paths at once. The functional twin is the live entry path in
  both; the classic definitions stay compiled, registered and tested, and both classic
  *demos* are kept as commented blocks because a bare `factory.GetGrain<IBankAccountGrain>`
  cannot resolve without C# CodeGen (verified by running them, not inferred). The
  bank-account demo prints its own replay proof from `onActivate`; the bank-transactions
  demo shows both abort shapes — a participant refusing, and an orchestrator failing after
  both accounts were written, which is the one that proves a rollback rather than a
  short-circuit.
- **`docs/api-reference.md` is functional-first.** The functional grain runtime now leads
  the page — contract, definition and journaled-definition builders, the invocation
  context, the bound reference, handler/hook types, persistent and transactional state,
  streaming replies, observers, hosting, scripting and the C# facade — with the `grain { }`
  cluster kept in full in a clearly deprecated section at the bottom. Every builder-keyword
  and context-member table is the set `tests/Orleans.FSharp.Tests/FunctionalSurfaceTests.fs`
  pins by reflection, and the classic tables were re-derived from the code rather than
  carried over: the classic `onActivate` / `onDeactivate` / `onReminder` signatures were
  wrong in `docs/`, and a `GrainMock.createMockContext` that does not exist has been
  dropped. The page's docs/ and website copies are identical again, so `api-reference.md`
  leaves `KNOWN_DRIFT` in `scripts/check-docs-mirror.py` (3 exemptions down to 2).
- **CI matrix across the supported Orleans range.** `build-and-test` now runs twice:
  at the declared floor, and at Orleans 10.2.2 via `-p:OrleansVersion=10.2.2`. Breakage
  from Orleans moving forward is caught without raising the floor for consumers. Bump
  the newest matrix entry on each Orleans release; touch `Directory.Packages.props`
  only when the code genuinely requires a newer API.

## [3.0.1] - 2026-06-29

Packaging and tooling release — **no API changes**.

### Fixed

- **`Orleans.FSharp.Templates` now publishes to NuGet.** The template package silently
  failed to pack on Linux/macOS CI: the content glob used backslash path separators
  (`orleans-fsharp\**\*`) that match nothing off-Windows, and the repo-wide symbol-package
  setting emitted an empty `.snupkg` (NU5017). Globs are now forward-slash, symbols are
  disabled for the content-only package, and the project is part of the solution publish
  set. `dotnet new install Orleans.FSharp.Templates && dotnet new orleans-fsharp -n MyApp`
  now works end to end.
- **`bank-account` example builds again** — it referenced `MartenConfig` without a project
  reference to (or `open` of) `Orleans.FSharp.EventSourcing.Marten`.

### Added

- **CI lane that builds every example.** Examples ship their own solutions outside the root
  solution and were not built by CI, so they could rot undetected; all eight now build on
  every push and PR.
- **Social-preview card** (`website/public/social-preview.png`, 1280×640) wired as
  `og:image` / `twitter:image` on the docs site and used as the GitHub repository social
  preview.

### Changed

- Versioning is back to MinVer tag-driven (the temporary `MinVerVersionOverride` used for
  the 3.0.0 release has been removed).
- Removed tracked copy-artifact files (`README 2.md`, `website/.gitignore 2`).

## [3.0.0] - 2026-06-29

**Breaking major.** The Universal Grain Pattern (`AddFSharpGrain` + `FSharpGrain.ref` / `send` /
`ask` / `post`) is now the canonical path. The non-functional `grain { }` CE keywords that were
deprecated in 2.x have been removed, along with several helper modules that did not fit the
universal-grain model. Adopting 3.0 requires removing those keywords/members from your code (see
Removed) — most callers only used the universal pattern and are unaffected.

### Removed

- **Dead `grain { }` CE keywords** — non-functional under the universal grain pattern (all F#
  grains share one `FSharpGrainImpl` class and one handler method, so per-grain class/method
  attributes cannot be expressed): `reentrant`, `interleave`, `statelessWorker`, `maxActivations`,
  `readOnly`, `oneWay`, the old string-based `mayInterleave`, `grainType`, `deactivationTimeout`,
  `implicitStreamSubscription`, and the placement operations (`preferLocalPlacement`,
  `randomPlacement`, `hashBasedPlacement`, `activationCountPlacement`,
  `resourceOptimizedPlacement`, `siloRolePlacement`, `customPlacement`). The `PlacementStrategy`
  type is also removed. To apply the equivalent per-grain Orleans attributes (`[Reentrant]`,
  `[StatelessWorker]`, `[MayInterleave]`, `[ReadOnly]`, `[OneWay]`, placement,
  `[ImplicitStreamSubscription]`, `[GrainType]`), use the per-grain `Orleans.FSharp.CodeGen` path.
- **`Telemetry` module** — wire OpenTelemetry directly with the standard .NET builder using
  Orleans' well-known names (`"Microsoft.Orleans.Runtime"`, `"Microsoft.Orleans.Application"`,
  `"Microsoft.Orleans"`).
- **`GrainServices` module** — register grain services with the `addGrainService` operation in
  `siloConfig { }`.
- **`GrainExtension` module / `getExtension`**.
- **`Behavior` / `BehaviorPattern` module** (and `BehaviorResult`) — the behavior-pattern adapters
  added in 2.x.
- **`Scripting.quickStart` and `Scripting.getGrainByString`** — use `Scripting.startOnPorts` and
  `Scripting.getGrain` (by int64 key); the rest of the `Scripting` module is unchanged.
- **`FsToolkitReexport` module.**

### Added

- **`FSharpGrain.post` is now a true one-way** — `post` / `postGuid` / `postInt` route through the
  `[OneWay] HandleMessageOneWay` interface method: the returned Task completes once the message is
  sent, no response is marshalled back, and grain-side exceptions are not propagated. (Previously
  `post` awaited the round-trip and discarded the result.)
- **`interleaveMessage typeof<'Msg>`** — a working `grain { }` CE operation that allows a message
  type to interleave, replacing the removed string-based `mayInterleave`. The universal
  `FSharpGrainImpl` carries one class-level `[MayInterleave]` predicate keyed on the message's
  runtime type; registered types match by assignability (registering a broad base type or
  interface makes every assignable message interleave — register specific types).
- **`StreamProviders.addRedisStreams name connectionString`** (**experimental**) — Redis Streams
  transport. Requires a prerelease `Microsoft.Orleans.Streaming.Redis` package (`-alpha` /
  `-preview`) at runtime — no stable 10.x exists yet; resolved by reflection so an absent package
  yields a clear "install the package" error rather than a build break.
- **`FSharpEventSourcedGrain.clearLog`** (**provider-dependent**) — clears a grain's confirmed
  event log via Orleans' `JournaledGrain.ClearLogAsync`; the confirmed view resets to the initial
  state (version 0). Throws `NotSupportedException` for log-consistency providers that do not
  override `ClearPrimaryLogAsync`.

### Changed

- **Orleans parity raised from 10.0.1 to 10.2.1.**
- **Adopted Central Package Management** — all package versions are managed centrally in
  `Directory.Packages.props`.

## [2.0.0-alpha.1] - 2026-04-28

First 2.0.0 preview. API may still shift before the stable 2.0.0 release. Install with `--prerelease` from NuGet. Headline themes:

- **Universal Grain Pattern** — call any registered F# grain without per-grain C# stubs. New `Orleans.FSharp.Abstractions` package hosts `IFSharpGrain` / `IFSharpGrainWithGuidKey` / `IFSharpGrainWithIntKey`. Register once with `services.AddFSharpGrain<State, Command>(grainDef)` and dispatch via `FSharpGrain.ref` / `send` / `ask` / `post`. Works with string, GUID, and integer keys.
- **Handler matrix completed** — 12 `handle*` CE variants covering every combination of state-only / typed result / context / cancellation: `handleState`, `handleTyped`, `handleStateWithContext`, `handleTypedWithContext`, `handleStateCancellable`, `handleTypedCancellable`, `handleStateWithContextCancellable`, `handleTypedWithContextCancellable`, plus the existing four. `getCancellableContextHandler` is the universal dispatch fallback.
- **Behavior pattern adapters** — `Behavior.run` and `Behavior.runWithContext` plug behavior handlers directly into `handleState` / `handleStateWithContext` without manual `BehaviorResult` unwrapping. `runWithContext` calls `ctx.DeactivateOnIdle()` on `Stop`.
- **`ask` / `askGuid` / `askInt`** — typed result access for handlers that return a value distinct from the state.
- **`Orleans.FSharp.Analyzers`** — new package shipping `OF0001` (warns on `async {}` in grain code) with `[<AllowAsync>]` opt-out.
- **Safer state migrations** — `StateMigration.tryApplyMigrations` returns `Result<'T, string list>` instead of throwing. `GrainContext.empty` for unit tests.
- **Auto-registered F# binary serializer** — `AddFSharpGrain` wires `FSharpBinaryCodec` automatically; no manual `addToSerializerBuilder` call needed for the universal pattern.
- **Test growth** — ~1500 tests (unit + integration), including 27 new FsCheck property suites across `StateMigration`, `SchemaEvolution`, `GrainRef`, `InputValidation`, and the full handler matrix.
- **MinVer-driven release** — version is now derived from the `v*` git tag; CI publishes on tag push.

### Deprecations

The following 7 `grain { }` CE keywords are now compile-time warnings (not errors) and are non-functional in the universal grain pattern. They remain in the API for source compatibility but produce no runtime effect, since all F# grains share `FSharpGrainImpl` and cannot carry per-grain class or per-method attributes:

- `reentrant`
- `statelessWorker`
- `maxActivations`
- `mayInterleave`
- `interleave`
- `oneWay`
- `readOnly`

To apply class-level (`[Reentrant]`, `[StatelessWorker]`, `[MayInterleave]`) or per-method (`[AlwaysInterleave]`, `[ReadOnly]`, `[OneWay]`) attributes, write a per-grain C# stub manually using the legacy `Orleans.FSharp.CodeGen` pattern. Existing callers will see warnings but continue to compile.

### Other breaking changes

- `IUniversalGrainHandler.Handle` signature widened from 2 to 4 parameters (`serviceProvider`, `grainFactory` added). Pass `null` in tests that do not exercise context.
- `IFSharpGrain` no longer inherits `IRemindable`. `IRemindable` is implemented directly by `FSharpGrain<'S,'M>` in `Orleans.FSharp.Runtime`. This avoids pulling the `Microsoft.Orleans.Reminders` source generator into the Abstractions project.

### Migration from 1.0.0

1. Add `Orleans.FSharp.Abstractions` to your silo project.
2. Register grains: `services.AddFSharpGrain<State, Command>(myGrainDef)`.
3. Call grains: `FSharpGrain.ref<State, Command> factory "key" |> FSharpGrain.send MyCommand`.
4. Replace any uses of the 7 deprecated CE keywords if you need their effect — write a per-grain C# stub via `Orleans.FSharp.CodeGen`.
5. Update `IUniversalGrainHandler.Handle` callers to pass the new `serviceProvider` and `grainFactory` parameters.

### Detailed change list

### `StateMigration.tryApplyMigrations` — safe Result-based migration

A new function that validates the migration chain before applying it, returning
`Result<'T, string list>` instead of throwing on an invalid chain:

```fsharp
match StateMigration.tryApplyMigrations<StateV3> migrations 1 (box oldState) with
| Ok newState -> // use newState
| Error errs  -> for e in errs do log.LogError("Migration error: {Error}", e)
```

Compared to `applyMigrations` (which throws on gaps/duplicates), `tryApplyMigrations`
is the preferred choice for production grain activation paths where you want to surface
migration errors through structured logging rather than runtime exceptions.

Also fixes a dead-code `List.iter` call in `StateMigration.validate` that was a no-op.

### `Behavior.run` and `Behavior.runWithContext` adapters

Two new adapter functions in the `Behavior` module eliminate the need to manually unwrap
`BehaviorResult` inside handler lambdas:

```fsharp
// Before — manual unwrap inside handleState
handleState (fun state cmd -> task {
    let! result = myBehaviorHandler state cmd
    return Behavior.unwrap state result
})

// After — plug the behavior handler directly
handleState (Behavior.run myBehaviorHandler)

// With context + deactivation on Stop
handleStateWithContext (Behavior.runWithContext myContextBehaviorHandler)
```

`Behavior.runWithContext` calls `ctx.DeactivateOnIdle()` automatically when the handler
returns `Stop`, so the grain is scheduled for deactivation without any extra code in the handler.

### `GrainContext.empty` convenience value

A pre-built empty `GrainContext` for use in unit tests where the handler does not interact
with the grain factory or service provider:

```fsharp
let handler = GrainDefinition.getContextHandler myGrain
let! ns, _ = handler GrainContext.empty initialState myCmd
```

### Testing guide expanded

`docs/testing.md` now covers direct handler testing for all 12 CE variants including
`getCancellableContextHandler` as the universal dispatch fallback, with a complete
score-tracker FsCheck property test example.

### FsCheck property test expansion

Generative property tests added to cover invariants across multiple modules:

- **StateMigration** (6 properties): `applyMigrations` with empty list, idempotency, `validate` for
  contiguous chains of any length, determinism, gap detection for any non-adjacent pair, identity
  migration preserves content
- **SchemaEvolution** (9 properties): JSON roundtrips for all V2 type variants, serialization
  determinism, backward-compatible case deserialization across versions
- **GrainRef** (5 properties): key roundtrip for string and int64, `invoke` dispatch for any key
  and payload, `unwrap` returns responsive grain
- **InputValidation** (7 properties): exhaustive whitespace rejection for `persist`, `clusterId`,
  `addMemoryStorage`; acceptance of any non-whitespace name

### Test coverage

- 12 new unit tests for `Behavior.run` / `Behavior.runWithContext` (including 2 FsCheck properties)
- 8 integration tests for the Behavior pattern grain (`TestGrains14`, `WorkflowGrain`)
- 27 new FsCheck property tests across StateMigration, SchemaEvolution, GrainRef, InputValidation
- Total: **~1500 tests** (unit + integration)

---

### `handleWithContext` — grain-to-grain calls via `IUniversalGrainHandler`

`IUniversalGrainHandler.Handle` now accepts `IServiceProvider` and `IGrainFactory` parameters,
enabling grains defined with `handleWithContext` (or `handleWithServices`) to make grain-to-grain
calls and resolve DI services when dispatched through the universal `AddFSharpGrain` pattern:

```fsharp
// Relay grain: on ForwardPing, calls a peer PingGrain via ctx.GrainFactory
let relay =
    grain {
        defaultState { PingsSent = 0; LastPeerCount = 0 }
        handleWithContext (fun ctx state cmd ->
            task {
                match cmd with
                | ForwardPing peerKey ->
                    let peer = FSharpGrain.ref<PingState, PingCommand> ctx.GrainFactory peerKey
                    let! peerState = FSharpGrain.send Ping peer
                    return { PingsSent = state.PingsSent + 1; LastPeerCount = peerState.Count }, box ()
                | ...
            })
    }

// Register with AddFSharpGrain and call as usual — context is threaded automatically
siloBuilder.Services.AddFSharpGrain<RelayState, RelayCommand>(relay) |> ignore
```

**Breaking change:** `IUniversalGrainHandler.Handle` signature changed from 2 to 4 parameters.
Callers of `Handle` must pass `serviceProvider` and `grainFactory` (use `null` in tests that do not exercise context).

### Sample: `LeaderboardGrain`

New sample grain in `Orleans.FSharp.Sample` demonstrating the `handleWithContext` pattern
for grain-to-grain fan-out: a leaderboard grain queries multiple player-score grains in
parallel via `Task.WhenAll`, sorts by score, and caches the snapshot.

### Test coverage

- 7 integration tests for `handleWithContext` (relay grain, grain-to-grain forwarding, isolation)
- 7 integration tests for `handleStateCancellable` (cancellable accumulator grain)
- 7 integration tests for `handleCancellable` (raw cancellable handler with manual box)
- 8 integration tests for `handleTypedCancellable` (typed result + CancellationToken, uses `ask`)
- 10 integration tests for `handleWithContextCancellable` (CtxCancAcc grain — mixed pure accumulation + grain-to-grain via ctx.GrainFactory)
- 9 integration tests for `handleState` (score accumulator grain)
- 8 integration tests for `handleStateWithContext` (state-only return + ctx.GrainFactory)
- 8 integration tests for `handleTypedWithContext` (typed result + ctx, uses `ask`)
- `HandlerCompositionProperties.fs` — 25 FsCheck property tests for handler invariants
  (added 11 new: hasAnyHandler for all variants, handleTypedCancellable result+token,
  handleWithContextCancellable ctx+token, handleStateWithContext ctx threading,
  handleTypedWithContext result typing)
- Expanded `ErrorMessageTests.fs` — error paths for context-only, CancellableContextHandler-only,
  and empty definitions; strengthened assertions to use `&&` instead of `||`
- `AnalyzerTests.fs` — `use!` binding, while/for loop tests; 23 total analyzer tests

### Bug fixes

- `AsyncUsageAnalyzer` — remove phantom `LetOrUseBang` case (merged into `LetOrUse(isBang=true)` in FCS 43.10+)
- `GrainBuilderTests` — FsCheck persist-name property: `IsNullOrEmpty` → `IsNullOrWhiteSpace` for tab characters
- `GrainMockTests` — fix spurious test that discarded `s1.Total` rather than asserting it

### New Package: `Orleans.FSharp.Analyzers`

Compile-time F# analyzer for Orleans grain code:

- **OF0001** — warns when `async { }` is used instead of `task { }` in Orleans grain handlers and Task-returning methods
- `[<AllowAsync>]` attribute suppresses OF0001 on a specific binding when `async { }` is genuinely required
- `AstWalker.collectAsyncRanges` walks the full untyped F# AST (LetOrUse, Match, Lambda, If/Then/Else, TryWith, TryFinally, nested modules, class methods, record fields)
- 20 unit tests covering detection, suppression, structural nesting, and attribute mechanics
- Add to your project: `dotnet add package Orleans.FSharp.Analyzers`

### New Packages
- **`Orleans.FSharp.Abstractions`** — New C# shim package hosting `IFSharpGrain`, `IFSharpGrainWithGuidKey`, and `IFSharpGrainWithIntKey` interfaces. Orleans source generators run on this project and produce public `Proxy_IFSharpGrain*` classes in the same assembly. Reference this from your silo instead of `Orleans.FSharp.CodeGen`.

### Universal Grain Pattern — code-gen-free grain calls

A brand-new way to define and call F# grains with zero per-grain C# stubs:

```fsharp
// Register at silo startup
siloBuilder.Services.AddFSharpGrain<PingState, PingCommand>(pingGrain)

// Call from any F# code — no ICounterGrain interface needed
let handle = FSharpGrain.ref<PingState, PingCommand> factory "grain-id"
let! state = handle |> FSharpGrain.send Ping

// GUID and integer keys also supported
let handle = FSharpGrain.refGuid<MyState, MyCmd> factory (Guid.NewGuid())
let handle = FSharpGrain.refInt<MyState, MyCmd> factory 42L
```

**Implementation:**
- `FSharpGrainImpl` — concrete `Grain` class for string-keyed grains (in Abstractions, auto-discovered by Orleans)
- `FSharpGrainGuidImpl` — concrete `Grain` class for GUID-keyed grains
- `FSharpGrainIntImpl` — concrete `Grain` class for integer-keyed grains
- `UniversalGrainHandlerRegistry` — routes messages to registered F# handlers by DU type name
- `IUniversalGrainHandler` / `GrainDispatchResult` — C#-to-F# dispatch interface
- **Correct F# DU dispatch:** nullary DU cases in mixed DUs compile to private `_CaseName` nested types; the registry uses `BindingFlags.Public | BindingFlags.NonPublic` when scanning nested types so all case variants are routed correctly

### New: Observer integration tests

Full end-to-end integration test suite for `FSharpObserverManager<'T>` running in a real `TestCluster`:
- `Observer.createRef` / `Observer.deleteRef` lifecycle
- `Observer.subscribe` IDisposable pattern
- Single and multiple observers, fan-out to N subscribers
- `Unsubscribe` stops notification delivery
- Empty broadcast completes without error

### New: `ask` / `askGuid` / `askInt` — typed result access

New variants in `FSharpGrain` module that return a separately-specified result type `'R`
instead of the grain state. Use these with `handleTyped` grains or any handler that
returns a value different from the state:

```fsharp
// Handler defined with handleTyped — result is int, not CalcState
let handle = FSharpGrain.ref<CalcState, CalcCommand> factory "calc-1"
let! result: int = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(3, 4))
// result = 7

// Also available for GUID and integer key grains
let! label: string = guidHandle |> FSharpGrain.askGuid<S, C, string> GetLabel
let! count: int64  = intHandle  |> FSharpGrain.askInt<S, C, int64> GetCount
```

### New: `handleTyped` end-to-end integration tests

Added `CalcGrain` (registered in `ClusterFixture`) that uses `handleTyped` to define a
calculator without any manual `box` calls. 8 integration tests cover `AddValues`,
`MultiplyValues`, `OpCount`, and the `post+ask` pattern.

### Improvements
- `GrainDefinition.invokeReminderHandler` — new C#-callable function for delegating to F# reminder handlers by name; used internally by backward-compat grain stubs
- **`AddFSharpGrain` auto-registers `FSharpBinaryCodec`** — no manual `FSharpBinaryCodecRegistration.addToSerializerBuilder` call needed on the silo side when using the universal pattern. Registration is idempotent across multiple `AddFSharpGrain<_,_>` calls.
- **XML remarks on `FSharpGrain` module** clarify when to use `send` vs `ask` vs `post`
- 30 new integration tests (universal pattern string/GUID/int keys + observers)
- 54 new unit tests: GrainDispatchResult, impl class metadata, registry dispatch, FsCheck properties, `AddFSharpGrain` DI wiring (14 new)
- 6 FsCheck property tests for `handleState`/`handleTyped` CE variants in `GrainBuilderTests`
- 8 `ask`/`askGuid`/`askInt` integration tests with `QueryGrain`
- 8 `handleTyped` integration tests with `CalcGrain`
- `GrainHandlerStateMachineProperties.fs` — 11 FsCheck properties for score-tracker grain
  testing actual handler pipeline (handleState, handleTyped, handleStateCancellable):
  net-score invariant, Reset to zero, N-wins, GetScore idempotency, Win+Lose symmetry,
  handleState/handleStateCancellable equivalence
### New CE operations: `handleStateWithContextCancellable` / `handleTypedWithContextCancellable`

Two new CE keywords completing the handler variant matrix — the final combination of
context + cancellation + convenience return style:

- `handleStateWithContextCancellable` — `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State>` (no manual `box`)
- `handleTypedWithContextCancellable` — `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State * 'Result>` (no manual `box`)
- Aliases: `handleStateWithServicesCancellable`, `handleTypedWithServicesCancellable`

Both store in `CancellableContextHandler` (the same slot as `handleWithContextCancellable`) and are
reachable through the full fallback chain in `getCancellableContextHandler`.

- 6 integration tests for `handleStateWithContextCancellable` (state-only + ctx.GrainFactory)
- 6 integration tests for `handleTypedWithContextCancellable` (typed result via `ask`)
- 5 FsCheck properties (ctx+token threading, equivalence, hasAnyHandler)
- Total: **1176 unit + 238 integration = 1414 tests**

### Documentation
- Rewrote `docs/getting-started.md` to lead with the universal grain pattern (no attributes, no C# stubs)
- Added `ask` to getting-started quick-reference table
- Added auto-registration callout to `docs/serialization.md`
- Added `ask`/`askGuid`/`askInt` entries to `docs/api-reference.md` FSharpGrain table
- Expanded `docs/testing.md` with direct handler testing and universal pattern test examples
- Added `handleState`/`handleTyped` documentation to `docs/grain-definition.md`

### Deprecations

Seven `grain { }` CE keywords are now marked `[<Obsolete>]` (compile warnings, not errors) because
they are non-functional under the universal F# grain pattern, where all grains share `FSharpGrainImpl`:

- Class-level attributes (cannot be applied to a shared impl class): `reentrant`, `statelessWorker`, `maxActivations`, `mayInterleave`
- Per-method attributes (the universal pattern exposes a single `HandleMessage(object)` entry point): `interleave`, `oneWay`, `readOnly`

Existing call sites continue to compile. To get the underlying Orleans behavior, write a per-grain
C# stub manually using `Orleans.FSharp.CodeGen`.

### Breaking changes
- `IFSharpGrain` no longer inherits `IRemindable`. `IRemindable` is implemented directly by `FSharpGrain<'S,'M>` in `Orleans.FSharp.Runtime`. This avoids pulling the `Microsoft.Orleans.Reminders` source generator into the Abstractions project.
- `IUniversalGrainHandler.Handle` signature changed from 2 to 4 parameters (added `serviceProvider`, `grainFactory`).

### Migration
From `Orleans.FSharp.CodeGen` (per-grain stubs) to universal `IFSharpGrain` pattern:
1. Add `Orleans.FSharp.Abstractions` to your silo project
2. Register grains: `services.AddFSharpGrain<State, Command>(myGrainDef)`
3. Call grains: `FSharpGrain.ref<State, Command> factory "key" |> FSharpGrain.send MyCommand`
4. `Orleans.FSharp.CodeGen` is still available for backward compatibility (per-grain C# stubs)

---

## [1.0.0] - 2026-04-03

### First stable release — full Orleans 10.0.1 parity from F#

804 tests (718 unit + 86 integration), zero warnings, zero `Unchecked.defaultof` in source.

### Core (`Orleans.FSharp`)

#### Grain Definition — `grain { }` CE (31 keywords)
- `defaultState`, `handle`, `handleWithContext`, `handleWithServices` — basic grain definition
- `handleCancellable`, `handleWithContextCancellable`, `handleWithServicesCancellable` — CancellationToken support
- `persist`, `additionalState` — single and multiple named persistent states
- `onActivate`, `onDeactivate`, `onLifecycleStage` — lifecycle hooks
- `onReminder`, `onTimer` — declarative reminders and timers
- `reentrant`, `interleave`, `readOnly`, `mayInterleave` — concurrency control
- `statelessWorker`, `maxActivations` — stateless worker grains
- `implicitStreamSubscription` — automatic stream subscriptions
- `oneWay`, `grainType`, `deactivationTimeout` — method and type annotations
- 7 placement strategies: `preferLocalPlacement`, `randomPlacement`, `hashBasedPlacement`, `activationCountPlacement`, `resourceOptimizedPlacement`, `siloRolePlacement`, `customPlacement`

#### Modules
- **GrainRef** — type-safe grain references: `ofString`, `ofGuid`, `ofInt64`, `ofGuidCompound`, `ofIntCompound`, `invoke`, `invokeOneWay`, `invokeWithTimeout`
- **GrainState** — immutable state wrapper: `read`, `write`, `clear`, `current`
- **GrainContext** — DI access from handlers: `getService<'T>`, `getState<'T>`, `grainId`, `primaryKeyString/Guid/Int64`, `deactivateOnIdle`, `delayDeactivation`
- **Stream** — Orleans streaming with TaskSeq: `getStream`, `publish`, `subscribe`, `asTaskSeq`, `unsubscribe`, `subscribeFrom`, `getSubscriptions`, `resumeAll`
- **BroadcastChannel** — fan-out pub/sub: `getChannel`, `publish`
- **StreamProviders** — `addEventHubStreams`, `addAzureQueueStreams`
- **Reminder** — persistent reminders: `register`, `unregister`, `get`
- **Timers** — in-memory timers: `register`, `registerWithState`
- **Observer** — grain observers: `createRef`, `deleteRef`, `subscribe` + `FSharpObserverManager<'T>`
- **Filter** — call interceptors: `incoming`, `outgoing`, `incomingWithAround`, `outgoingWithAround`
- **FilterContext** — introspect grain calls: `methodName`, `interfaceType`, `grainInstance`
- **RequestCtx** — propagate context across calls: `set`, `get`, `getOrDefault`, `remove`, `withValue`
- **Log** — structured logging with correlation: `logInfo`, `logWarning`, `logError`, `logDebug`, `withCorrelation`, `currentCorrelationId`
- **Transactions** — `TransactionalState.read`, `update`, `performRead` + `TransactionOption` DU
- **Versioning** — `CompatibilityStrategy`, `VersionSelectorStrategy`
- **Telemetry** — OpenTelemetry: `runtimeActivitySourceName`, `meterName`, `enableActivityPropagation`
- **GrainDirectory** — `Default`, `Redis`, `AzureStorage`, `Custom`
- **GrainServices** — `addGrainService<'T>`
- **GrainExtension** — `getExtension<'T>`
- **Kubernetes** — `useKubernetesClustering`, `useKubernetesClusteringWithNamespace`
- **Shutdown** — `configureGracefulShutdown`, `stopHost`, `onShutdown`
- **StateMigration** — typed migrations: `migration`, `applyMigrations`, `validate`
- **Serialization** — `fsharpJsonOptions`, `addFSharpConverters`, `withConverters`
- **FSharpSerialization** — `addFSharpSerialization` (native Orleans serializer)
- **Scripting** — `quickStart`, `getGrain`, `shutdown` for .fsx REPL
- **Immutable<'T>** — `immutable`, `unwrapImmutable` for zero-copy

### Silo Configuration (`Orleans.FSharp.Runtime`)

#### `siloConfig { }` CE (39 keywords)
- **Clustering**: `useLocalhostClustering`, `addRedisClustering`, `addAzureTableClustering`, `addAdoNetClustering`
- **Storage**: `addMemoryStorage`, `addRedisStorage`, `addAzureBlobStorage`, `addAzureTableStorage`, `addAdoNetStorage`, `addCosmosStorage`, `addDynamoDbStorage`, `addCustomStorage`
- **Streaming**: `addMemoryStreams`, `addPersistentStreams`, `addBroadcastChannel`
- **Reminders**: `addMemoryReminderService`, `addRedisReminderService`, `addCustomReminderService`
- **Security**: `useTls`, `useTlsWithCertificate`, `useMutualTls`, `useMutualTlsWithCertificate`
- **Infrastructure**: `addDashboard`, `addDashboardWithOptions`, `enableHealthChecks`, `addStartupTask`, `addGrainService`
- **Filters**: `addIncomingFilter`, `addOutgoingFilter`
- **Identity**: `clusterId`, `serviceId`, `siloName`, `siloPort`, `gatewayPort`, `advertisedIpAddress`
- **Tuning**: `grainCollectionAge`, `useGrainVersioning`, `useSerilog`, `configureServices`

#### `clientConfig { }` CE (11 keywords)
- `useLocalhostClustering`, `useStaticClustering`, `clusterId`, `serviceId`
- `useTls`, `useTlsWithCertificate`, `useMutualTls`
- `addMemoryStreams`, `configureServices`
- `gatewayListRefreshPeriod`, `preferredGatewayIndex`

### Event Sourcing (`Orleans.FSharp.EventSourcing`)
- `eventSourcedGrain { }` CE: `defaultState`, `apply`, `handle`, `logConsistencyProvider`
- `EventStore` module: wraps JournaledGrain methods
- `MartenConfig`: placeholder for PostgreSQL event store integration

### Testing (`Orleans.FSharp.Testing`)
- **TestHarness** — in-process test cluster: `createTestCluster`, `getGrain`, `captureLogs`, `reset`, `dispose`
- **GrainMock** — mock factory for unit tests: `create`, `withGrain`, `createMockContext`
- **GrainArbitrary** — TypeShape-based auto FsCheck Arbitrary: `forState<'T>`, `forCommands<'T>`
- **FsCheckHelpers** — `stateMachineProperty`, `commandSequenceArb`
- **LogCapture** — `CapturedLogEntry`, `CapturingLoggerFactory`

### Analyzers (`Orleans.FSharp.Analyzers`)
- **OF0001**: Warns on `async { }` usage — suggests `task { }` for Orleans compatibility
- Supports `[<AllowAsync>]` opt-out attribute

### CodeGen (`Orleans.FSharp.CodeGen`)
- C# bridge project for Orleans Roslyn source generators
- Required because Orleans source generators only work on C# projects

### Infrastructure
- `.NET 10` / `F# 9+` / `Orleans 10.0.1`
- `FsToolkit.ErrorHandling 5.2.0` — `taskResult { }` CE available
- `TypeShape` — auto FsCheck Arbitrary generation
- `FSharp.SystemTextJson` — DU/Record serialization
- `FSharp.Control.TaskSeq` — streaming with `taskSeq { }`
- `IcedTasks` — ColdTask, CancellableTask CE extensions
- `Serilog` — structured logging
- GitHub Actions CI with Gitleaks security scanning
- NuGet trusted publisher (OIDC)
- Source Link + symbol packages (snupkg)
- `dotnet new orleans-fsharp` project template
- Allocation benchmarks: GrainRef struct confirmed zero-alloc
- Input validation on all CE string parameters (35 tests)
- TLS/mTLS security warnings in XML docs

### Documentation
- 10 comprehensive guides (3,800+ lines)
- Per-package NuGet README files
- CONTRIBUTING.md, CODE_OF_CONDUCT.md, SECURITY.md
- 3 sample patterns: CQRS, Saga, Rate Limiter
- Complete API reference

[Unreleased]: https://github.com/Neftedollar/orleans-fsharp/compare/v4.0.1...HEAD
[4.0.1]: https://github.com/Neftedollar/orleans-fsharp/compare/v4.0.0...v4.0.1
[4.0.0]: https://github.com/Neftedollar/orleans-fsharp/compare/v3.0.2...v4.0.0
[2.0.0-alpha.1]: https://github.com/Neftedollar/orleans-fsharp/releases/tag/v2.0.0-alpha.1
[1.0.0]: https://github.com/Neftedollar/orleans-fsharp/releases/tag/v1.0.0
