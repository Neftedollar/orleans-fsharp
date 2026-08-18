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

## 1. Implicit stream subscriptions (and implicit broadcast consumers) — RESOLVED

**Status: implemented.** Delivered on `feat/004-parity-phase-b`; the design sketch below has
been replaced by the resolved design it turned into, and every claim here is backed by a test or
by named Orleans source.

**Gap (proven, spec-003 feature tour §11), for the record:** two-part wall. (a) The functional
manifest published no stream-binding grain properties, and (b) even with the bindings forced in,
the functional activation did not participate in `IStreamConsumerExtension` delivery — Orleans
activated the grain, then dropped the item ("… I don't have any subscriber for that stream.
Dropping on the floor.").

### The mechanism (normative)

Both halves are Orleans' own seams; neither needs code generation.

- **Binding.** `ImplicitStreamSubscriptionAttribute` / `ImplicitChannelSubscriptionAttribute`
  implement `IGrainBindingsProviderAttribute`, and Orleans' `AttributeGrainBindingsProvider`
  (an `IGrainPropertiesProvider`) writes each returned dictionary into the grain manifest under
  `binding.attr-<n>.<key>`, `n` 1-based. `GrainBindingsResolver` regroups those keys by the token
  between `binding.` and the next `.`, and `ImplicitStreamSubscriberTable` /
  `ImplicitChannelSubscriberTable` read `type`, `pattern` / `channel-pattern`, and
  `streamid-mapper` / `channelid-mapper` out of each group. The registry's own properties
  provider therefore **constructs the real Orleans attribute and writes its own `GetBindings`
  output** under those keys — not a transcription of it.
- **Target selection.** `DefaultStreamIdMapper.GetGrainKeyId` returns `streamId.GetKeyIdSpan()`
  verbatim unless the binding carries `legacy-grain-key-type`, which it only does for a grain
  class implementing `IGrainWithGuidKey` / `IGrainWithIntegerKey` / `IGrainWithStringKey` /
  their compound forms. `FunctionalGrainMarker<'Actor>` implements none of them, so the target is
  `GrainId.Create(grainType, <stream key bytes>)` — i.e. the stream key IS the functional grain
  key, in the contract's own key encoding.
- **Delivery.** `StreamConsumerGrainContextAction.Configure` installs a `StreamConsumerExtension`
  on any activation whose **grain instance** implements `IStreamSubscriptionObserver`, and the
  keyed `IGrainExtension` factory registered by `AddSiloStreaming` does the same cast on demand.
  When an item arrives for a subscription the extension has no observer for — always the case for
  an implicit subscription on a fresh activation — `StreamConsumerExtension.DeliverMutable` /
  `DeliverBatch` call `OnSubscribed(handleFactory)`, then **re-check the observer table and
  deliver the pending item**. Attaching a typed observer from inside `OnSubscribed`
  (`handleFactory.Create<'Item>().ResumeAsync observer`) is exactly what turns "activated, then
  dropped on the floor" into a delivery. Broadcast channels use the identical shape:
  `BroadcastChannelConsumerExtension` casts the grain instance to `IOnBroadcastChannelSubscribed`
  and calls `OnSubscribed(subscription)`, where `subscription.Attach<'Item>` registers the hook.

### Resolved design

- Two definition operations, declarative like `onTimer` / `onReminder`:

  ```fsharp
  onStream    "provider" "namespace" (fun context state (item: 'Item) -> task { ... return state' })
  onBroadcast "provider" "namespace" (fun context state (item: 'Item) -> task { ... return state' })
  ```

  The item type is inferred from the hook and erased into two preclosed delegates at declaration
  time, so no silo-side code closes a generic per delivery. **Deviation from the sketch:**
  `onBroadcast` takes a channel *namespace*, not a channel id — Orleans' implicit channel binding
  is a namespace predicate, and the channel *key* is what selects the activation, exactly as the
  stream key does.

- Sealing freezes the declarations into definition metadata and validates: non-blank provider and
  namespace; at most one hook per `(transport, provider, namespace)` triple (the same namespace on
  two providers is two different streams and is allowed); and **`statelessWorker` is rejected with
  either operation** — `SiloStreamProviderRuntime.BindExtension` throws "The extension … cannot be
  bound to a Stateless Worker", and implicit delivery addresses one activation identity derived
  from the stream key, which multiplexed local activations cannot honor.

- Silo startup validation requires every named provider to resolve as a keyed `IStreamProvider` /
  `IBroadcastChannelProvider`, exactly like the storage-provider check. Without it an unregistered
  provider is silent: the binding is still published, Orleans still activates the grain, and the
  item is simply dropped.

- The activator installs the two Orleans-facing interfaces on a **separate activation-target
  class**, used only when the definition declares at least one implicit subscription. This is
  load-bearing: `StreamConsumerGrainContextAction` eagerly binds the extension to every activation
  implementing `IStreamSubscriptionObserver`, and `BindExtension` throws for a stateless worker, so
  an unconditional implementation would fail the activation of every stateless-worker functional
  grain on a silo with streaming configured.

- The manifest publication de-duplicates by `(transport, namespace)`: Orleans' binding names no
  provider, so two declarations of one namespace on two providers would otherwise publish two
  byte-identical binding groups.

- **Added during implementation:** `FunctionalGrain.streamId contract ns key` and
  `FunctionalGrain.channelId contract ns key`, because the producer side turned out to carry a
  silent trap. Orleans routes an implicit delivery to `GrainId.Create(grainType, streamId.Key)` —
  the stream key bytes verbatim — so the stream key must be the grain key in the *contract's own*
  encoding. `stringKey` and `guidKey` agree with `StreamId.Create(ns, key)`; **`int64Key` does
  not** (`StreamId.Create(ns, 42L)` writes decimal `"42"`, while `GrainIdKeyExtensions.CreateIntegerKey`,
  which the codec uses, writes hexadecimal `"2A"`), and the compound codecs have no
  `StreamId.Create` overload at all. Building the id from the contract makes drift impossible; an
  integration test publishes both ways to an `int64Key` definition and shows the naive publish
  landing on the grain whose key reads as `0x42` = 66.

### Delivery semantics (normative)

The timer-hook rules apply unchanged:

- a delivery is an ordinary **non-reentrant** grain call — `IStreamConsumerExtension`'s delivery
  methods carry no `[AlwaysInterleave]`, so this is Orleans' scheduling, not a setting of ours;
- **whole-state replacement**, published **only on a successful return**;
- **no implicit storage write** by the runtime;
- the context cancellation token is `CancellationToken.None`, because neither
  `IAsyncObserver.OnNextAsync` nor `IBroadcastChannelSubscription.Attach` supplies one.

**Open question resolved — `StreamSequenceToken` exposure.** Exposed, read-only, on the invocation
context as `context.streamSequenceToken : StreamSequenceToken option`, rather than as a fourth hook
parameter. Justification: the sketch's hook shape (`context -> state -> item`) is binding, and a
fourth parameter would be `null` for every non-rewindable provider and for every `onBroadcast`
delivery, i.e. noise in the common case; the context is where every other per-callback fact
(`cancellationToken`, `utcNow`) already lives, and `option` makes "this provider has no cursor"
a total, non-null answer. **The runtime never rewinds with it**: the implicit subscription is
resumed with no token, so a fresh activation starts at the subscription's current position. The
token is exposed so an application can checkpoint or de-duplicate — which matters because delivery
is at-least-once. Consuming a stored token to rewind on activation is a follow-up, not this item.

**Open question resolved — the delivery context token.** `CancellationToken.None`, for the same
reason the reminder hook uses it: the Orleans delivery path supplies no token, and a token that
can never be cancelled but *can* be disposed underneath a registered continuation would be worse
than none.

**Failure semantics (verified by test, not assumed).** A throwing hook propagates back through
`StreamConsumerExtension` to `PersistentStreamPullingAgent`, which retries the same item through
`AsyncExecutorWithRetries.ExecuteWithRetries` with `INFINITE_RETRIES` bounded by
`StreamPullingAgentOptions.MaxEventDeliveryTime` (30 s default) and the delivery backoff provider.
When the budget is spent, `ErrorProtocol` delivers the error to the consumer and records a
delivery failure, but **never faults an implicit subscription** — it excludes them explicitly
(`&& !SubscriptionMarker.IsImplicitSubscription(...)`) — so the cursor advances and later items
still arrive. Delivery is therefore **at-least-once**, and the integration test observes ≥ 2 hook
entries for one item plus the next item arriving afterwards. The state of a failed attempt is
never published.

**Broadcast failure semantics.** There is no pulling agent, so nothing is retried: a broadcast
publish is a direct fan-out grain call. `BroadcastChannelOptions.FireAndForgetDelivery` defaults to
**`true`**, so a throwing hook is logged at `Error` by `BroadcastChannelWriter.PublishToSubscriber`
and the publisher's `Publish` still completes; with `FireAndForgetDelivery = false` that method
rethrows and `Publish` faults with an `AggregateException` carrying the hook's exception.

**An item of the wrong runtime type is made to take that same path, and this needed a deliberate
choice.** `BroadcastChannelConsumerExtension.Callback<T>.OnPublished` routes a mismatch into the
subscription's *error* callback as an `InvalidCastException` naming both types — never into the
`onPublished` one. Completing that callback successfully (the obvious implementation) lets the
extension proceed to `EmitItemDelivered` and lets an awaited `Publish` report success, so the item
disappears with **no fault and no log anywhere** — strictly worse than the stream side, where the
same mismatch at least throws and is retried. The runtime therefore returns
`Task.FromException error` from that callback, which propagates through `PublishToSubscriber` and
so is logged in the default mode and thrown to the publisher in the awaited one. The hook is not
entered, no state is published, and the subscription stays healthy. Both modes are pinned by
integration tests, and the fix is mutation-checked: with the callback restored to
`Task.CompletedTask` the fire-and-forget test times out waiting for a log that never arrives and
the awaited test observes no exception at all.

**One asymmetry.** Orleans' binding names a namespace but not a provider, so an item published to
a declared namespace on an *undeclared* provider still routes to this grain type. The runtime
matches on `(provider, namespace)`, logs a warning, and leaves the item undelivered (Orleans then
drops it) — throwing would poison a pulling agent over an item a legitimately configured different
provider delivered.

### Out of scope, recorded as follow-ups

- **Batch delivery (`IAsyncBatchObserver`)** — a hook receives one item at a time. Adding it means
  a second hook shape (`'Item list` plus per-item tokens) and its own back-pressure story.
- **Rewind on activation** — accepting a stored `StreamSequenceToken` back and passing it to
  `ResumeAsync`, so an activation replays from a checkpoint instead of the current position.
- **Regex / custom namespace predicates** (`RegexImplicitStreamSubscriptionAttribute`,
  `IStreamNamespacePredicate`) — only exact-match namespaces are declarable today.
- **Custom `IStreamIdMapper` / `IChannelIdMapper`** — the binding always publishes the default
  mapper (a null `streamid-mapper`, which is what an undecorated attribute publishes too).
- **A wrong-typed item on the STREAM side stays documented-only.** Orleans casts inside
  `StreamSubscriptionHandleImpl.DeliverItem` and throws `InvalidCastException`, which then rides
  the ordinary retry-then-drop path; observing the "then drop" half costs a full
  `MaxEventDeliveryTime` per run, which is not worth the CI time. The broadcast half — where the
  failure was silent rather than merely slow — is tested in both delivery modes.

**Size:** M/L (as estimated). **Depended on:** nothing new.

## 2. Distributed ACID transactions — RESOLVED

**Status: implemented.** Delivered on `feat/004-parity-phase-d`; the design sketch below has been
replaced by the resolved design it turned into, and every claim here is backed by a test or by
named Orleans source.

**Gap (for the record):** the classic KEEP-path (`AddFSharpTransactionalGrain`,
`FSharpTransactionalGrain<'State>`) existed and still does; the functional model had no
transactional state and no way to declare a per-operation transaction policy.

### The mechanism (normative)

All of it is Orleans' own, at both ends. The reason this item was the deepest in the spec is not
that the machinery is hidden — it is that it lives in a place the functional transport had already
built its own version of.

**The transaction rides the invokable, not the message.** `[Transaction(option)]` carries
`[InvokableBaseType(typeof(GrainReference), typeof(Task<>), typeof(TransactionTaskRequest<>))]`
(and three sibling declarations for `Task`/`ValueTask`/`ValueTask<>`), plus
`[InvokableCustomInitializer("SetTransactionOptions")]`
(`src/Orleans.Transactions/TransactionAttribute.cs`). Orleans' code generator therefore emits, for
an attributed method, an invokable whose **base class** is `TransactionRequest`/
`TransactionRequest<T>`/`TransactionTaskRequest`/`TransactionTaskRequest<T>` instead of the plain
`Request`/`Request<T>`, and calls `SetTransactionOptions(option)` from its constructor. Verified
by generating the code: a probe project with three `[Transaction]` methods emits
`Invokable_IAccount_GrainReference_F65AF134 : global::Orleans.TransactionTaskRequest` with
`SetTransactionOptions(global::Orleans.TransactionOption.CreateOrJoin)` in its constructor.

Everything else follows from that base class, `TransactionRequestBase`:

- **Caller side.** It implements `IOutgoingGrainCallFilter`, and
  `OutgoingCallInvoker`'s constructor does `if (request is IOutgoingGrainCallFilter requestFilter)`
  and runs the request itself as the last stage of the outgoing pipeline
  (`src/Orleans.Core/Runtime/OutgoingCallInvoker.cs`). `GrainReferenceRuntime.InvokeMethodAsync`
  routes through that pipeline whenever `request is IOutgoingGrainCallFilter`, even with no
  application filters registered. The stage reads the ambient `TransactionInfo`, enforces `Join`
  and `NotAllowed`, clears the context for `Create`/`Suppress`, **forks** the info into the
  request, and on return joins the `TransactionInfo` the reply carried back.
- **Target side.** It overrides `IInvokable.Invoke()` — the method the activation's message loop
  calls. It starts a transaction when `IsTransactionRequired` and none arrived
  (`ITransactionAgent.StartTransaction`, read-only when the request carries
  `InvokeMethodOptions.ReadOnly`), sets `TransactionContext` (an `AsyncLocal<TransactionInfo>`)
  around `BaseInvoke()`, and afterwards resolves or aborts the transaction it started and wraps the
  reply in a `TransactionResponse` carrying the participant set.

**The facets are a separate, differently-shaped seam.**
`ITransactionalStateFactory.Create<TState>(TransactionalStateConfiguration)` takes **no**
`IGrainContext` — unlike `IPersistentStateFactory.Create<TState>(context, configuration)` — and
resolves the activation from `IGrainContextAccessor.GrainContext`, which is `RuntimeContext.Current`
(`src/Orleans.Runtime/Activation/GrainContextAccessor.cs`). The created state subscribes to
`GrainLifecycleStage.SetupState` from inside `Create` (`TransactionalStateFactory.Create` calls
`state.Participate(context.ObservableLifecycle)`), so it has to exist before the lifecycle starts.
Both conditions hold exactly where the functional runtime already builds persistent facets —
`IGrainActivator.CreateInstance`, which `ActivationData.Start` runs as a work item on the
activation's own scheduler, and `WorkItemGroup.Execute` wraps every work item in
`RuntimeContext.SetExecutionContext(GrainContext)`.

**The option set is stable across the supported range.** `Orleans.TransactionOption` has the same
six members with the same values on 10.1.0 and 10.2.2 (`Suppress=0, CreateOrJoin=1, Create=2,
Join=3, Supported=4, NotAllowed=5`), and `TransactionRequestBase` has the same shape on both,
including the `UseExclusiveLock` property that the checked-in 10.1.0 API baseline
(`src/api/Orleans.Transactions/Orleans.Transactions.cs`) does not list — verified by reflection
over both `Orleans.Transactions.dll` assemblies, not from the baseline file.

### Resolved design

- **`FunctionalTransactionRequest : TransactionRequest<FunctionalReply>`** — a second invokable
  shell for the fixed transport, used only for operations that declare a policy. Everything that
  is not "which base class" moved into a shared `FunctionalRequestBody`, so the two shells cannot
  drift. Its codec delegates the base segment to Orleans' generated
  `IBaseCodec<TransactionRequestBase>` (resolved through `ICodecProvider.GetBaseCodec`, not by
  generated type name) and hand-writes only the one derived field, the envelope; its copier
  delegates to `IBaseCopier<TransactionRequestBase>` so a same-silo participant joins the
  transaction on the local, copy-only path too.
- **The option travels in the admission byte.** Bits 3-5 carry the `TransactionOption` value plus
  one (`0` = not transactional, `7` unassigned); bits 6-7 stay reserved. Dispatch already compares
  the whole admission byte against the hosted descriptor as step 3 of spec-003's normative
  validation order, so a caller and a host that disagree about whether an operation is
  transactional — or about which option it uses — produce a rejected request instead of a silently
  non-transactional call. The receiving side derives the option from that byte rather than trusting
  the copy Orleans' base codec also carries.
- **`transactional Orleans.TransactionOption.X (_.op)`** on the contract, using Orleans' own enum
  rather than a mirrored F# union: it is already the closed set, its values are identical across the
  supported range, and the admission byte encodes the value directly, so there is no mapping to
  drift. (`Orleans.FSharp.Transactions.TransactionOption`, the classic path's union, keeps its
  simple name; the collision is documented.)
- **`transactionalStateFrom (TransactionalState.create<'S> "name" "storage") initializer`** on the
  definition, mirroring `usePersistentState`, plus `context.transactionalState descriptor` returning
  an invocation-bound facade with `read` / `readWith` / `update` / `updateWith`.
- **`FunctionalTransactionalBox<'S>`** — the runtime's own `class, new()` holder, and the reason an
  ordinary immutable F# record can be transactional state at all. See ruling 1.
- **Three registrations Orleans' defaults cannot supply**, added per attached facet at
  `AddFunctionalGrain`: an exact-type `ITransactionDataCopier` for the box and for the stored value,
  and the stored type's declaration as a top-level payload type. See ruling 4.

### Rulings

1. **Open question resolved: state-in/state-out, made possible by a runtime-owned box — not
   `PerformRead`/`PerformUpdate` lambdas over the application's own type.** Orleans constrains
   `ITransactionalState<TState>` to `TState : class, new()` and applies an update by **mutating the
   instance it stores**: `PerformUpdate` hands the callback `record.State` and keeps whatever that
   object looks like when the callback returns (`src/Orleans.Transactions/State/TransactionalState.cs`).
   An F# record satisfies neither half. The classic KEEP-path's answer is visible in this repository
   — `TransactionalGrainDefinition` carries a `CopyState: 'State -> 'State -> unit` field whose only
   job is to copy a computed new state field-by-field into the instance Orleans owns. The functional
   runtime instead stores a `FunctionalTransactionalBox<'S>`, which is the `class, new()` instance
   Orleans mutates, and hands application code `'State -> 'State`. The mutation is one reference
   assignment the runtime performs. This is not only more idiomatic, it is **enforcement**: an
   application function that never receives the stored object cannot mutate transactional state in
   place.

   All four facade members take **synchronous** functions, also by necessity rather than taste:
   Orleans runs them inside the transactional state's reader-writer lock and throws
   `LockRecursionException` when the same state is re-entered from within one, so a function that
   cannot be awaited cannot call another grain, another transactional state, or any I/O from inside
   that lock.

2. **Open question resolved: Orleans does not re-execute.** The sketch's premise — "transactions
   re-execute, so handlers must be pure over the transactional read" — is false as stated, and the
   normative text says so. There is no retry loop in `TransactionRequestBase.Invoke`, and
   `ReaderWriterLock.EnterLock` builds a single `completion()` closure that either sets the result
   of one `TaskCompletionSource` or its exception, so a read or update callback runs **exactly
   once** (`src/Orleans.Transactions/State/ReaderWriterLock.cs`). On a participant fault, a lock
   timeout, or a failed commit, the transaction aborts and the caller receives an
   `OrleansTransactionException`. Measured rather than asserted: an aborted transfer enters each
   participant's handler exactly once and never again, and the counter is shown to be live by an
   application-driven retry moving it to two. The normative consequences — retry is the caller's
   decision, "exactly once" holds per attempt and not across retries, and effects outside
   transactional state are not covered — are in
   `docs/functional-grains.md`, "Re-execution semantics (normative)".

3. **A transaction-scoped operation is state-neutral for everything except its transactional
   facets.** "Transaction-scoped" is exactly `Create`, `CreateOrJoin`, `Join` — the three for which
   Orleans' own `TransactionRequestBase.IsTransactionRequired` is true. In such an operation the
   handler's replacement primary state is **discarded** exactly as a `readOnly` handler's is, and
   its persistent-state facades reject the `State` setter and every storage call with a diagnostic
   naming the reason. Neither an in-memory publication nor a storage write has any participant that
   could undo it, so allowing either would let one aborted transaction leave the activation
   half-updated — and a caller that retried would then apply the non-transactional half twice.
   `Supported`, `Suppress`, and `NotAllowed` are **not** transaction-scoped: Orleans starts no
   transaction for them, so state publication and persistent facets behave exactly as for any other
   operation. This is the rule that makes the two kinds of facet safe to coexist, which the sketch
   asked for.

4. **The functional runtime had to supply three things Orleans' transaction defaults assume.** All
   three were found by the probe, none is optional, and each is registered per attached facet so it
   cannot be forgotten:
   - `DefaultTransactionDataCopier<TState>` asks the Orleans serializer for a
     `DeepCopier<TState>`, which for the box needs one for the application's stored type. The
     functional runtime deliberately registers the F# generalized codec **without** its generalized
     copier (payloads cross an explicit byte boundary instead), so an ordinary F# record has no
     Orleans copier at all. The facet registers its own exact-type `ITransactionDataCopier`, which
     Microsoft.Extensions.DependencyInjection prefers over Orleans' open-generic default because an
     exact closed service type matches before an open generic one.
   - `TransactionalState.CopyResult<TResult>` resolves a **required**
     `ITransactionDataCopier<TResult>` for whatever the callback returned, so an arbitrary
     application result type would make every projection depend on a copier registration it cannot
     have. The facade therefore keeps `TResult` to exactly two shapes: the stored type for `read()`,
     whose result *is* the stored value and so must be isolated from it, and `bool` for every other
     member, whose application-facing result is carried out of the callback in a captured cell —
     sound precisely because the callback runs exactly once (ruling 2).
   - Exact-type serialization makes Orleans elide the field type, so the F# binary codec resolves a
     top-level payload type by name. Silo startup now declares transactional stored types the same
     way it already declares argument, reply, and persistent stored types; without it a state type
     from an application assembly serializes and then fails to deserialize on the way back out of
     the snapshot copy.

5. **Sealing and startup rejections.** Contract: `transactional` twice on one operation, an
   undefined enum value, `transactional` with `oneWay` (no reply means the participants a call
   enlisted are never reported back), and `transactional` with `alwaysInterleave` (Orleans admits an
   always-interleave request before any interleaving policy is consulted, so two turns of one
   activation could hold transactional locks on the same states). `readOnly` **composes**, and makes
   the started transaction read-only. Definition: duplicate transactional state names; a
   transactional facet no operation could reach (every callback other than `Create`/`CreateOrJoin`/
   `Join`/`Supported` runs without a `TransactionContext`, which Orleans requires for both facet
   members); `transactionalStateFrom` on a derived `grainType`; `transactionalStateFrom` with
   `statelessWorker`. A `transactional` operation with **no** transactional facet is accepted — a
   state-free participant is the orchestrator shape, and this repository's own classic
   `FSharpAtmGrain` is an instance of it. Silo startup: no `UseTransactions()`, and a transactional
   storage name that resolves to neither a keyed `ITransactionalStateStorageFactory` nor a keyed
   `IGrainStorage`, which is the exact order `NamedTransactionalStateStorageFactory.Create` tries.
   All twelve guards are mutation-checked.

6. **The client needs nothing beyond `AddFunctionalGrainClient()`.** `TransactionRequestBase` reads
   the transaction agent only inside `Invoke()`, which runs on the target, so a `Create` or
   `CreateOrJoin` call from a client outside a transaction starts the transaction on the silo that
   receives it. `UseTransactions()` on the client builder is needed only when the client itself
   drives `ITransactionClient.RunTransaction`. Proven by the Phase D fixture, whose client builder
   calls only `AddFunctionalGrainClient()`.

### What this does NOT give you

- **No automatic retry.** Nothing in the library re-runs an aborted transaction; a retry is an
  application call, and every participant handler then runs again from the beginning.
- **No transactional primary state.** `stateFrom` and `usePersistentState` are not participants, and
  a transaction-scoped operation cannot write them at all (ruling 3). A grain whose durable state
  must be transactional puts it in a `transactionalStateFrom` facet.
- **No transactional timers, reminders, stream deliveries, or observer pushes.** None of them
  carries a `TransactionContext`, so the facade refuses the facet inside them by name rather than
  letting Orleans throw "did you forget a `[Transaction]` attribute?" about an attribute this API
  does not have.
- **No cross-cluster transactions**, because Orleans' transaction manager is per-cluster.
- **Catching a participant's exception does not un-abort the transaction.** The fault is recorded
  on the shared `TransactionInfo` by the participant's own `Invoke()`, and the caller's outgoing
  filter joins that info on return, so `MustAbort` dooms the transaction whatever the intermediate
  handler did with the exception. Pinned by a test whose handler catches and still fails.
- **The admission byte changed.** Bits 3-5 were reserved before this item; an older library rejects
  a transactional call as a reserved-bit violation. Non-transactional traffic is byte-identical, so
  the incompatibility is confined to operations that did not exist before.
- **The snapshot copy is a serializer round trip.** Before the first write of each transaction the
  runtime copies the stored value through the exact-type payload codec: one serialize plus one
  deserialize per transaction per written state. Orleans' own default also walks the whole graph, so
  the order of cost is the same, but the extra byte buffer is real. The alternative — sharing the
  value and relying on `'State -> 'State` never mutating it — was rejected because an application
  that mutates its own state object in place would then corrupt the version an abort has to restore.
- **`UseExclusiveLock` is not exposed.** Orleans' `[UseExclusiveLock]` sets a third field on the
  transaction request; the functional contract has no operation for it, so every functional
  transaction uses the ordinary read/write locking. Adding it later is one admission bit and one
  contract operation, and it is not a wire-breaking change for callers that do not use it.
- **No `TransactionOptionAlias`.** Orleans' alias enum maps onto the same six values with different
  names (and one of them, `Suppress`, maps to a *different* option than the identically named member
  of `TransactionOption`); exposing both would be a trap, and the aliases add nothing the six
  canonical names do not say.

**Size:** L — as estimated. The transaction plumbing itself was reachable in a day once the
invokable-base mechanism was identified; the three registrations of ruling 4 and the re-execution
question were the rest of it.

## 3. Event sourcing — RESOLVED

**Status: implemented.** Delivered on `feat/004-parity-phase-e`; the design sketch below has been
replaced by the resolved design it turned into, and every claim here is backed by a test or by
named Orleans source at both supported versions (`v10.1.0`, `v10.2.2`).

**Gap (for the record):** the classic KEEP-path (`eventSourcedGrain { }`,
`FSharpEventSourcedGrainImpl : JournaledGrain<...>`) existed and still does; the functional model
had no event-sourced definition kind.

**Provider story (owner ruling 2026-08-17, unchanged):** Orleans' own log-consistency providers
(`LogStorage`/`StateStorage`) are the base; Marten would ship as a separate optional adapter
package. See ruling 8 for what the existing `Orleans.FSharp.EventSourcing.Marten` package turned
out to contain.

### The mechanism (normative)

All of it is Orleans' own, and **none of it is internal.** The step-0 probe
(`tests/Orleans.FSharp.Integration/FunctionalJournalSeamProbe.fs`) drives it from an activation
that derives from neither `JournaledGrain` nor `LogConsistentGrain`, on both providers and both
Orleans versions.

**The adaptor is installed by the grain, not by Orleans.** `LogConsistentGrain<TView>.OnSetupState`
(`src/Orleans.EventSourcing/LogConsistency/LogConsistentGrain.cs`) does exactly four things at
`GrainLifecycleStage.SetupState`, and every one of them is reachable from anywhere:

1. resolve the log-consistency provider as a **keyed** `ILogViewAdaptorFactory`
   (`GetKeyedService<ILogViewAdaptorFactory>(name)`);
2. resolve `Factory<IGrainContext, ILogConsistencyProtocolServices>` from the service provider and
   invoke it for this activation's grain context. The implementation behind that delegate
   (`Orleans.Runtime.LogConsistency.ProtocolServices`) **is** internal, and is never named: it is
   registered by `LogConsistencyProtocolSiloBuilderExtensions.AddLogConsistencyProtocolServicesFactory`,
   which every stock `Add*BasedLogConsistencyProvider` call performs;
3. resolve the `IGrainStorage` the provider writes through;
4. call the public `ILogViewAdaptorFactory.MakeLogViewAdaptor<TView, TEntry>(host, initialState,
   grainTypeName, storage, services)`.

The grain then drives the adaptor through three lifecycle points — `PreOnActivate` at
`Activate - 1`, `PostOnActivate` at `Activate + 1`, `PostOnDeactivate` on the stop side of
`SetupState`. `ILogConsistencyProtocolParticipant` is **not** required: in Orleans 10 it is a
purely local marker `LogConsistentGrain` uses to decide whether to subscribe those two stages, and
a repository-wide search finds it referenced only by `LogConsistentGrain` and `JournaledGrain`.

**Four mechanism facts no documentation states**, each pinned by the probe:

- **The view is deep-copied through the Orleans serializer.**
  `PrimaryBasedLogViewAdaptor`'s constructor does `Services.DeepCopy(initialstate)`, and
  `CalculateTentativeState` copies the confirmed view again. The functional runtime registers the
  F# generalized codec **without** its generalized copier, so an ordinary F# view type fails with
  `CodecNotFoundException: Could not find a copier`.
- **The two providers disagree about the seeded view.** `LogStorage`'s adaptor folds into the very
  instance it was handed (`UpdateConfirmedView` calls `Host.UpdateView(ConfirmedViewInternal, …)`),
  so a key-derived initial state survives. `StateStorage`'s `ReadAsync` reads into a fresh
  `GrainStateWithMetaDataAndETag<TView>()`, whose constructor does `State = new TView()`, so the
  seed is **discarded** on the first read of a grain with no record.
- **`PostOnActivate` does not await the replay.** It only calls `worker.Notify()`; the initial read
  runs on the adaptor's batch worker afterwards. A `JournaledGrain` can therefore serve a call
  against a view that has not been read yet.
- **A failing fold is swallowed.** Both adaptors call `Host.UpdateView` inside a `try/catch` that
  logs through `CaughtUserCodeException` and continues with an unchanged view — and they call it
  *after* the storage write that made the entry durable, so a fold that throws leaves a permanently
  poisoned journal.

### Resolved design

```fsharp
journaledGrainFor accountContract {
    initialEventState (fun key -> { balance = 0m })
    apply (fun state event -> …)              // pure; 'State -> 'Event -> 'State

    logProvider "LogStorage"                  // required
    journalStorage "Journals"                 // optional; silo default otherwise

    handle (_.deposit) (fun context state amount ->
        task { return [ Deposited amount ], state.balance + amount })
}
```

- **A second definition kind, one contract layer.** `journaledGrainFor` produces a
  `FunctionalJournaledGrainDefinition`, registered with `AddFunctionalJournaledGrain`, and hosted
  through the **same** `FunctionalHostedDefinition`, registry, manifest, activator, transport and
  dispatch as `grainFor`. The contract, the client binding and the C# facade are untouched, and the
  definition kind is invisible to a caller.
- **`initialEventState` then `apply`, in that order**, are the first two operations and both are
  required: the first introduces `'State`, the second `'Event`, so every later operation is typed
  against both.
- **Handlers raise, never replace.** `JournaledHandler` is
  `context -> 'State -> 'Argument -> Task<'Event list * 'Reply>`. Dispatch treats the adapter's
  first result as the boxed event list rather than a replacement state
  (`src/Orleans.FSharp.Runtime/FunctionalDispatch.fs`).
- **The view and the entries are cells of exact-type payload bytes.**
  `FunctionalJournalView { byte[] Payload; bool HasValue }` and
  `FunctionalJournalEntry { byte[] Payload }` (`src/Orleans.FSharp.Abstractions/FunctionalJournal.cs`,
  both `[GenerateSerializer]`). This is what answers the deep-copy fact — Orleans' own generated
  copier handles `byte[]` plus `bool` with no registration at all — and it keeps CLR type names out
  of durable storage, the same byte boundary the transport puts between a caller and a handler.
  `HasValue` answers the seed fact: the runtime re-materializes the declared initial state for any
  cell that was never written, so the two providers agree.
- **The replay is forced.** The activation awaits `adaptor.Synchronize()` at `Activate + 1`, so a
  handler is never handed a state read from an unreplayed view. This is a deliberate deviation from
  `JournaledGrain`, which does not.
- **The fold is dry-run before submission.** `RaiseAndConfirm` folds the events over the confirmed
  state before anything is submitted, so a failing `apply` fails the call with **nothing appended**
  instead of poisoning the journal. Sound because `apply` is required to be pure.
- **Startup validates the whole chain**: the named provider resolves, the protocol-services factory
  exists, the journal storage resolves, and the state and event types have codecs **and are declared
  as top-level payload types** — without the declaration a journal serializes but cannot be replayed,
  because the F# codec's fallback (`Type.GetType`) cannot see an application assembly.
- **Context surface:** `context.journalVersion` (Orleans' `ConfirmedVersion`, the same number
  `JournaledGrain.Version` reports) and `context.raiseConditional events : Task<bool>`
  (`TryAppendRange`). Both raise a definition-stage diagnostic on a non-journaled definition.

### Confirmation strategy (normative)

- The runtime appends a handler's returned events as **one atomic batch** (`SubmitRange`) and awaits
  `ConfirmSubmittedEntries` **after the handler returns and before the reply leaves the activation**.
  A caller that received a reply is looking at state the provider has confirmed; a later replay can
  never observe half of one handler's events.
- A handler observes the journal **as it was when the turn started**: `context.journalVersion` is the
  pre-turn version, and the `state` argument is the confirmed fold, never a tentative one.
- **An empty event list performs no storage write at all** — a query, or a command the handler
  refused, leaves no trace and does not move the version. A handler that **throws** likewise
  appends nothing: the events never leave its return value.
- A **one-way** operation appends and confirms in its own turn like any other, but its caller
  completed at the local acknowledgement and learns nothing about the outcome, the append
  included.
- **A mid-confirm failure does not fail the call.** `UpdatePrimary` loops while `WriteAsync`
  reports no progress and never throws to the caller, so the turn *blocks*; what the caller
  observes is its own request timeout while the activation is still retrying. If the confirm later
  succeeds, the events are in the journal even though the caller saw a timeout — **a timed-out call
  is not a rolled-back call.** The next call therefore observes a state that may include a command
  the caller believes failed. Commands must be idempotent, or carry a de-duplication key.
  **Tested, not inferred**: a fault-injecting storage provider refuses the first three writes; the
  call does not fault, the provider records exactly four write attempts, and a later activation
  replays the events. The test is assertion-based rather than timed, which is possible because
  `PrimaryOperationFailed.ComputeRetryDelay` returns `TimeSpan.Zero` for the first failure and only
  then backs off (~7-22 ms, ~19-56 ms, x1.5 to a 10 s slow-poll), so three refused writes resolve
  in tens of milliseconds.

### Rulings

1. **`logProvider` is required, not defaulted.** `LogStorage` and `StateStorage` store completely
   different things under the same storage key and cannot read each other's records, so a silent
   default would make an irreversible storage decision invisible in the definition.
2. **`snapshotEvery` is NOT shipped**, and the operation does not exist rather than existing and
   failing. Evidence: `ILogViewAdaptor` (`ILogViewRead` + `ILogViewUpdate` +
   `ILogConsistencyDiagnostics`) has no snapshot or truncate member at all; the only log-lifecycle
   operation is the all-or-nothing `ClearLogAsync`. On `StateStorage` the view is written on every
   confirm, which is what "snapshot every event" would mean; on `LogStorage` the log is rewritten
   whole on every confirm and never truncated, so it grows without bound and every activation
   replays all of it. Neither could honour a period. Measured rather than asserted: a fold counter
   shows `LogStorage` re-folding all three stored entries after a re-activation and `StateStorage`
   folding none.
3. **Which `grainFor` operations carry over**, each by mechanism:
   - `handle` (events-and-reply shape), `onActivate`/`onDeactivate` (returning `unit` — there is no
     state to replace, and `onActivate` runs after the replay), `collectionAge`, `placement`: **yes**.
   - `statelessWorker`: **no**. Many activations of one grain identity, each hosting its own
     log-view adaptor over the same journal, racing the others' appends through the adaptor's e-tag
     retry loop.
   - `defaultState`/`initialState`: **no**, replaced by `initialEventState`.
   - `stateFrom`, `usePersistentState`: **no**. A second durable holder on the same activation is a
     second source of truth with no ordering against the journal.
   - `transactionalStateFrom`, `transactional`: **no**. A log-view adaptor registers nothing with
     the transaction manager and has no prepare or abort, so events confirmed inside a transaction
     would survive its abort. Refused at sealing, naming the operations.
   - `onStream`, `onBroadcast`, `onTimer`, `onReminder`: **no**. Every one is a whole-state-
     replacement hook, which a journaled definition cannot honour. A journal-raising variant of each
     is a follow-up, not a wall.
4. **A `readOnly` or `alwaysInterleave` operation may not raise events**, refused at dispatch. Such
   an operation may run while another turn is in flight, so its appends could not be ordered against
   that turn's; dropping the events silently would be worse, because the handler believed it had
   changed the grain.
5. **An explicit contract `grainType` is always required.** The grain type name is part of the
   storage key of the journal, so a brand rename would orphan every stored event rather than a single
   record — the `grainFor` durable-attachment rule, applied unconditionally.
6. **`raiseConditional` is shipped; version counters are shipped.** `context.journalVersion` is
   cheap and matches `JournaledGrain.Version`. `raiseConditional` can only ever answer `false` when
   something else can write the journal between the handler's read and its append, and with a
   non-reentrant definition on a single cluster nothing can — the activation is the sole writer and
   Orleans does not interleave its turns. It is shipped because a `reentrant`/`mayInterleave`
   journaled contract is reachable and is exactly where it becomes meaningful; the test pins the
   supported case rather than staging a conflict that cannot happen.
7. **Upcasting: recorded as a follow-up, with the hook located.** There is no upcasting today and no
   place a hook could go without a new decision. An entry is the definition's exact `'Event` type
   serialized through the F# binary codec, whose union format is positional
   (`[case-tag][field-count][fields…]`), so appending a case is safe and reordering or reshaping one
   is not. The hook would have to sit in `FunctionalJournalHost.UpdateView`, between
   `blueprint.DecodeEvent` and `blueprint.Apply` — but it needs something the entry does not carry:
   a **version stamp**. Adding one is a durable-format change to `FunctionalJournalEntry`, so it
   belongs in its own task with a migration story, not bolted onto this one.
8. **Marten: the existing package contains no Marten adapter.**
   `src/Orleans.FSharp.EventSourcing.Marten/MartenConfig.fs` is 50 lines whose three helpers all
   forward to Orleans' own `AddLogStorageBasedLogConsistencyProvider*`; `addMartenEventStore`
   ignores its connection string and carries a `TODO`. It is neither an `ILogViewAdaptorFactory` nor
   a grain base class, so there is nothing Marten-shaped to compose with — the composition claim was
   therefore proved in the general form instead: a hand-registered keyed `ILogViewAdaptorFactory` of
   a *different* implementation serves a journaled definition alongside a stock one, with nothing
   functional-specific on either side (`FunctionalJournalHostingTests.fs`). A real Marten adapter
   remains a separate optional package, per the owner ruling. One constraint an adapter author must
   know, and startup now checks: `AddLogConsistencyProtocolServicesFactory` is internal to Orleans,
   so a hand-registered provider has to ride along with one stock `Add*BasedLogConsistencyProvider`
   call.

### What this does NOT give you

- **No snapshotting or log truncation** (ruling 2). A `LogStorage` journal grows without bound and
  is replayed in full on every activation; keep it short or bring a provider that snapshots.
- **No event upcasting** (ruling 7). Add union cases at the end; do not reorder or reshape them.
- **No cross-cluster replication.** Orleans 10 still declares `ILogConsistencyProtocolGateway`, but
  a repository-wide search finds nothing that constructs or calls it, and
  `ILogConsistencyProtocolServices` has no message-sending member at all. A journal is
  single-cluster.
- **No transactions** (ruling 3).
- **No queryable event history.** The journal is exposed as the fold and the version.
  `RetrieveLogSegment` works on `LogStorage` and throws `NotSupportedException` on `StateStorage`
  (the base implementation), so a "read your own log" API would be provider-dependent and is not
  offered; a projection can read the storage provider directly.
- **No exactly-once command semantics.** See the confirmation section: a timed-out call cannot be
  distinguished from a lost reply.
- **No ordering across grains.** Each grain's journal is its own; there is no global sequence.
- **A storage provider that does not follow Orleans' read contract defeats the retry protocol.**
  Orleans' own providers REPLACE `grainState.State` with a fresh instance when no record exists
  (`MemoryStorage.ReadStateAsync`). The log-view adaptor decides whether an apparently-failed write
  actually landed by re-reading and comparing a write bit it had already flipped in the in-memory
  instance, so a provider that leaves the caller's instance alone hands the flipped bit straight
  back and the adaptor concludes a failed write succeeded. Found while building the fault-injection
  test, which it had silently turned into a no-op.
- **The fold pays a codec round trip per event.** `UpdateView` decodes the state, decodes the event,
  applies, and re-encodes — chosen over holding the live object because the alternative needs a
  copier registration for every application state type and writes CLR type names into durable
  storage. Replay of an N-event journal is O(N) either way; this is a constant factor on top.
- **`ClearLogAsync` is not exposed.** Both built-in providers support it, but it is an
  all-or-nothing delete of a durable journal and deserves its own decision about who may call it.
- **Reentrancy is allowed but unguarded.** A `reentrant` journaled contract interleaves turns that
  each read the confirmed state and append; the appends are ordered by the adaptor but the decisions
  are not. `raiseConditional` is the tool for that, and using it is the application's call.
- **No `FunctionalScripting` registration.** `FunctionalGrainRegistration.of'` boxes a `grainFor`
  definition only. A journaled one needs a log-consistency provider registered on the same builder,
  and `FunctionalScripting.startOnPorts` exposes no hook for that — so boxing a journaled definition
  would ship a path that always fails startup validation. Both halves (the boxing overload and a
  configuration hook, or a convention for the two built-in providers) belong in one follow-up.

**Size:** L — as estimated. The step-0 probe was the majority of the risk and most of the surprises;
the definition kind, the runtime, and the dispatch change were mechanical once the four mechanism
facts above were known.

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

## 5. Reentrancy variants — RESOLVED

**Status: implemented.** Delivered on `feat/004-parity-phase-c`; the design sketch below has been
replaced by the resolved design it turned into, and every claim here is backed by a test or by
named Orleans source (identical on 10.1.0 and 10.2.2 — `IGrainContextActivator.cs` and
`GrainAttributeConcurrency.cs` are byte-identical between the two tags).

**Gap (for the record):** per-operation `readOnly`/`alwaysInterleave` existed; Orleans also has
whole-grain `[Reentrant]` and predicate `[MayInterleave]`, and the functional runtime could reach
neither.

### The mechanism (normative)

Both are Orleans' own seams; neither needs code generation, and neither is reachable by installing
a component of our own — `GrainCanInterleave` and `IMayInterleavePredicate` are both `internal` to
`Orleans.Runtime`, so the only way in is through the two attributes' published grain properties.

- **Whole-grain.** `ReentrantAttribute` is an `IGrainPropertiesProviderAttribute` whose `Populate`
  writes `WellKnownGrainTypeProperties.Reentrant` (`"reentrant"`) `= "true"`. Orleans'
  `ReentrantSharedComponentsConfigurator` — an `IConfigureGrainTypeComponents` registered by
  `DefaultSiloServices` — reads that property back and sets a `GrainCanInterleave` component
  holding `ReentrantPredicate.Instance`, which `ActivationData.MayInvokeRequest` consults. The
  registry's own properties provider therefore **constructs the real attribute and writes its own
  `Populate` output**, exactly as Phase B does for stream bindings. Because `[Reentrant]`
  contributes a property and nothing else, that is complete fidelity.
- **Per request.** `MayInterleaveAttribute.Populate` writes
  `WellKnownGrainTypeProperties.MayInterleavePredicate` (`"may-interleave-predicate"`) `=` the
  callback **method name**, and `MayInterleaveConfiguratorProvider` then reflects a method of that
  name off the **grain class** (`Public | Static | Instance | FlattenHierarchy`, signature
  `bool (IInvokable)`). A property alone cannot supply a method, so the attribute has to genuinely
  be on the grain class — and it is not inert: `AttributeGrainPropertiesProvider` publishes the key
  for every class carrying it, and the configurator provider installs a predicate for every grain
  type whose properties contain it. Putting it on the shared marker would therefore give **every**
  functional grain type a predicate it never asked for, so a definition declaring `mayInterleave`
  is published under a separate grain class, `FunctionalInterleavingGrainMarker<'Actor>` — the same
  reason Phase B's `FunctionalStreamingGrainTarget` is a separate type.
- **The callback must be static.** `MayInterleaveStaticPredicate` discards the grain instance;
  `MayInterleaveInstancedPredicate<T>` binds `instance as T` where `T` is the grain class, which is
  always `null` here because the functional activation instance is `FunctionalGrainTarget<'Actor>`
  and the grain class is the marker. A static callback has no `this` to carry the definition, so it
  identifies its definition by the one identity it does have: its own closed marker type, looked up
  in a process-wide table written while the silo's service collection is configured.
- **The predicate sees the envelope.** `GrainCanInterleave.MayInterleave` hands the callback
  `message.BodyObject as IInvokable`, which for a functional call is the fixed `FunctionalRequest`,
  whose **argument 0 is the `FunctionalRequestEnvelope`** — i.e. the public
  `IFunctionalRequestMetadata`. Nothing deserializes the payload to decide admission.

**Proof both seams work (Step 0, both Orleans versions):** two overlapping calls interleave on one
functional activation with the property published and do not without it; a predicate admits one
operation and refuses another on the same activation, seeing our envelope in both cases.

### Resolved design

- **`reentrant`** — a contract operation taking no argument. Publishes the property above.
- **`mayInterleave (predicate: IFunctionalRequestMetadata -> bool)`** — a contract operation.
  **Open question resolved: metadata only**, as recommended. The predicate is *declared* over
  `IFunctionalRequestMetadata`, which exposes grain type, contract version, operation ID, the three
  admission flags, and the payload **length** and no payload — so protocol-before-payload is a
  type-level guarantee here, not a convention. It also has to be: Orleans runs this callback on the
  activation's scheduling path, strictly before dispatch, where deserializing an argument would be
  both a cost and a trust decision taken before any protocol validation has run.

### Rulings

1. **Which per-operation flags conflict with `reentrant`: `alwaysInterleave` only.**
   `ActivationData.MayInvokeRequest` returns `true` for an `InvokeMethodOptions.AlwaysInterleave`
   message **before** it looks at the `GrainCanInterleave` component at all, so on a reentrant
   grain (where every request already interleaves) the flag adds nothing. Rejected at sealing.
   `readOnly` and `oneWay` are **not** rejected, because neither is only a scheduling flag in this
   runtime: `readOnly` also makes the invocation state-neutral (its replacement state is discarded
   and its persistent-state facade rejects the setter — `FunctionalDispatch.dispatch`), and
   `oneWay` is a delivery mode with no reply. Both keep their full meaning on a reentrant grain,
   and rejecting them would remove expressiveness the sketch's "redundant" argument does not cover.
2. **`mayInterleave` also rejects `alwaysInterleave`,** by the same mechanism read the other way:
   the flag is admitted before the predicate is consulted, so the predicate could never refuse that
   operation. One uniform rule — *a contract-level interleaving policy rejects the per-operation
   `alwaysInterleave` flag* — covers both, and keeps "which decision governs this operation"
   answerable from the contract alone.
3. **`reentrant` and `mayInterleave` are mutually exclusive.** `GrainCanInterleave.MayInterleave`
   returns on the first predicate that answers `true`, and `ReentrantPredicate` always does, so a
   predicate on a reentrant grain could only ever be ignored.
4. **A throwing predicate propagates.** Orleans logs it (`LogErrorInvokingMayInterleavePredicate`)
   and rethrows; the message loop's `catch` then rejects that message to its caller as
   `Message.RejectionTypes.Transient` and removes it from the queue. `InsideRuntimeClient` completes
   the caller's callback with the rejection and does **not** resend, so there is no retry storm and
   the activation is unharmed. That behaviour is kept rather than swallowed — degrading to "do not
   interleave" would hide an application fault and silently change the activation's concurrency —
   and the runtime only wraps the exception in a transport-stage diagnostic naming the grain type
   and the operation, so the rejection is attributable.

### What this does NOT give you

- **Reentrancy does not make whole-state replacement concurrency-safe.** A handler receives the
  state it started with and publishes its replacement when it returns, so two interleaved writers
  are last-writer-wins and the earlier write is silently lost. This is a property of the authoring
  model, not a defect of the seam, and it is asserted by an integration test and printed by the
  feature tour rather than only documented.
- **The predicate is consulted for the running request too.** `MayInvokeRequest` admits an incoming
  request when `predicate(incoming) || predicate(blocking)`, so an operation the predicate accepts
  also lets anything interleave with it while it is executing. Documented; not something the
  runtime can or should change.
- **The predicate binding is process-wide, keyed by the closed marker type** — which is derived
  from the actor brand alone, so it is not per-silo. Re-registering the SAME grain type name is an
  idempotent overwrite (an in-process silo restart re-seals the definition and produces a fresh
  closure; latest wins is correct there). A SECOND grain type name on one actor brand is rejected
  at configuration time, naming both grain types and the brand: each silo's own
  `FunctionalGrainRegistry` already rejects that collision within a silo but cannot see across
  silos in one process, and a silent overwrite would leave a live grain type consulting another
  definition's predicate. **The residual therefore narrows to:** two silos in one process hosting
  the same grain type name share one predicate closure — the last one sealed. That is only
  observable if the two silos were configured with *different* predicate bodies for the same grain
  type, which is not a supported configuration.

**Size:** M — as estimated.

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

## 7. Version-tolerant contracts — RESOLVED

**Status: implemented.** Delivered on `feat/004-parity-phase-c`.

**Gap (deliberate spec-003 design, for the record):** requests had to match the hosted contract
version exactly; a rolling deploy across a version bump rejected old-version calls (documented,
with the two-contracts pattern as the workaround).

### The mechanism (normative)

Admission, and nothing else — but "nothing else" needed one non-obvious piece.

A **protocol token** is `SHA256(grainType NUL version NUL operationId NUL direction)`
(`ProtocolToken.compute`). The version is inside the digest, so a caller at an older admitted
version sends a *different* request token and validates the reply against a *different* reply
token (`FunctionalCallSite.ValidateReply`). A version-tolerant host therefore cannot compare
against one fixed pair: it has to expect, and answer in, **the caller's own version's tokens**.
`FunctionalHostedOperation.VersionTokens` precomputes one pair per admitted version when the
definition is sealed, indexed by `version - MinAcceptedVersion`; dispatch resolves the pair from
the admitted request version and replies with it.

Everything else is untouched: the envelope layout, the stable operation IDs, the admission-flag
byte, the payload codec, the grain identity, and the storage identity are all version-independent
(spec 003's guarantee, now pinned by tests that call one grain key at two versions and read each
other's state back). Nothing is published to the grain manifest for the policy either — it is a
host-side rule, so a silo that has gossiped the grain type sees exactly what it saw before.

### Resolved design

- **`acceptsVersions policy`**, a contract operation over a **closed DU**:

  ```fsharp
  type VersionPolicy =
      | Exact                                  // default; spec-003 behaviour
      | BackwardCompatible of minVersion: int  // admit minVersion .. contractVersion
  ```

  **The predicate form is not merely disfavoured here, it is unimplementable at the same cost.**
  A predicate's accepted set is unbounded, so the token pairs above could not be precomputed and
  the host would have to hash per call, on the request path, for a set it cannot enumerate — and
  the range could not appear in a diagnostic. The standing "closed sets over predicates" rule and
  the mechanism agree, so no SPEC-DEVIATION is raised.
- **`sinceVersion n (_.op)`**, per operation. A call admitted at version `v` is refused for an
  operation whose `sinceVersion > v`, with a diagnostic naming both numbers. This is **not**
  redundant with the token check: the older caller's token is computed from its own version, which
  is precisely the token a tolerant host now expects for that version, so without `sinceVersion`
  the call would be admitted and its argument deserialized as the newer declared type. Checked
  immediately after descriptor resolution and before the token comparison, as step 2b of the
  spec-003 validation order — the more specific fault, reported first.

### Rulings

1. **Open question resolved: yes, argument-shape evolution is the application's problem.**
   Accepting a version **asserts wire compatibility**. The argument payload is deserialized as the
   hosted definition's exact declared CLR type whatever version admitted it, and the reply is
   serialized the same way; nothing converts between shapes and nothing inspects an older one.
   `BackwardCompatible n` is the application stating that every version from `n` upwards still
   sends and reads the same argument and reply types for every operation it can invoke. An
   operation whose shape changed needs a **new operation** (a new `operationId`), not a wider
   policy — and `sinceVersion` then keeps the old callers off it. Normative text lives in
   `docs/functional-grains.md`, "Version tolerance".
2. **Rejection diagnostics stay in the spec-003 taxonomy and stage** — transport stage, before any
   handler runs. Under `Exact` the sentence is byte-for-byte the spec-003 one, so the feature tour,
   the tour README, and the existing tests are unchanged; `BackwardCompatible` adds a
   range-naming sentence, and `sinceVersion` adds an operation-naming one.
3. **Sealing rejects a policy that cannot do anything**: a floor at or below zero, a floor above
   the contract version (the contract would admit nothing at all), a `sinceVersion` at or below
   zero or above the contract version, and — uniformly — a `sinceVersion` at or below the lowest
   admitted version, which could never reject a call. That last rule is what catches the realistic
   mistake, `sinceVersion` declared **without** `acceptsVersions`, where the default policy admits
   the hosted version only and the declaration is silently dead.

### What this does NOT give you

- **No negotiation and no down-conversion.** The host does not adapt payloads, does not pick a
  handler per version, and does not tell the caller which versions it accepts before the call. A
  refused version is a failed call.
- **The policy is per hosted definition, not per operation.** `sinceVersion` narrows an admitted
  version *for one operation*; there is no way to admit version 2 for one operation and version 3
  for another in the widening direction.
- **Wire compatibility includes the admission flags.** The admission-flag byte
  (`readOnly`/`oneWay`/`alwaysInterleave`) travels in the envelope and is compared against the
  hosted descriptor, so an operation whose flags changed between two versions inside the accepted
  range still fails — with the spec-003 admission-flags diagnostic rather than a version one.
  Pinned by an integration test.
- **A wider policy does not make an older client's *reply* handling tolerant.** The client still
  validates the reply token and deserializes the reply as *its own* declared type; if the reply
  shape changed, the wider policy has not helped and `sinceVersion` on a new operation is the
  answer.

**Size:** M — as estimated; the normative text was indeed the hard part.

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

**Status (Phase A, Task A2 — delivered):** implemented on
`feat/004-parity-phase-a` (`src/Orleans.FSharp/FunctionalInterop.fs`).

**Gap (compiler-verified above):** calling functional grains from C# works
but is non-idiomatic (`.Invoke` chains, `Module`-suffixed names,
`FSharpResult` handling) and requires knowing to pin FSharp.Core.

**Resolved design — the consumer declares the interface, the library binds it.**
The sketch's "generated-at-bind C# view … whose members are ordinary
`Task<TReply> Op(TArg arg)` delegates" is not what shipped, and the difference
matters: a *generated* view can only be reached through `dynamic` or through a
name C# cannot see at compile time, so the consumer would still not get an
interface call. What ships instead inverts it — the consumer writes the
interface, and the library binds *that*:

```csharp
public interface IChatRoom {
    Task Join(string user);
    Task<FSharpResult<int, ChatError>> Say(string sender, string message);
    Task<int> MemberCount();
}

var room = FunctionalGrainInterop.For<IChatRoom>(RoomApiModule.contract, factory, "general");
```

`For<TFacade>` materializes the interface with `System.Reflection.DispatchProxy`.
Nothing is code-generated, no build step is added, and the F# side of a contract
is unchanged.

**Binding rules, all enforced at the `For` call and none on a call** (the
spec's own earliest-stage-validation rule):

1. **Name mapping** — a member matches an operation whose **operation ID**
   (the record field name unless `operationId` overrode it) differs only by
   case (`OrdinalIgnoreCase`). `[FunctionalOperation("say")]` on the member
   overrides the name match and is matched **exactly** (`Ordinal`) — the
   attribute is the documented way to disambiguate operation IDs that differ
   only by case, which a case-folding override could not name. Two operations
   matching one member is an error naming both.
2. **Coverage** — every member must map; an unmapped member is an error
   listing the contract's operations. Unmapped **operations** are fine:
   partial facades are supported and documented (a read-only facade and a
   writer facade over one contract is the intended use).
3. **Argument shape** — a `unit` argument maps to a parameterless member; a
   single argument to one parameter of exactly that type; a tuple argument to
   that many parameters in order (the facade packs them with a precomputed
   `FSharpValue.PreComputeTupleConstructor`, which handles the nested
   representation beyond seven elements) **or**, alternatively, to one
   parameter of the tuple type. Anything else is an error naming expected and
   actual. A trailing `CancellationToken` is just an extra parameter and is
   rejected as one; remote cancellation stays on `FunctionalGrainRef.callCancellable`.
4. **Reply shape** — exactly `Task<'Reply>`; a `unit` reply additionally
   accepts the non-generic `Task`, which is what a C# author writes.
   `Task<Unit>` is accepted too, for generic code that needs a value. `void`,
   `ValueTask`, a bare `T`, and a mismatched `Task<T>` are errors.
5. **Rejected member shapes** — generic members, `ref`/`out`/`in` parameters,
   properties, events, default interface methods, and static members. Members
   of extended interfaces are included, which is what makes
   `interface IChatRoom : IDisposable` an error rather than a surprise. A
   *non-public* facade interface is **not** rejected: `DispatchProxy` emits
   the access-check suppression it needs, proven by test.
6. **Preclosing** — one invoker per member, closed over the operation's exact
   argument and reply types while the plan is built, over the same preclosed
   `BoundCall.Field` closure an F# caller reaches through the API record. The
   plan is cached per (interface, contract) pair and the typed contract binder
   per contract CLR type, so binding a second key closes no generic at all. A
   call performs one dictionary lookup, one delegate call, and the grain call
   — asserted against the runtime's instrumentation counters, with a
   counterweight test proving those same counters are non-zero while the
   facade is created.

**A non-generic contract base was required.** C# has no partial type-argument
inference, so `For<IChatRoom>(contract, factory, key)` can only compile when
every *parameter* type is non-generic or inferable. `GrainContract<'Actor,
'Key, 'Api>` therefore gained an abstract base, `FunctionalContract`, carrying
the key type and the operation descriptors; the domain key is passed as
`obj` and type-checked against the contract at the `For` call. This is the one
place the facade trades a compile-time check for an earliest-stage runtime
one, and it buys the single-type-argument call site the whole item exists for.

**FSharp.Core, measured rather than assumed.** The sketch's "requires knowing
to pin FSharp.Core" overstates it. The packed `Orleans.FSharp` nuspec declares
`FSharp.Core >= 10.1.201`, and a `ProjectReference` flows it too, so a C#
consumer that references nothing else compiles with no FSharp.Core reference
of its own (verified both ways). What actually breaks is a *lower direct*
reference, since a direct `PackageReference` wins over a transitive one:
NU1605 "package downgrade", a warning by default and an error under
`TreatWarningsAsErrors`. Documented in that form.

**Delivered artifacts:** `docs/calling-from-csharp.md` + published mirror
(assembly references, hosting from C#, the interface pattern, mapping rules,
`Result`/`Option`/list reading, the full rejection list, the honest per-call
cost); `examples/chat-room/src/Interop` — a C# console project that hosts the
F# room with `AddFunctionalGrain` and drives it through the facade, run (not
merely built) by the CI examples job; 44 unit tests over C#-declared fixture
interfaces (`tests/Orleans.FSharp.Tests.Facades`, C# because default interface
methods and events cannot be declared in F# at all).

**Follow-up, deliberately out of scope here:** a facade over
`FunctionalObserverHandle`. A C# process can already *be pushed to* — the
handle is an ordinary operation argument — but the handler side is still an F#
record, so "C# can consume everything" is not yet true for observers. Same for
streaming replies (item 6): its `IAsyncEnumerable<'T>` shape must stay
C#-consumable, and this item is the enforcement point for that rule, but
nothing about streaming is implemented here.

**Size:** S/M as estimated.

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
  fresh from spec 003. **Landed:** item 1 is implemented and its section above is
  the resolved design.
- **Phase C:** 5 (reentrancy), 7 (version tolerance) — both are admission-layer
  work and share tests.
- **Phase D:** 2 (transactions). **Landed:** item 2 is implemented and its section
  above is the resolved design.
- **Phase E:** 3 (event sourcing) — after the provider-story decision. **Landed:** item 3 is
  implemented and its section above is the resolved design.
- **Phase F:** 6 (streaming replies) — largest; consider splitting into its
  own spec once A-C land.

Each phase repeats the spec-003 discipline: seam proofs before runtime
layers, both-Orleans-versions matrix, mutation-checked guards, examples with
visible parity mapping, docs updated with old pages kept.
