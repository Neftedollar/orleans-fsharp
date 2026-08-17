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

**Gap:** composition works today (feature tour §10 applies
`StatelessWorkerAttribute` through an application `IGrainPropertiesProvider`;
measured: 8 concurrent calls → 4 activations), but there is no contract/
definition surface.

**Design sketch:**

- Definition operations: `statelessWorker maxLocalWorkers`,
  `placement PlacementStrategy.PreferLocal` (closed set mirroring Orleans'
  strategies), publishing the placement properties from the registry's
  provider — the exact mechanism the tour proved.
- Sealing: `statelessWorker` rejects `stateFrom`, `usePersistentState`, and
  `onReminder` (durable identity is meaningless for multiplexed local
  activations) and rejects `collectionAge` overrides Orleans ignores for
  stateless workers.

**Size:** S/M. **Depends on:** nothing.

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

- **Lifecycle-stage hooks:** classic `grain{}` had `onLifecycleStage n`;
  functional exposes only `onActivate`/`onDeactivate`. Add
  `onLifecycle stage hook` for the closed set of documented Orleans stages
  (First/SetupState/Activate/Last) — not arbitrary ints. **S**
- **`Scripting.startOnPorts` cannot host functional registrations** (proven
  in spec-003 Task 11 by reading and by run): add a
  `functionalGrain definition` hook to the scripting silo builder so
  `quickstart-functional.fsx` stops hosting its own silo. **S**

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
