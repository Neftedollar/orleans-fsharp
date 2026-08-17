# Feature Proposal 004: Orleans Parity Extensions for the Functional Grain Runtime

**Status:** proposal (design sketches; not yet an implementation contract)

**Target:** .NET 10, F# 10, Orleans 10.1.0 minimum; newest tested Orleans must
also pass (10.2.2 at the time of writing)

**Baseline:** the functional grain runtime shipped by spec 003. Already
covered there and NOT in scope here: persistence, timers, reminders,
collection age, call filters, request context, cooperative cancellation,
explicit streams (producer and all three consumer arms), functional observers
(client push), broadcast-channel production and consumption, heterogeneous
clusters, composed stateless-worker placement, per-actor Dashboard
visibility.

## Objective

Close the remaining gaps between the functional grain runtime and the Orleans
feature set, so that "written against Orleans.FSharp" stops implying any
feature sacrifice. Items are ordered by a rough cost/coupling judgment, not
by demand statistics — no usage data exists for such a ranking.

Every design here must satisfy two standing rules established in spec 003:

1. **Earliest-stage validation** — a configuration the transport cannot honor
   is rejected at contract construction or definition sealing, never at first
   call.
2. **C#-consumable surfaces** — replies and handles must be BCL or otherwise
   C#-idiomatic types. A C# grain can already call a functional grain today
   (compiler-verified):

   ```csharp
   // requires: reference to the F# contracts assembly, Orleans.FSharp,
   // and an explicit FSharp.Core >= 10.1.0 PackageReference
   var api = RoomApiModule.@ref.Invoke(factory).Invoke(RoomIdModule.create("general"));
   await api.join.Invoke(UserIdModule.create("alice"));
   var result = await api.say.Invoke(new PostMessage(...));
   ```

   Workable, not idiomatic. Item 9 turns this into a supported surface.

---

## 1. Implicit stream subscriptions (and implicit broadcast consumers)

**Gap (proven, spec-003 feature tour §11):** two-part wall. (a) The functional
manifest publishes no stream-binding grain properties, and (b) even with the
bindings forced in, the functional activation does not participate in
`IStreamConsumerExtension` delivery — Orleans activates the grain, then drops
the item ("… I don't have any subscriber for that stream. Dropping on the
floor.").

**Design sketch:**

- New definition operations, declarative like `onTimer`/`onReminder`:

  ```fsharp
  onStream "provider" "namespace" (fun context state item -> task { ... return state' })
  onBroadcast channelProvider channelId (fun context state item -> task { ... })
  ```

- Sealing freezes stream bindings into definition metadata; the registry's
  properties provider publishes the same binding keys Orleans' codegen
  publishes for `[ImplicitStreamSubscription]`.
- The functional activator installs the stream-consumer extension component
  during activation (same `IConfigureGrainTypeComponents` seam the activator
  already owns) and routes deliveries into the declared hook with whole-state
  replacement semantics (the timer-hook rules apply: `Interleave = false`,
  publication on successful return only).
- Broadcast consumption rides the same machinery with the channel-subscriber
  extension.

**Open questions:** batch delivery (`IAsyncBatchObserver`) in scope or later;
rewindable-stream cursors exposed to the hook or deliberately not (Orleans
exposes `StreamSequenceToken` — the hook signature must decide).

**Size:** M/L. **Depends on:** nothing new.

## 2. Distributed ACID transactions

**Gap:** the classic KEEP-path (`AddFSharpTransactionalGrain`) exists; the
functional model has no transactional state.

**Design sketch:**

- `transactionalStateFrom (TransactionalState.create<'S> "name" "storage")`
  attaches an `ITransactionalState<'S>` facet; `context.transactionalState
  descriptor` returns it invocation-bound (facade rules mirror
  `persistentState`: expiry, readOnly rejection).
- Per-operation policy: `transactional TransactionOption.CreateOrJoin (_.op)`
  on the contract; the descriptor's admission carries the option and dispatch
  maps it onto Orleans' transactional request path.
- Sealing rules: a transactional operation must not be `oneWay`
  (no ack = no commit report), must not mix with `alwaysInterleave`;
  transactional facets and ordinary persistent facets may coexist but a
  handler is either transactional or not — decided by its operation.

**Open questions:** whether `PerformRead`/`PerformUpdate` lambda shapes or a
state-in/state-out handler adaptation fit better (the latter matches the
runtime's idiom but transactions re-execute — handlers must be pure over the
transactional read); exactly-once semantics documentation.

**Size:** L. **Depends on:** nothing new, but the re-execution semantics need
their own spec section as normative text.

## 3. Event sourcing

**Gap:** classic `FSharpEventSourcedGrain` (JournaledGrain) exists; no
functional equivalent.

**Design sketch (least-machinery direction):**

- A separate definition kind sharing the contract layer:

  ```fsharp
  journaledGrainFor RoomApi.contract {
      initialEventState (fun key -> initial)
      apply (fun state event -> state')          // pure fold
      handle (_.op) (fun ctx state arg -> task {
          return [ Event1; Event2 ], reply })    // handlers RAISE, never mutate
      snapshotEvery 100
  }
  ```

- The activation target hosts a `JournaledGrain`-equivalent log through
  Orleans' log-consistency providers; `apply` is the replay fold; handler
  returns events + reply; confirmation strategy (`confirmEvents` implicit per
  turn) spelled normatively.

**Provider story (decided, owner ruling 2026-08-17):** reuse Orleans' own
log-consistency providers (`LogStorage`/`StateStorage`) as the base — the
functional journaled model stays inside the Orleans ecosystem. Marten support
ships as a separate optional adapter package (precedent: the classic path's
`Orleans.FSharp.EventSourcing.Marten` already follows exactly this shape).
Remaining open question: upcasting hooks.

**Size:** L. **Depends on:** provider-story decision.

## 4. First-class placement operations

**Status (Phase A, Task A1 — delivered):** implemented on
`feat/004-parity-phase-a`. `statelessWorker (maxLocalWorkers: int)` and
`placement (strategy: PlacementStrategy)` are `grainFor` definition
operations (`src/Orleans.FSharp/FunctionalDefinition.fs`); the registry's
`FunctionalGrainPropertiesProvider` publishes their manifest properties
(`src/Orleans.FSharp.Runtime/FunctionalManifest.fs`), replacing the
feature-tour §10 composition (`examples/feature-tour/src/FeatureTour/Placement.fs`
now declares `statelessWorker 4` directly; its `FunctionalPlacementProvider`
composition helper is kept as a one-line note that hand-composing placement
remains possible, not as the primary path — status matrix row 12 moved from
composed to supported).

**`PlacementStrategy`, resolved:**

```fsharp
type PlacementStrategy =
    | Random               // Orleans' own default
    | PreferLocal
    | ActivationCountBased
    | ResourceOptimized
```

Verified by reflection against Orleans 10.1.0 and 10.2.2 (both, identical):
all four strategy classes, all four placement attributes, and the
`StatelessWorkerPlacement` strategy and its attribute are present on both
versions with byte-identical `IGrainPropertiesProviderAttribute.Populate`
output. **No strategy here is version-gated.** Orleans also ships
`HashBasedPlacement` and `SiloRoleBasedPlacement` (present on both versions
too) plus the internal `ClientObserversPlacement` /
`SystemTargetPlacementStrategy`; none of the four are mirrored — hash-based
and silo-role placement address separate, more specialized concerns this
design sketch's candidate list did not name, and the other two are not
meant for application grains.

**Exact published properties (verified live, identical on both versions):**

| Operation | `placement-strategy` | Other properties |
|---|---|---|
| `placement Random` | `RandomPlacement` | — |
| `placement PreferLocal` | `PreferLocalPlacement` | — |
| `placement ActivationCountBased` | `ActivationCountBasedPlacement` | — |
| `placement ResourceOptimized` | `ResourceOptimizedPlacement` | — |
| `statelessWorker n` | `StatelessWorkerPlacement` | `max-local-instances = n`, `remove-idle-workers = True` (`bool.ToString()` casing), `unordered = true` (lowercase literal, independent of any `removeIdleWorkers` argument — this runtime does not expose one) |

A property-key exactness test
(`tests/Orleans.FSharp.Tests/FunctionalRuntimeTests.fs`) constructs a live
`StatelessWorkerAttribute`/`*PlacementAttribute` and diffs its real
`Populate()` output against the registry provider's, catching the
`True`-vs-`true` casing trap a hand-transcribed reference would have missed.

**Sealing (as designed, confirmed by mutation-checked tests):** `statelessWorker`
and `placement` are mutually exclusive in either declaration order;
`statelessWorker` requires a strictly positive `maxLocalWorkers` and rejects
`stateFrom`, `usePersistentState`, `onReminder`, and `collectionAge` — in
either order relative to `statelessWorker` itself, checked at sealing
(`DefinitionDraft.run`) rather than at the custom-operation call site, since
the rejected operation may be declared before or after `statelessWorker`.

**Size:** S/M as estimated. **Depends on:** nothing, confirmed.

## 5. Reentrancy variants

**Gap:** per-operation `readOnly`/`alwaysInterleave` exist; Orleans also has
whole-grain `[Reentrant]` and predicate `[MayInterleave]`.

**Design sketch:**

- Contract operations: `reentrant` (whole grain — publishes the reentrancy
  property) and `mayInterleave (fun (meta: IFunctionalRequestMetadata) -> bool)`
  — the predicate registered per grain type; dispatch adapts Orleans'
  may-interleave callback to our envelope metadata (the public metadata
  interface exists precisely for filters/predicates).
- Sealing: `reentrant` makes per-operation interleave flags redundant —
  reject the combination to keep contracts unambiguous.

**Open questions:** whether the predicate sees only metadata or also the
deserialized argument (metadata-only keeps the protocol-before-payload
invariant from spec 003 — the recommended answer).

**Size:** M.

## 6. Server-streaming replies (`IAsyncEnumerable<'T>`)

**Gap:** API fields are exactly `'Arg -> Task<'Reply>`; Orleans grains can
return `IAsyncEnumerable<T>` (codegen-based). The functional transport has no
streaming-reply envelope.

**Design sketch:**

- New field kind: `'Arg -> IAsyncEnumerable<'Item>` (BCL type — directly
  consumable from C# with `await foreach`, satisfying the C#-surface rule).
- Transport: a chunk-stream envelope family over the fixed transport —
  sequence-numbered item envelopes + completion/fault envelope, one-way from
  the target with credit-based flow control from the caller, cancellation
  through the existing cancellable machinery (disposing the enumerator sends
  the cancel).
- Handler shape: `handle (_.watch) (fun ctx state arg -> taskSeq { ... })`
  (or an explicit writer object — decide against `taskSeq` dependency vs a
  minimal own builder).
- Payload limits apply per item envelope; the spec-003 hot-path rule applies
  (no per-item reflection).

**Open questions:** backpressure window defaults; whether interleaving rules
treat an in-flight stream as an open turn (it must not block the activation —
likely requires the streaming send to run detached like one-way notifies).

**Size:** L — the largest single item; propose it as its own phase or even
its own spec once 004's smaller items land.

## 7. Version-tolerant contracts

**Gap (deliberate spec-003 design):** requests must match the hosted contract
version exactly; a rolling deploy across a version bump rejects old-version
calls (documented, with the two-contracts pattern as the workaround).

**Design sketch:**

- Opt-in contract operation: `acceptsVersions (fun v -> v >= 3)` or a closed
  policy set (`exact` default | `backwardCompatible minVersion`).
- Per-operation `sinceVersion n` metadata so a v4 host can reject a v3 call
  only for operations that did not exist in v3.
- The wire already carries the version; only dispatch admission changes.
  Storage identity and operation IDs stay version-independent (spec 003
  already guarantees this).

**Open questions:** whether argument-shape evolution within an accepted range
is the application's problem (recommended: yes — document that accepting a
version asserts wire compatibility; no magic).

**Size:** M (mechanism) — the normative text is the hard part.

## 8. Small parity items

### 8a. Lifecycle-stage hooks (Phase A, Task A1 — delivered)

Classic `grain{}` had `onLifecycleStage n` (arbitrary int, hook shape
`CancellationToken -> Task<unit>`); functional exposed only
`onActivate`/`onDeactivate`. Delivered: `onLifecycle stage hook` on
`grainFor`, for the closed set of documented Orleans stages
(`src/Orleans.FSharp/FunctionalContext.fs`):

```fsharp
type LifecycleStage = First | SetupState | Activate | Last
type LifecycleHook<'Actor, 'Key> = FunctionalGrainContext<'Actor, 'Key> -> Task<unit>
```

**Resolved design — activation ordering (verified by an integration probe
subscribed directly at the raw Orleans stage, `tests/Orleans.FSharp.Integration/FunctionalPlacementIntegrationTests.fs`,
not assumed from the stage names):**

```
CreateInstance (activator): facets created (not yet loaded)
  │
  ├─ GrainLifecycleStage.First       (int.MinValue) — onLifecycle First
  ├─ GrainLifecycleStage.SetupState  (1000)         — persistent-state facets load; onLifecycle SetupState
  ├─ GrainLifecycleStage.Activate    (2000)         — onLifecycle rejects this stage; nothing runs here for functional grains
  ├─ GrainLifecycleStage.Last        (int.MaxValue) — onLifecycle Last
  │    (the four stages above are ONE sequence, run to completion in ascending
  │     numeric order by Orleans' ObservableLifecycle.OnStart, before anything below)
  └─ OnActivateAsync (a separate step, not itself gated by any single stage number):
       1. env.State.Initialize — ephemeral or facet-backed primary state now exists
       2. onActivate hook (in-memory-only publication)
       3. reminders reconciled (RegisterOrUpdateReminder, in declaration order)
       4. timers created
```

The obvious-looking guess going in — "First/SetupState clearly precede
state init; Last is the final stage, so it must run after `onActivate`" —
is **wrong**, and the probe is what caught it: `Last` completes *before*
`OnActivateAsync` starts, not after. All three accepted stages
(`First`/`SetupState`/`Last`) are equally pre-state; there is no "post-state"
stage among the four.

**`Activate` vs `onActivate`:** `onLifecycle Activate` is **rejected** at
sealing with a diagnostic pointing at `onActivate`. Not because the two
coincide in time (they don't — see above), but to avoid a footgun: letting
an application aim at the stage literally named "Activate" while state is
not yet initialized there would be confusing, and "the operation for
activation-time behavior" should have exactly one name.

**State interplay — resolved:** every accepted stage runs before
`OnActivateAsync`, so `'State` cannot be meaningful at any of them, not only
the ones an initial read might guess are "early." `onLifecycle` hooks
therefore carry **no** `'State` parameter at all, uniformly — not a
per-stage-typed shape. This also sidesteps an F# mechanics constraint: a
single `[<CustomOperation>]` cannot vary a hook's type by which DU case
(stage) was passed at the call site without either a second, differently
named operation (which would itself violate the "no two ways to say one
thing" rule already applied to `Activate`) or a runtime `'State option` that
is always `None` for stages that can never populate it. A hook that
genuinely needs to read a stored value can still do so explicitly through
`context.persistentState`.

**Sealing:** each stage accepts at most one hook (checked inline at the
`onLifecycle` custom-operation call site, matching `onActivate`/`stateFrom`'s
"reject a repeated singleton" idiom). Confirmed by mutation-checked tests
(`tests/Orleans.FSharp.Tests/FunctionalDefinitionTests.fs`).

**Size:** S, as estimated.

### 8b. Scripting functional hosting (Phase A, Task A1 — delivered)

**`Scripting.startOnPorts` could not host functional registrations** (proven
in spec-003 Task 11 by reading and by run). Delivered:
`Orleans.FSharp.Runtime.FunctionalScripting.startOnPorts` — a separate
module rather than an overload of `Scripting.startOnPorts` itself, because
`AddFunctionalGrain` and `FunctionalGrainDefinition` live one layer above
`Orleans.FSharp` (in `Orleans.FSharp.Runtime`), which cannot depend back on
it. It reuses `Scripting`'s own host-building core
(`Scripting.startOnPortsWith`, `internal`, reachable through this project's
existing `InternalsVisibleTo` grant) rather than duplicating the
localhost-clustering / memory-storage / memory-streams recipe, plus the
standalone-host manifest pre-load (`SiloConfig.manifestAssemblies`, spec-003
E1/Task-13), and returns the same `Scripting.SiloHandle` so
`Scripting.getGrain`/`Scripting.shutdown` work unchanged against it.
Definitions are boxed for a heterogeneous list with
`FunctionalGrainRegistration.of'`, erasing the four `FunctionalGrainDefinition`
type parameters into one registration closure applied via `AddFunctionalGrain`
inside the shared builder callback. `samples/quickstart-functional.fsx` no
longer hosts its own silo — it calls `FunctionalScripting.startOnPorts` and
was run end to end against the local build (transcript in the Task A1
report).

**Size:** S, as estimated.

## 9. C#-callable facade

**Gap (compiler-verified above):** calling functional grains from C# works
but is non-idiomatic (`.Invoke` chains, `Module`-suffixed names,
`FSharpResult` handling) and requires knowing to pin FSharp.Core.

**Design sketch:**

- A generated-at-bind C# view: `FunctionalGrain.forCSharp(contract, factory, key)`
  returning an object whose members are ordinary `Task<TReply> Op(TArg arg)`
  delegates (precedent: the old model's `GrainContext.forCSharp`);
  `FSharpResult` replies get `TryOk(out T)` helpers or are left to the
  consumer with documented patterns.
- A short "Calling from C#" docs page: assembly references, the FSharp.Core
  pin, record construction from C#, Result handling.
- Streaming replies (item 6) and observer handles must keep C#-consumable
  shapes (`IAsyncEnumerable<T>`, plain handle classes) — this item is the
  enforcement point for the C#-surface rule.

**Size:** S/M.

---

## Explicit non-goals

- Grain services / SystemTargets (not grains; the hosting CE keeps serving
  them).
- Custom storage/stream providers (orthogonal — `IGrainStorage`/stream
  providers already work unmodified).
- Popularity-based prioritization: the order above is cost/coupling judgment;
  the owner sets priorities.

## Suggested phasing

- **Phase A (S items):** 4 (placement), 8 (lifecycle + scripting), 9 (C# facade).
- **Phase B:** 1 (implicit subscriptions) — the manifest/extension seams are
  fresh from spec 003.
- **Phase C:** 5 (reentrancy), 7 (version tolerance) — both are admission-layer
  work and share tests.
- **Phase D:** 2 (transactions).
- **Phase E:** 3 (event sourcing) — after the provider-story decision.
- **Phase F:** 6 (streaming replies) — largest; consider splitting into its
  own spec once A-C land.

Each phase repeats the spec-003 discipline: seam proofs before runtime
layers, both-Orleans-versions matrix, mutation-checked guards, examples with
visible parity mapping, docs updated with old pages kept.
