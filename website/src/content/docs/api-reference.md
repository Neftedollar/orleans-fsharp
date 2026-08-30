---
title: "API Reference"
description: "Quick reference for the public modules, types, and functions in Orleans.FSharp."
---

# API Reference

**Quick reference for the public modules, types, and functions in Orleans.FSharp.**

Reference tables, not tutorials. Every section names the guide that carries the semantics; look
there for what a thing *means* and here for what it is *called*.

The [functional grain runtime](#functional-grain-runtime) is the current authoring model and comes
first. Shared Orleans helpers follow it. The superseded authoring surface has its own
[Legacy API Reference](/orleans-fsharp/legacy/api-reference/).

**Where the names in the functional tables come from.** Every custom-operation name and every
context member below is pinned by `tests/Orleans.FSharp.Tests/FunctionalSurfaceTests.fs`, which
reflects over the builders and the context type and asserts the exact set. A name that appears here
and not there, or there and not here, is a bug in one of the two.

---

## Functional grain runtime

**The current grain authoring model.** A user-authored API record instead of a C# CodeGen
interface, a contract that declares the wire and delivery policy, and a definition that binds
handlers to it. See [Functional Grain Runtime](/orleans-fsharp/functional-grains/) for the full guide.

### Entry points

| Entry point | Signature | Description |
|---|---|---|
| `grainContract<'Actor, 'Key, 'Api>` | `GrainContractBuilder<'Actor,'Key,'Api>` — a value, not a function | Opens the contract CE |
| `contract<'Key, 'Api>` | `GrainContractBuilder<'Api,'Key,'Api>` — a value, not a function | Short form: the API record is its own actor brand ([details](/orleans-fsharp/functional-grains/#the-short-form-the-api-record-as-its-own-brand)) |
| `grainFor contract` | `GrainContract<...> -> FunctionalGrainDefinitionBuilder<...>` | Opens the definition CE |
| `journaledGrainFor contract` | `GrainContract<...> -> FunctionalJournaledGrainDefinitionBuilder<...>` | Opens the journaled definition CE ([Event Sourcing](/orleans-fsharp/event-sourcing/)) |
| `observerContract<'Brand, 'Api>` | `ObserverContractBuilder<'Brand,'Api>` — a value, not a function | Opens the observer contract CE |
| `FunctionalGrain.ref` | `contract -> IGrainFactory -> 'Key -> 'Api` | Binds a typed API record |
| `FunctionalGrain.rawRef` | `contract -> IGrainFactory -> 'Key -> FunctionalGrainRef<'Actor,'Key,'Api>` | Binds the typed wrapper |
| `FunctionalGrain.streamId` | `contract -> string -> 'Key -> StreamId` | Stream id whose key is the contract's own grain-key bytes |
| `FunctionalGrain.channelId` | `contract -> string -> 'Key -> ChannelId` | The same for a broadcast channel |

The three contract entry points are **type functions** -- generic values, not functions of `unit`.
`grainContract<RoomActor, RoomId, RoomApi>` *is* the builder, so the CE braces follow the type
arguments directly and there is no `()` to write. F# re-evaluates a type function at every mention,
so each contract expression opens on its own builder instance (pinned by
`tests/Orleans.FSharp.Tests/FunctionalSurfaceTests.fs`, "each entry point mention yields its own
builder"). The other four rows are ordinary functions and take their argument as usual.

`FunctionalGrain` is a static class, so `ref`/`rawRef` generalize only where F# lets a static-class
application generalize -- see [Functional grains](/orleans-fsharp/functional-grains/), "The `FunctionalGrain`
static-class inference rule".

### Contract builder — `grainContract<'Actor, 'Key, 'Api> { }`

| Keyword | Signature | Description |
|---|---|---|
| `grainType` | `string` | The wire `GrainType` string -- routing and storage identity. Optional; see [Functional grains](/orleans-fsharp/functional-grains/), "Optional grainType" |
| `version` | `int` | Native Orleans interface version plus functional envelope version (`1..65535`); matched exactly unless `acceptsVersions` widens payload admission. Defaults to `1` |
| `stringKey` / `guidKey` / `int64Key` | — | Native key codec: the domain key type *is* the Orleans key type |
| `stringKeyMapped` / `guidKeyMapped` / `int64KeyMapped` | `('Key -> K)` `(K -> 'Key)` | Mapped key codec over a domain key type |
| `guidCompoundKey` / `int64CompoundKey` | — | Native compound key (Guid/int64 + string extension) |
| `guidCompoundKeyMapped` / `int64CompoundKeyMapped` | `('Key -> K * string)` `(K -> string -> 'Key)` | Mapped compound key |
| `readOnly` | `selector` | The handler's returned state is discarded; interleaves with other read-only calls |
| `oneWay` | `selector` | The caller's `Task` completes once the message enters the local send path |
| `alwaysInterleave` | `selector` | Interleaves regardless of `readOnly`/`oneWay`; also state-neutral. Rejected at sealing when the contract declares `reentrant` or `mayInterleave` |
| `transactional` | `Orleans.TransactionOption -> selector` | Orleans transaction policy for one operation ([Functional grains](/orleans-fsharp/functional-grains/), "Distributed ACID transactions"). Orleans' own enum, **not** this library's `Orleans.FSharp.Transactions.TransactionOption` DU |
| `operationId` | `string -> selector` | Override an operation's wire ID, decoupling it from the F# field name. A second overload takes a `StreamSelector` |
| `sinceVersion` | `int -> selector` | The version an operation was introduced at; an admitted older call is refused for it by name. A second overload takes a `StreamSelector` |
| `reentrant` | — | Whole-grain reentrancy -- every request may enter a busy activation. Does **not** make whole-state replacement concurrency-safe |
| `mayInterleave` | `(IFunctionalRequestMetadata -> bool)` | Per-request interleave predicate over protocol metadata only; mutually exclusive with `reentrant`. Orleans consults it for the *running* request too |
| `acceptsVersions` | `VersionPolicy` | `Exact` (default) or `BackwardCompatible n` -- which request versions this definition admits |

`operationId` and `sinceVersion` are the only two per-operation declarations that compose with a
streaming field; the four admission policies are refused at sealing.

Every API field takes exactly one F# argument. Prefer a named record for multi-input domain data
(`typing: Typing -> Task<unit>`); tuples remain valid when positional data is intentional. A field
spelled curried fails contract construction. See [Functional grains](/orleans-fsharp/functional-grains/),
"One operation, one argument".

### Definition builder — `grainFor contract { }`

| Keyword | Handler signature | Description |
|---|---|---|
| `defaultState` | `unit -> 'State` | Ephemeral state factory, called once per activation |
| `initialState` | `'Key -> 'State` | Key-aware ephemeral state factory |
| `handle` | `selector` + `Handler<'Actor,'Key,'State,'Arg,'Reply>` | Attach a handler to one API operation |
| `handleQuery` | `selector` + `QueryHandler<'Actor,'Key,'State,'Arg,'Reply>` | Attach a reply-only handler; the operation must be declared `readOnly` |
| `handleStream` | `streamSelector` + `StreamHandler<'Actor,'Key,'State,'Arg,'Item>` | Attach a handler to one server-streaming operation ([Streaming replies](/orleans-fsharp/streaming-replies/)) |
| `stateFrom` | `PersistentStateRef<'State>` | Attach the primary persistent-state holder |
| `usePersistentState` | `PersistentStateRef<'S>` + `('Key -> 'S)` | Attach an additional named persistent-state facet (repeatable) |
| `persistenceCodec` | `FunctionalPersistenceCodec` | Grain-level durable codec for attached persistent states; an element-level `PersistentState.withCodec` wins |
| `transactionalStateFrom` | `TransactionalStateRef<'S>` + `('Key -> 'S)` | Attach a transactional facet (repeatable) |
| `collectionAge` | `TimeSpan` | Idle-deactivation threshold override |
| `placement` | `PlacementStrategy` | `Random` / `PreferLocal` / `ActivationCountBased` / `ResourceOptimized` / `HashBased` / `SiloRoleBased` |
| `statelessWorker` | `int` | Stateless-worker placement with a max-local-workers cap |
| `migrationParticipant` | `FunctionalMigrationParticipantFactory<'Actor,'Key>` | Add an activation-scoped Orleans migration participant; repeatable and created before rehydration |
| `onActivate` | `ActivateHook<'Actor,'Key,'State>` | Activation hook; its returned state is published in memory |
| `onDeactivate` | `DeactivateHook<'Actor,'Key,'State>` | Deactivation hook; no replacement state |
| `onLifecycle` | `LifecycleStage` + `LifecycleHook<'Actor,'Key>` | Hook a numbered Orleans grain-lifecycle stage |
| `onReminder` | `string` + `TimeSpan` (due) + `TimeSpan` (period) + `ReminderHook<...>` | Declare a reminder |
| `onTimer` | `string` + `GrainTimerCreationOptions` + `TimerHook<...>` | Declare a timer |
| `onStream` | `string` (provider) + `string` (namespace) + `StreamHook<...>` | Implicit stream subscription |
| `onBroadcast` | `string` (provider) + `string` (namespace) + `StreamHook<...>` | Implicit broadcast-channel subscription |

### Journaled definition builder — `journaledGrainFor contract { }`

A journal-aware version of the operations above: request, timer, reminder, stream, and broadcast
handlers return events instead of replacement state. A journal still cannot be a transaction
participant or be shared by the many activations of a stateless worker. See
[Event Sourcing](/orleans-fsharp/event-sourcing/).

| Keyword | Handler signature | Description |
|---|---|---|
| `initialEventState` | `'Key -> 'State` | The seed the journal folds onto. **Required, and first** |
| `apply` | `'State -> 'Event -> 'State` | The pure fold. **Required, and second** -- it introduces the event type |
| `logProvider` | `string` | The registered log-consistency provider. **Required** |
| `journalStorage` | `string` | The grain storage a built-in provider writes through; defaults to the silo's default `IGrainStorage` and cannot be combined with `customStorage` |
| `journalCodec` | `FunctionalPersistenceCodec` | Definition-level state/event payload codec; overrides the silo's `DefaultJournalCodec` |
| `stateSchema` | `FunctionalSchema<'State>` | Version and typed upcasters for materialized state/views/snapshots |
| `eventSchema` | `FunctionalSchema<'Event>` | Version and typed upcasters for journal entries |
| `customStorage` | `IServiceProvider -> IFunctionalJournalStorage<'Key,'State,'Event>` | Typed storage bridge for Orleans' `CustomStorage` provider |
| `snapshotPolicy` | `FunctionalJournalSnapshotPolicy<'State>` | Per-definition `Inherit`, `Disabled`, `Every n`, or `When` override; requires `customStorage` |
| `handle` | `selector` + `JournaledHandler<'Actor,'Key,'State,'Event,'Arg,'Reply>` | A handler returning `events, reply` |
| `handleQuery` | `selector` + `QueryHandler<'Actor,'Key,'State,'Arg,'Reply>` | A reply-only handler that raises nothing; the operation must be declared `readOnly` |
| `handleStream` | `streamSelector` + `StreamHandler<...>` | A streaming operation; raises no events |
| `onActivate` | `JournaledActivateHook<'Actor,'Key,'State>` | Runs after replay; returns no state |
| `onDeactivate` | `JournaledDeactivateHook<'Actor,'Key,'State>` | Deactivation hook |
| `onReminder` | `string` + due/period + `JournaledReminderHook<...>` | A successful tick appends and confirms returned events |
| `onTimer` | `string` + `GrainTimerCreationOptions` + `JournaledTimerHook<...>` | Appends returned events; `Interleave = true` is supported |
| `onStream` | provider + namespace + `JournaledStreamHook<...>` | Implicit stream delivery appending returned events |
| `onBroadcast` | provider + namespace + `JournaledStreamHook<...>` | Implicit broadcast delivery appending returned events |
| `onTentativeStateChanged` | `JournaledStateChangedHook<...>` | Synchronous tentative-view notification |
| `onStateChanged` | `JournaledStateChangedHook<...>` | Synchronous confirmed-view notification |
| `onConnectionIssue` | `JournaledConnectionIssueHook<...>` | Synchronous Orleans connection-issue notification |
| `onConnectionIssueResolved` | `JournaledConnectionIssueHook<...>` | Synchronous recovery notification |
| `collectionAge` | `TimeSpan` | Idle-deactivation threshold override |
| `placement` | `PlacementStrategy` | As above. `statelessWorker` has no journaled form at all: many activations of one grain cannot share a journal |
| `migrationParticipant` | `FunctionalMigrationParticipantFactory<'Actor,'Key>` | Add an activation-scoped Orleans migration participant; repeatable |

### `FunctionalGrainContext<'Actor, 'Key>` — the per-invocation context

Passed to every handler, hook, timer, reminder, and stream callback.

| Member | Type | Description |
|---|---|---|
| `key` | `'Key` | The domain key decoded from the grain identity |
| `grainId` | `GrainId` | The Orleans identity of this activation |
| `grainFactory` | `IGrainFactory` | Bind further grain references |
| `services` | `IServiceProvider` | Resolve DI services registered on the silo |
| `logger` | `ILogger` | Logger scoped to this activation |
| `timeProvider` | `TimeProvider` | The registered time provider |
| `utcNow` | `DateTimeOffset` | Frozen at context creation -- stable across the whole callback |
| `cancellationToken` | `CancellationToken` | Selected by callback kind |
| `streamSequenceToken` | `StreamSequenceToken option` | The delivery cursor; `Some` only inside an `onStream` delivery on a rewindable provider |
| `deactivateOnIdle()` | `unit -> unit` | Request deactivation once this turn ends |
| `migrateOnIdle()` | `unit -> unit` | Ask the configured placement director to migrate after this turn; advisory, and a non-migrating Orleans 10.x attempt can become ordinary deactivation |
| `migrateOnIdle(targetSilo)` | `SiloAddress -> unit` | Request a specific compatible destination through Orleans' placement hint |
| `delayDeactivation(span)` | `TimeSpan -> unit` | Postpone idle collection |
| `persistentState(ref)` | `PersistentStateRef<'S> -> IPersistentState<'S>` | Look up an attached persistent-state facet |
| `transactionalState(ref)` | `TransactionalStateRef<'S> -> FunctionalTransactionalState<'S>` | Look up an attached transactional facet |
| `journalVersion` | `int` | The confirmed journal length, as it was when the turn started |
| `journalState<'S>()` | `unit -> 'S` | Current confirmed view |
| `journalTentativeState<'S>()` | `unit -> 'S` | Confirmed view plus submitted events |
| `unconfirmedEvents<'E>()` | `unit -> 'E list` | Locally submitted, unconfirmed suffix |
| `raiseEvent(event)` / `raiseEvents(events)` | `'E -> unit` / `'E list -> unit` | Submit without waiting for confirmation |
| `confirmEvents()` | `unit -> Task` | Confirm all submitted entries |
| `snapshotNow()` | `unit -> unit` | Force a custom-storage snapshot after this successful callback's events; overrides disabled automatic rules |
| `refreshJournal()` | `unit -> Task` | Confirm all submitted events and synchronize the confirmed view with the global journal |
| `retrieveConfirmedEvents<'E>(from, to)` | `int * int -> Task<'E list>` | Read a provider-supported half-open event segment |
| `clearJournal()` | `unit -> Task` | Clear the whole log and restore the initial state |
| `enableJournalStats()` / `disableJournalStats()` | `unit -> unit` | Toggle Orleans log-consistency statistics |
| `getJournalStats()` | `unit -> LogConsistencyStatistics` | Read collected statistics |
| `raiseConditional(events)` | `'Event list -> Task<bool>` | Append and confirm *inside* the turn; reports whether it was accepted |
| `raiseConditionalEvent(event)` | `'Event -> Task<bool>` | Single-event conditional append |
| `tryGetRequestContext<'T>(name)` | `string -> 'T option` | Typed Orleans request-context read |
| `setRequestContext(name, value)` | `string -> 'V -> unit` | Request-context write |
| `removeRequestContext(name)` | `string -> unit` | Request-context removal |

The journal members live on the one context type rather than on a journaled variant of it, and
all refuse with a definition-stage diagnostic on an ordinary `grainFor` definition.

### `FunctionalGrainRef<'Actor, 'Key, 'Api>` — the bound reference

| Member | Signature | Description |
|---|---|---|
| `key` | `'Key` | The domain key this reference is bound to |
| `api` | `'Api` | The bound API record; the same instance on every access |
| `call` | `selector -> 'Arg -> Task<'Reply>` | Invoke one operation by selector |
| `callCancellable` | `selector -> 'Arg -> CancellationToken -> Task<'Reply>` | The same, with a token |
| `stream` | `streamSelector -> 'Arg -> IAsyncEnumerable<'Item>` | Invoke one streaming operation |
| `streamCancellable` | `streamSelector -> 'Arg -> CancellationToken -> IAsyncEnumerable<'Item>` | The same, with a token |

### Handler and hook types

| Type | Definition |
|---|---|
| `Handler<'Actor,'Key,'State,'Argument,'Reply>` | `context -> 'State -> 'Argument -> Task<'State * 'Reply>` |
| `QueryHandler<'Actor,'Key,'State,'Argument,'Reply>` | `context -> 'State -> 'Argument -> Task<'Reply>` -- what `handleQuery` binds, on both definition builders |
| `StreamHandler<'Actor,'Key,'State,'Argument,'Item>` | `context -> 'State -> 'Argument -> IAsyncEnumerable<'Item>` |
| `JournaledHandler<'Actor,'Key,'State,'Event,'Argument,'Reply>` | `context -> 'State -> 'Argument -> Task<'Event list * 'Reply>` |
| `ActivateHook<'Actor,'Key,'State>` | `context -> 'State -> Task<'State>` |
| `DeactivateHook<'Actor,'Key,'State>` | `context -> DeactivationReason -> 'State -> Task<unit>` |
| `JournaledActivateHook<'Actor,'Key,'State>` | `context -> 'State -> Task<unit>` |
| `JournaledDeactivateHook<'Actor,'Key,'State>` | `context -> DeactivationReason -> 'State -> Task<unit>` |
| `JournaledReminderHook<'Actor,'Key,'State,'Event>` | `context -> 'State -> TickStatus -> Task<'Event list>` |
| `JournaledTimerHook<'Actor,'Key,'State,'Event>` | `context -> 'State -> Task<'Event list>` |
| `JournaledStreamHook<'Actor,'Key,'State,'Event,'Item>` | `context -> 'State -> 'Item -> Task<'Event list>` |
| `JournaledStateChangedHook<'Actor,'Key,'State>` | `context -> 'State -> unit` |
| `JournaledConnectionIssueHook<'Actor,'Key,'State>` | `context -> 'State -> ConnectionIssue -> unit` |
| `ReminderHook<'Actor,'Key,'State>` | `context -> 'State -> TickStatus -> Task<'State>` |
| `TimerHook<'Actor,'Key,'State>` | `context -> 'State -> Task<'State>` |
| `StreamHook<'Actor,'Key,'State,'Item>` | `context -> 'State -> 'Item -> Task<'State>` |
| `LifecycleHook<'Actor,'Key>` | `context -> Task<unit>` |
| `OperationSelector<'Api,'Argument,'Reply>` | `'Api -> ('Argument -> Task<'Reply>)` — a field projection of a unary operation (`_.join`) |
| `StreamSelector<'Api,'Argument,'Item>` | `'Api -> ('Argument -> IAsyncEnumerable<'Item>)` — a field projection of a streaming operation (`_.tail`) |

### Persistent state

| Function | Signature | Description |
|---|---|---|
| `PersistentState.create<'State>` | `string -> string -> PersistentStateRef<'State>` | `stateName -> providerName -> descriptor` |
| `PersistentState.withCodec` | `FunctionalPersistenceCodec -> PersistentStateRef<'State> -> PersistentStateRef<'State>` | Copy a descriptor with an element-level codec override |
| `PersistentState.withSchema` | `FunctionalSchema<'State> -> PersistentStateRef<'State> -> PersistentStateRef<'State>` | Copy a descriptor with a versioned envelope and typed upcaster pipeline |

The descriptor's `(stateName, providerName, storedType)` triple is its logical identity, and it is
durable identity -- see [Functional grains](/orleans-fsharp/functional-grains/), "Persistence model".

Codec resolution is `withCodec` > `persistenceCodec` > silo `DefaultStateCodec`. The default
`OrleansBinary` path preserves the existing direct state schema for supported types; F# JSON uses a
functional envelope, so changing an existing state name in either direction requires migration.
See [Serialization](/orleans-fsharp/serialization/#durable-persistence-codecs).

### Durable schema evolution

| Name | Signature | Description |
|---|---|---|
| `FunctionalSchema.current<'T>` | `int -> FunctionalSchema<'T>` | Start a pipeline at one schema version; use `0` for pre-versioning envelopes |
| `FunctionalSchema.upcaster` | `('Previous -> 'Current) -> FunctionalSchema<'Previous> -> FunctionalSchema<'Current>` | Add the next integer version and typed pure mapping |
| `FunctionalSchema.upcasterTo` | `int -> ('Previous -> 'Current) -> FunctionalSchema<'Previous> -> FunctionalSchema<'Current>` | Add a mapping to an explicit greater version |

Schemas are per persistent-state element (`PersistentState.withSchema`) or independently per
journal state/event (`stateSchema` / `eventSchema`). See
[Serialization](/orleans-fsharp/serialization/#versioned-state-and-upcasters).

### Activation migration helpers

| Name | Signature | Description |
|---|---|---|
| `ActivationMigration.participant` | `(IDehydrationContext -> unit) -> (IRehydrationContext -> unit) -> IGrainMigrationParticipant` | Build one participant from F# callbacks |
| `ActivationMigration.addValue` | `string -> 'T -> IDehydrationContext -> unit` | Add one Orleans-serialized migration value; duplicate keys fail |
| `ActivationMigration.tryValue<'T>` | `string -> IRehydrationContext -> 'T option` | Read one typed migration value |

Ordinary ephemeral functional state participates automatically. Custom participants are declared
with `migrationParticipant`; see [Functional grains](/orleans-fsharp/functional-grains/#live-activation-migration).

### Functional persistence codecs

| Name | Signature | Description |
|---|---|---|
| `FunctionalPersistenceCodec.OrleansBinary` | `FunctionalPersistenceCodec` | Compatibility default; functional exact-type binary payload |
| `FunctionalPersistenceCodec.FSharpJson` | `FunctionalPersistenceCodec` | F#-aware JSON with the library's standard options |
| `FunctionalPersistenceCodec.CreateFSharpJson` | `JsonSerializerOptions -> FunctionalPersistenceCodec` | Compatibility overload using `fsharp-json-v1` with copied custom options |
| `FunctionalPersistenceCodec.CreateFSharpJson` | `string * JsonSerializerOptions -> FunctionalPersistenceCodec` | F# JSON with an application-owned stable codec id; prefer this for custom durable contracts |
| `codec.WithReadCodec` | `FunctionalPersistenceCodec -> FunctionalPersistenceCodec` | Keep a historical JSON decoder registered while the returned codec remains the current writer; duplicate reader ids are rejected |
| `FunctionalPersistenceCodec.Id` | `string` | Stable durable id; built-ins use `orleans-binary-v1` / `fsharp-json-v1`, explicit custom codecs use the supplied id |
| `FunctionalPersistenceOptions.DefaultStateCodec` | mutable `FunctionalPersistenceCodec` | Silo default inherited by functional state without grain/element overrides |
| `FunctionalPersistenceOptions.DefaultJournalCodec` | mutable `FunctionalPersistenceCodec` | Silo default inherited by functional journals without `journalCodec` |
| `FSharpJsonGrainStorageSerializer()` | `FSharpJsonGrainStorageSerializer` | Provider-wide F# JSON serializer with standard options |
| `FSharpJsonGrainStorageSerializer(options)` | `JsonSerializerOptions -> FSharpJsonGrainStorageSerializer` | Provider-wide F# JSON serializer with copied custom options |

`FSharpJsonGrainStorageSerializer` belongs to a provider's `GrainStorageSerializer` setting and
also affects ordinary Orleans grains. It is independent of functional per-element envelopes and
does not define the durable format of a user implementation of `IFunctionalJournalStorage`.

### Transactional state

| Name | Signature | Description |
|---|---|---|
| `TransactionalState.create<'State>` | `string -> string -> TransactionalStateRef<'State>` | `stateName -> storageName -> descriptor` |
| `FunctionalTransactionalState<'S>.read` | `unit -> Task<'S>` | The current value, copied before it is returned |
| `FunctionalTransactionalState<'S>.readWith` | `('S -> 'R) -> Task<'R>` | A projection, run inside Orleans' read lock and returned uncopied |
| `FunctionalTransactionalState<'S>.update` | `('S -> 'S) -> Task<unit>` | Replace the value, inside Orleans' write lock |
| `FunctionalTransactionalState<'S>.updateWith` | `('S -> 'S * 'R) -> Task<'R>` | Replace and return a result |

Both update functions are **synchronous by type**: Orleans runs them inside the transactional
state's reader-writer lock and rejects re-entering the same state from inside a callback.

### Streaming replies

| Name | Signature | Description |
|---|---|---|
| `handleStream` | see the definition builder | Binds a streaming operation |
| `FunctionalGrainRef.stream` / `.streamCancellable` | see the bound reference | Calls one by selector |
| `FunctionalStream.withBatchSize` | `int -> IAsyncEnumerable<'T> -> IAsyncEnumerable<'T>` | Set the pull batch size of a functional stream call |

A streaming field is `'Arg -> IAsyncEnumerable<'Item>`, not `'Arg -> Task<...>`; that is what makes
it a second field kind rather than an ordinary operation. See
[Streaming replies](/orleans-fsharp/streaming-replies/).

### Orleans streams

These are push-provider streams from `Orleans.FSharp.Streaming`, distinct from a functional grain
method returning `IAsyncEnumerable<'T>`.

The canonical, complete function catalog is under `Orleans.FSharp.Streaming` below. See
[Streaming](/orleans-fsharp/streaming/) for provider semantics and worked examples.

### Observers

A handler record whose every field is `'Msg -> Task<unit>`. Push to a client-hosted observer with
no application code generation; see [Functional grains](/orleans-fsharp/functional-grains/), "Push to clients:
functional observers".

#### `observerContract<'Brand, 'Api> { }`

| Keyword | Signature | Description |
|---|---|---|
| `observerType` | `string` | Wire identity of the observer; defaults to the brand's simple CLR name, which requires a simple, non-generic, non-nested brand exactly as a derived `grainType` does |
| `version` | `int` | Contract version; defaults to `1` |

A push operation's wire ID is always its handler-record field name -- there is no `operationId`
override, so the notifying and observing sides cannot drift apart.

#### `FunctionalObserver`

| Function | Signature | Description |
|---|---|---|
| `create` | `ObserverContract -> IClusterClient -> 'Api -> FunctionalObserverHandle<'Brand,'Api>` | Host a handler record and return a serializable typed handle |
| `createFrom` | `ObserverContract -> IServiceProvider -> 'Api -> FunctionalObserverHandle<'Brand,'Api>` | The same, from any services carrying the functional transport (e.g. inside a silo) |
| `notify` | `handle -> selector -> 'Msg -> Task<unit>` | Push one message; resolves its selector on every call -- the convenience form |
| `notifier` | `handle -> selector -> ('Msg -> Task<unit>)` | Resolve once, return a preclosed push function -- the hot-path form |
| `unsubscribe` | `IGrainFactory -> handle -> unit` | Release the object reference; idempotent |

#### `FunctionalObserverManager<'Brand,'Api>`

| Member | Signature | Description |
|---|---|---|
| `.ctor` | `TimeSpan` | Liveness window a subscription must be refreshed within |
| `Subscribe` | `handle -> unit` | Add or refresh a subscription |
| `Unsubscribe` | `handle -> bool` | Remove one subscription |
| `Notify` | `selector -> 'Msg -> Task<unit>` | Fan out to every live subscription; resolves its selector once per call, not once per subscriber |
| `RemoveExpired` | `unit -> unit` | Drop subscriptions past the liveness window |
| `Clear` | `unit -> unit` | Forget every subscription |
| `Count` | `int` | Live subscription count |
| `Expiry` | `TimeSpan` | The configured liveness window |

A manager is a mutable object held in **ephemeral** handler state. It holds live object references,
so it must never be part of a persistent state type -- the F# codec refuses one.

### Types

| Type | Description |
|---|---|
| `GrainContract<'Actor, 'Key, 'Api>` | Sealed result of `grainContract { }` |
| `FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>` | Sealed result of `grainFor { }` |
| `FunctionalJournaledGrainDefinition<'Actor, 'Key, 'Api, 'State, 'Event>` | Sealed result of `journaledGrainFor { }` |
| `IFunctionalJournalStorage<'Key, 'State, 'Event>` | Typed read/append/clear contract behind Orleans' CustomStorage provider |
| `FunctionalJournalStorageIdentity<'Key>` | Grain type, complete `GrainId`, and decoded key supplied to custom storage |
| `FunctionalJournalRead<'State, 'Event>` | Optional snapshot plus the ordered retained event tail |
| `FunctionalJournalWrite<'State, 'Event>` | CAS version, atomic event batch, and optional resulting snapshot |
| `FunctionalJournalSnapshot<'State>` | Materialized state plus the event version it represents |
| `FunctionalJournalSnapshotPolicy<'State>` | Per-definition `Inherit`, `Disabled`, `Every`, or typed `When` rule |
| `FunctionalJournalSnapshotDefault` | Silo-wide `Disabled`, `Every`, or heterogeneous `When` rule |
| `FunctionalJournalSnapshotOptions` | Options whose `Policy` is inherited by custom-storage definitions |
| `FunctionalJournalSnapshotContext` | Boxed identity, version, state type, and state passed to a global `When` rule |
| `FunctionalJournalPermanentStorageException` | Marks a custom-storage failure as non-retryable; the runtime fails the operation and deactivates the grain |
| `FunctionalPersistenceCodec` | Durable functional payload descriptor: Orleans binary or F# JSON |
| `FunctionalSchema<'Current>` | Immutable typed durable-schema pipeline; exposes `EarliestVersion` and `CurrentVersion` |
| `FunctionalPersistenceOptions` | Mutable silo-wide state and journal codec defaults |
| `FSharpJsonGrainStorageSerializer` | Provider-wide `IGrainStorageSerializer` for F# JSON |
| `FunctionalGrainContext<'Actor, 'Key>` | Per-invocation context (members above) |
| `FunctionalGrainRef<'Actor, 'Key, 'Api>` | Typed reference wrapper (members above) |
| `ObserverContract<'Brand, 'Api>` | Sealed result of `observerContract { }`; exposes `ObserverTypeName` and `Version` |
| `FunctionalObserverHandle<'Brand, 'Api>` | Serializable typed handle to a client-hosted observer; an operation argument or a tuple element, never an F# record field |
| `PersistentStateRef<'State>` | Immutable descriptor returned by `PersistentState.create` |
| `TransactionalStateRef<'State>` | Immutable descriptor returned by `TransactionalState.create` |
| `FunctionalTransactionalState<'State>` | The invocation-bound transactional facade |
| `PlacementStrategy` | `Random`, `PreferLocal`, `ActivationCountBased`, `ResourceOptimized`, `HashBased`, `SiloRoleBased` |
| `FunctionalActivationMigrationContext<'Actor,'Key>` | Per-activation key, `GrainId`, services, and logger supplied to a migration-participant factory |
| `FunctionalMigrationParticipantFactory<'Actor,'Key>` | Creates one `IGrainMigrationParticipant` per activation before Orleans rehydrates it |
| `VersionPolicy` | `Exact`, `BackwardCompatible of int` |
| `LifecycleStage` | `First`, `SetupState`, `Activate`, `Last` (`Activate` is rejected by `onLifecycle`; use `onActivate`) |
| `IFunctionalRequestMetadata` | `mayInterleave`'s argument: `GrainType`, `ContractVersion`, `OperationId`, `IsReadOnly`, `IsOneWay`, `IsAlwaysInterleave`, `PayloadLength` |
| `FunctionalGrainTransportOptions` | Transport limits; `DefaultMaxPayloadBytes` is 16 MiB |

### Custom journal storage and snapshots

These are the complete application-facing members of the typed CustomStorage bridge. The storage
implementation, not the runtime, owns durable I/O; the runtime owns replay through the definition's
single `apply` fold.

| Member | Signature | Contract |
|---|---|---|
| `IFunctionalJournalStorage.Read` | `FunctionalJournalStorageIdentity<'Key> -> Task<FunctionalJournalRead<'State,'Event>>` | Return the latest snapshot and the ordered retained tail strictly after it |
| `IFunctionalJournalStorage.Append` | `FunctionalJournalStorageIdentity<'Key> * FunctionalJournalWrite<'State,'Event> -> Task<bool>` | Compare-and-swap on `ExpectedVersion`; append the batch and optional snapshot atomically, or return `false` without changing storage |
| `IFunctionalJournalStorage.Clear` | `FunctionalJournalStorageIdentity<'Key> -> Task` | Delete the complete journal for that identity |

| Type | Public members |
|---|---|
| `FunctionalJournalStorageIdentity<'Key>` | `GrainTypeName: string`, `GrainId: GrainId`, `Key: 'Key` |
| `FunctionalJournalSnapshot<'State>` | `Version: int`, `State: 'State` |
| `FunctionalJournalRead<'State,'Event>` | `Snapshot: FunctionalJournalSnapshot<'State> option`, `Events: IReadOnlyList<'Event>` |
| `FunctionalJournalWrite<'State,'Event>` | `ExpectedVersion: int`, `Events: IReadOnlyList<'Event>`, `Snapshot: FunctionalJournalSnapshot<'State> option` |
| `FunctionalJournalSnapshotPolicy<'State>` | `Inherit | Disabled | Every of int | When of (int -> 'State -> bool)` |
| `FunctionalJournalSnapshotDefault` | `Disabled | Every of int | When of (FunctionalJournalSnapshotContext -> bool)` |
| `FunctionalJournalSnapshotContext` | `GrainTypeName: string`, `GrainId: GrainId`, `Key: obj`, `Version: int`, `StateType: Type`, `State: obj` |
| `FunctionalJournalSnapshotOptions` | Mutable `Policy`; mutable `ManualSnapshotMaxConflictRetries` (default `3`, must be `>= 0`, counts retries after the first CAS attempt) |
| `FunctionalJournalPermanentStorageException` | Constructors `(message: string)` and `(message: string, innerException: Exception)` |

Ordinary exceptions raised while Orleans' CustomStorage adaptor reads or appends events are
considered transient and remain eligible for its retry loop. A zero-event manual snapshot and
`Clear` call the typed store directly: an ordinary exception fails that call once without
deactivation, and the caller may retry explicitly. Throw
`FunctionalJournalPermanentStorageException` only when the same operation cannot succeed without
an application, configuration, or durable-data change. The functional runtime exits that retry
loop, fails the current journal operation, and requests deactivation so a later call starts with a
fresh activation and durable read. The permanent exception also fails and deactivates on both
direct paths.

Snapshot resolution is deterministic: `context.snapshotNow()` for the successful callback wins;
otherwise the definition's `snapshotPolicy` wins; `Inherit` or no definition policy uses
`FunctionalJournalSnapshotOptions.Policy`; the silo default is `Disabled`. These policies apply
only to definitions with `customStorage`. See [Event Sourcing](/orleans-fsharp/event-sourcing/#custom-storage-and-snapshots).

`ManualSnapshotMaxConflictRetries` applies to a zero-event manual snapshot requested through
`context.snapshotNow()`. The default `3` permits the initial compare-and-swap attempt plus three
retries. Each retry refreshes durable state and recomputes the snapshot; `0` permits only the first
attempt, and exhausting the limit fails the call without writing the snapshot.

### Hosting

| Method | Signature | Description |
|---|---|---|
| `AddFunctionalGrain` | `ISiloBuilder -> FunctionalGrainDefinition<...> -> ISiloBuilder` | Register a hosted definition (`Orleans.FSharp.Runtime`) |
| `AddFunctionalJournaledGrain` | `ISiloBuilder -> FunctionalJournaledGrainDefinition<...> -> ISiloBuilder` | Register a hosted journaled definition (`Orleans.FSharp.Runtime`) |
| `ConfigureFunctionalPersistence` | `ISiloBuilder * Action<FunctionalPersistenceOptions> -> ISiloBuilder` | Configure the independent silo-wide state and journal codec defaults |
| `UseFunctionalFSharpJsonPersistence` | `ISiloBuilder -> ISiloBuilder` | Set both functional persistence defaults to `FSharpJson` |
| `ConfigureFunctionalJournalSnapshots` | `ISiloBuilder * Action<FunctionalJournalSnapshotOptions> -> ISiloBuilder` | Configure the silo-wide rule inherited by custom-storage definitions |
| `UseFunctionalJournalSnapshots` | `ISiloBuilder * every:int -> ISiloBuilder` | Set a positive fixed event-count default |
| `AddFunctionalGrainClient` | `IClientBuilder -> IClientBuilder` | Register the client-side transport on a client-only process (`Orleans.FSharp`) |

Both silo registrations install the client transport too, and both are idempotent per definition
value. A standalone F# host also has to make Orleans see the assemblies it reaches only through F#
-- see [Functional grains](/orleans-fsharp/functional-grains/), "Running a silo from a standalone F# process".

### Scripting

| Name | Signature | Description |
|---|---|---|
| `FunctionalGrainRegistration.of'` | `FunctionalGrainDefinition<...> -> FunctionalGrainRegistration` | Erase an ordinary definition's four type parameters so a heterogeneous list can be passed around |
| `FunctionalScripting.startOnPorts` | `int -> int -> FunctionalGrainRegistration list -> Task<Scripting.SiloHandle>` | Start a one-line localhost silo hosting those definitions, manifest pre-load included |
| `Scripting.startOnPorts` | `int -> int -> Task<SiloHandle>` | The same without functional definitions (`Orleans.FSharp`) |
| `Scripting.shutdown` | `SiloHandle -> Task<unit>` | Stop the silo |

### Calling a functional grain from C#

| Name | Signature | Description |
|---|---|---|
| `FunctionalGrainInterop.For<'TFacade>` | `FunctionalContract * IGrainFactory * obj -> 'TFacade` | Bind a C#-declared facade interface to a functional contract |
| `FunctionalOperationAttribute` | `.ctor(string)`, `OperationId` | Map a facade method to a wire operation ID that differs from its name |

The facade names no definition kind: an ordinary and a journaled definition are indistinguishable
across the boundary. See [Calling from C#](/orleans-fsharp/calling-from-csharp/).

---

## Orleans.FSharp (Core)

Shared Orleans helpers which compose with functional definitions and hosting code.

### Types

| Type | Description |
|---|---|
| `CompoundGuidKey` | Compound key: GUID + string extension |
| `CompoundIntKey` | Compound key: int64 + string extension |
| `Immutable<'T>` | Alias for `Orleans.Concurrency.Immutable<'T>` for zero-copy passing |
| `FSharpIncomingFilter` | Wraps an F# function as `IIncomingGrainCallFilter` |
| `FSharpOutgoingFilter` | Wraps an F# function as `IOutgoingGrainCallFilter` |
| `Migration<'TOld, 'TNew>` | State migration definition from one version to another |
| `AssemblyMarker` | Marker type for assembly discovery |

#### `Filter`

| Function | Signature | Description |
|---|---|---|
| `incoming` | `(IIncomingGrainCallContext -> Task<unit>) -> IIncomingGrainCallFilter` | Create incoming filter |
| `outgoing` | `(IOutgoingGrainCallContext -> Task<unit>) -> IOutgoingGrainCallFilter` | Create outgoing filter |
| `incomingWithAround` | `before -> after -> IIncomingGrainCallFilter` | Before/after incoming filter |
| `outgoingWithAround` | `before -> after -> IOutgoingGrainCallFilter` | Before/after outgoing filter |

Filters see a functional grain as an ordinary Orleans call -- see
[Functional grains](/orleans-fsharp/functional-grains/), "Call filters over a functional grain".

#### `FilterContext`

| Function | Signature | Description |
|---|---|---|
| `methodName` | `IIncomingGrainCallContext -> string` | Get called method name |
| `interfaceType` | `IIncomingGrainCallContext -> Type` | Get grain interface type |
| `grainInstance` | `IIncomingGrainCallContext -> obj option` | Get grain instance |

#### `RequestCtx`

| Function | Signature | Description |
|---|---|---|
| `set` | `string -> obj -> unit` | Set a request context value |
| `get<'T>` | `string -> 'T option` | Get a typed context value |
| `getOrDefault<'T>` | `string -> 'T -> 'T` | Get with fallback |
| `remove` | `string -> unit` | Remove a context value |
| `withValue<'T>` | `string -> obj -> (unit -> Task<'T>) -> Task<'T>` | Scoped context value |

#### `Log`

| Function | Signature | Description |
|---|---|---|
| `logInfo` | `ILogger -> string -> obj[] -> unit` | Log informational message |
| `logWarning` | `ILogger -> string -> obj[] -> unit` | Log warning message |
| `logError` | `ILogger -> exn -> string -> obj[] -> unit` | Log error with exception |
| `logDebug` | `ILogger -> string -> obj[] -> unit` | Log debug message |
| `withCorrelation` | `string -> (unit -> Task<'T>) -> Task<'T>` | Scoped correlation ID |
| `currentCorrelationId` | `unit -> string option` | Get current correlation ID |

#### `Shutdown`

| Function | Signature | Description |
|---|---|---|
| `configureGracefulShutdown` | `TimeSpan -> IHostBuilder -> IHostBuilder` | Set drain timeout |
| `stopHost` | `IHost -> Task<unit>` | Stop host gracefully |
| `onShutdown` | `(CT -> Task<unit>) -> IHostBuilder -> IHostBuilder` | Register shutdown handler |

#### `StateMigration`

| Function | Signature | Description |
|---|---|---|
| `migration<'TOld, 'TNew>` | `int -> int -> ('TOld -> 'TNew) -> Migration<obj, obj>` | Define a migration. The result is erased to `Migration<obj, obj>` so a chain over several state versions is one homogeneous list |
| `applyMigrations<'T>` | `Migration<obj, obj> list -> int -> obj -> 'T` | Apply migration chain (throws on invalid chain) |
| `tryApplyMigrations<'T>` | `Migration<obj, obj> list -> int -> obj -> Result<'T, string list>` | Validate and apply; returns `Ok` or `Error` with messages |
| `validate` | `Migration<obj, obj> list -> string list` | Validate migration chain; empty list means valid |

#### `Serialization`

| Function | Signature | Description |
|---|---|---|
| `fsharpJsonOptions` | `JsonSerializerOptions` | Pre-configured F# JSON options |
| `addFSharpConverters` | `JsonSerializerOptions -> JsonSerializerOptions` | Add F# converters |
| `withConverters` | `JsonConverter list -> JsonSerializerOptions` | Create options with extras |

#### `TaskHelpers`

| Function | Signature | Description |
|---|---|---|
| `taskResult` | `'T -> Task<Result<'T, 'E>>` | Wrap as Ok |
| `taskError` | `'E -> Task<Result<'T, 'E>>` | Wrap as Error |
| `taskMap` | `('T -> 'U) -> Task<Result<'T, 'E>> -> Task<Result<'U, 'E>>` | Map Ok value |
| `taskBind` | `('T -> Task<Result<'U, 'E>>) -> Task<Result<'T, 'E>> -> Task<Result<'U, 'E>>` | Bind Ok value |

#### `GrainResilience` — Polly v8 resilience wrappers

Wrap any grain call in retry, circuit-breaker, and timeout strategies. See [Resilience guide](/orleans-fsharp/resilience/).

| Type | Description |
|---|---|
| `ResilienceOptions` | Record: `MaxRetryAttempts`, `RetryDelay`, `CircuitBreakerThreshold`, `CircuitBreakerDuration`, `Timeout` |

| Function | Signature | Description |
|---|---|---|
| `GrainResilience.defaultOptions` | `ResilienceOptions` | 3 retries · 1s delay · no circuit breaker · no timeout |
| `GrainResilience.retry<'T>` | `int -> TimeSpan -> (unit -> Task<'T>) -> Task<'T>` | Retry N times with delay; each attempt re-invokes the call |
| `GrainResilience.withTimeout<'T>` | `TimeSpan -> (unit -> Task<'T>) -> Task<'T>` | Deadline on one call — raises `TimeoutRejectedException` and abandons the in-flight call (does not cancel it) |
| `GrainResilience.withTimeoutCancellable<'T>` | `TimeSpan -> (CancellationToken -> Task<'T>) -> Task<'T>` | Same deadline, handed to the operation as a token so it can stop instead of being abandoned |
| `GrainResilience.execute<'T>` | `ResilienceOptions -> (unit -> Task<'T>) -> Task<'T>` | Full options: retry + circuit breaker + timeout. The timeout spans the whole sequence; the pipeline is rebuilt per call, so circuit state is not shared |
| `GrainResilience.executeCancellable<'T>` | `ResilienceOptions -> (CancellationToken -> Task<'T>) -> Task<'T>` | Full options for an operation that takes the deadline's token |
| `GrainResilience.buildPipeline<'T>` | `ResilienceOptions -> ResiliencePipeline<'T>` | Build reusable Polly pipeline — the way to get shared circuit state |
| `GrainResilience.circuitBreaker` | `int -> TimeSpan -> ResiliencePipeline` | Shared circuit breaker (non-generic, long-lived) |

#### `GrainBatch` — concurrent fan-out

| Function | Signature | Description |
|---|---|---|
| `GrainBatch.map<'TG,'TR>` | `'TG seq -> ('TG -> Task<'TR>) -> Task<'TR list>` | Fan-out; fails if any call throws |
| `GrainBatch.tryMap<'TG,'TR>` | `'TG seq -> ('TG -> Task<'TR>) -> Task<Result<'TR, exn> list>` | Fan-out; captures individual failures |
| `GrainBatch.aggregate<'TG,'TR,'TA>` | `'TG seq -> ('TG -> Task<'TR>) -> ('TR list -> 'TA) -> Task<'TA>` | Fan-out then reduce |
| `GrainBatch.iter<'TG>` | `'TG seq -> ('TG -> Task) -> Task` | Concurrent fan-out; waits for every call and fails if any throws |
| `GrainBatch.tryIter<'TG>` | `'TG seq -> ('TG -> Task) -> Task<Result<unit, exn> list>` | Concurrent fan-out; waits for every call and captures failures |
| `GrainBatch.choose<'TG,'TR>` | `'TG seq -> ('TG -> Task<'TR option>) -> Task<'TR list>` | Fan-out; filters out None results |
| `GrainBatch.partition<'TG,'TR>` | `'TG seq -> ('TG -> Task<'TR>) -> Task<'TR list * exn list>` | Fan-out; separates successes from failures |

> **Tip**: For 2–4 fixed grain calls, prefer the F# `and!` applicative keyword inside `task {}` — it is more ergonomic and starts every bound call before awaiting any of them, exactly as these do. Use `GrainBatch` when the number of grains is dynamic.

#### Other modules

| Module | Key Function | Description |
|---|---|---|
| `FSharpSerialization.addFSharpSerialization` | `ISiloBuilder -> ISiloBuilder` | Orleans native F# serializer |
| `FSharpBinaryCodecRegistration.addToSerializerBuilder` | `ISerializerBuilder -> ISerializerBuilder` | Register FSharpBinaryCodec manually |
| `immutable` | `'T -> Immutable<'T>` | Wrap as immutable |
| `unwrapImmutable` | `Immutable<'T> -> 'T` | Unwrap immutable |

---

## Orleans.FSharp.Streaming

| Type | Description |
|---|---|
| `StreamRef<'T>` | Typed reference to an Orleans stream (`Provider`, `StreamId`) |
| `StreamSubscription<'T>` | Active stream subscription handle (`Handle`) |
| `StreamHandlers<'T>` | Immutable item/error/completion callback set; item callbacks may receive a sequence token |
| `StreamBatchItem<'T>` | One batch item and its optional provider sequence token |
| `StreamBatchHandlers<'T>` | Immutable batch/error/completion callback set |

#### `Stream`

| Function | Signature | Description |
|---|---|---|
| `getStream<'T>` | `IStreamProvider -> string -> string -> StreamRef<'T>` | Get stream reference |
| `publish<'T>` | `StreamRef<'T> -> 'T -> Task<unit>` | Publish event |
| `publishBatch<'T>` | `StreamRef<'T> -> seq<'T> -> Task<unit>` | Publish a provider-native batch |
| `publishBatchFrom<'T>` | `StreamRef<'T> -> StreamSequenceToken -> seq<'T> -> Task<unit>` | Publish a batch with an explicit starting token |
| `complete<'T>` / `fail<'T>` | `StreamRef<'T> -> Task<unit>` / `StreamRef<'T> -> exn -> Task<unit>` | Forward producer terminal signals; provider semantics are preserved |
| `subscribe<'T>` | `StreamRef<'T> -> ('T -> Task<unit>) -> Task<StreamSubscription<'T>>` | Subscribe with callback |
| `subscribeWithToken<'T>` | `StreamRef<'T> -> ('T -> StreamSequenceToken option -> Task<unit>) -> Task<StreamSubscription<'T>>` | Subscribe with the event's cursor — the way to checkpoint for `subscribeFrom` |
| `subscribeHandlers<'T>` | `StreamRef<'T> -> StreamHandlers<'T> -> Task<StreamSubscription<'T>>` | Subscribe with item/error/completion callbacks |
| `subscribeFiltered<'T>` | `StreamRef<'T> -> string -> StreamHandlers<'T> -> Task<StreamSubscription<'T>>` | Pass filter data to the provider's `IStreamFilter` |
| `subscribeBatch<'T>` | `StreamRef<'T> -> StreamBatchHandlers<'T> -> Task<StreamSubscription<'T>>` | Subscribe through `IAsyncBatchObserver<'T>` |
| `asTaskSeq<'T>` | `StreamRef<'T> -> TaskSeq<'T>` | Pull-based consumption |
| `subscribeFrom<'T>` | `StreamRef<'T> -> StreamSequenceToken -> ('T -> Task<unit>) -> Task<StreamSubscription<'T>>` | Subscribe from token (rewind is inclusive of that event) |
| `subscribeFromWithToken<'T>` | `StreamRef<'T> -> StreamSequenceToken -> ('T -> StreamSequenceToken option -> Task<unit>) -> Task<StreamSubscription<'T>>` | Rewind and keep checkpointing |
| `subscribeFromHandlers<'T>` | `StreamRef<'T> -> StreamSequenceToken -> StreamHandlers<'T> -> Task<StreamSubscription<'T>>` | Rewind with item/error/completion callbacks |
| `subscribeFromFiltered<'T>` | `StreamRef<'T> -> StreamSequenceToken -> string -> StreamHandlers<'T> -> Task<StreamSubscription<'T>>` | Rewind with provider filter data |
| `subscribeBatchFrom<'T>` | `StreamRef<'T> -> StreamSequenceToken -> StreamBatchHandlers<'T> -> Task<StreamSubscription<'T>>` | Rewind with provider-native batch callbacks |
| `unsubscribe<'T>` | `StreamSubscription<'T> -> Task<unit>` | Cancel subscription |
| `getSubscriptions<'T>` | `StreamRef<'T> -> Task<StreamSubscription<'T> list>` | List subscriptions |
| `resumeAll<'T>` | `StreamRef<'T> -> ('T -> Task<unit>) -> Task<unit>` | Resume all subscriptions |
| `resume<'T>` | `StreamSubscription<'T> -> StreamHandlers<'T> -> Task<StreamSubscription<'T>>` | Reattach one item subscription |
| `resumeFrom<'T>` | `StreamSubscription<'T> -> StreamSequenceToken -> StreamHandlers<'T> -> Task<StreamSubscription<'T>>` | Reattach one item subscription from a token |
| `resumeBatch<'T>` | `StreamSubscription<'T> -> StreamBatchHandlers<'T> -> Task<StreamSubscription<'T>>` | Reattach one batch subscription |
| `resumeBatchFrom<'T>` | `StreamSubscription<'T> -> StreamSequenceToken -> StreamBatchHandlers<'T> -> Task<StreamSubscription<'T>>` | Reattach one batch subscription from a token |
| `resumeAllHandlers<'T>` | `StreamRef<'T> -> StreamHandlers<'T> -> Task<unit>` | Reattach all durable subscriptions with item callbacks |
| `resumeAllBatch<'T>` | `StreamRef<'T> -> StreamBatchHandlers<'T> -> Task<unit>` | Reattach all durable subscriptions with batch callbacks |
| `getSequenceToken<'T>` | `StreamSubscription<'T> -> StreamSequenceToken option` | **Deprecated** — carries `[<Obsolete>]` (a warning, not an error) and still returns **always `None`**: `StreamSubscriptionHandle` exposes no token, so there was never anything to return. Replacement: `subscribeWithToken` / `subscribeFromWithToken`, or `context.streamSequenceToken` in an `onStream` hook |

`StreamHandlers.create` / `withToken` create item callbacks; `withError` and `withCompletion`
replace terminal callbacks immutably. `StreamBatchHandlers` exposes the corresponding `create`,
`withError`, and `withCompletion` functions.

A functional definition consumes a stream declaratively with `onStream` instead; see
[Streaming](/orleans-fsharp/streaming/) and [Functional grains](/orleans-fsharp/functional-grains/), "Implicit subscriptions".

---

## Orleans.FSharp.BroadcastChannel

| Type | Description |
|---|---|
| `BroadcastChannelRef<'T>` | Typed reference to a broadcast channel |

#### `BroadcastChannel`

| Function | Signature | Description |
|---|---|---|
| `getChannel<'T>` | `IBroadcastChannelProvider -> string -> string -> BroadcastChannelRef<'T>` | Get channel reference |
| `publish<'T>` | `BroadcastChannelRef<'T> -> 'T -> Task<unit>` | Publish to all subscribers |

---

## Orleans.FSharp.StreamProviders

#### `StreamProviders`

| Function | Signature | Description |
|---|---|---|
| `addEventHubStreams` | `string -> string -> string -> ISiloBuilder -> ISiloBuilder` | Event Hubs provider |
| `addAzureQueueStreams` | `string -> string -> ISiloBuilder -> ISiloBuilder` | Azure Queue provider |
| `addRedisStreams` | `string -> string -> ISiloBuilder -> ISiloBuilder` | Redis Streams provider (**experimental**: needs a prerelease `Microsoft.Orleans.Streaming.Redis`) |

---

## Orleans.FSharp.GrainDirectory

| Type | Description |
|---|---|
| `GrainDirectoryProvider` | Default, Redis, AzureStorage, Custom |

#### `GrainDirectory`

| Function | Signature | Description |
|---|---|---|
| `configure` | `GrainDirectoryProvider -> ISiloBuilder -> ISiloBuilder` | Configure grain directory |

---

## Orleans.FSharp.Kubernetes

#### `Kubernetes`

| Function | Signature | Description |
|---|---|---|
| `useKubernetesClustering` | `ISiloBuilder -> ISiloBuilder` | Enable K8s clustering |
| `useKubernetesClusteringWithNamespace` | `string -> ISiloBuilder -> ISiloBuilder` | K8s with custom namespace |

---

## Orleans.FSharp.Runtime

### Types

| Type | Description |
|---|---|
| `SiloConfig` | Immutable silo configuration record |
| `ClientConfig` | Immutable client configuration record |
| `FSharpSerialization` | Explicit generalized policy: `Binary`, `Json`, or binary with JSON for unsupported CLR types |
| `ClusteringMode` | Localhost, RedisClustering, AzureTableClustering, AdoNetClustering, CustomClustering |
| `ClientClusteringMode` | Localhost, StaticGateway, Custom |
| `StorageProvider` | Memory, RedisStorage, AzureBlobStorage, AzureTableStorage, AdoNetStorage, CosmosStorage, DynamoDbStorage, CustomStorage |
| `StreamProvider` | MemoryStream, PersistentStream, CustomStream |
| `ReminderProvider` | MemoryReminder, RedisReminder, CustomReminder |
| `TlsConfig` | TlsSubject, TlsCertificate, MutualTlsSubject, MutualTlsCertificate |
| `DashboardConfig` | DashboardDefaults, DashboardWithOptions |

### Computation expressions

| CE | Builder | Description |
|---|---|---|
| `siloConfig { }` | `SiloConfigBuilder` | Configure an Orleans silo |
| `clientConfig { }` | `ClientConfigBuilder` | Configure an Orleans client |

See [Silo configuration](/orleans-fsharp/silo-configuration/) and [Client configuration](/orleans-fsharp/client-configuration/)
for the full keyword lists.

#### Generalized serialization

| Name | Signature | Description |
|---|---|---|
| `FSharpSerialization.Binary` | `FSharpSerialization` | Compact F# binary generalized codec |
| `FSharpSerialization.Json` | `FSharpSerialization` | F#-aware JSON as the primary generalized codec |
| `FSharpSerialization.BinaryWithJsonForUnsupportedTypes` | `FSharpSerialization` | Binary when supported; otherwise F# JSON |
| `FSharpSerialization.forUnsupportedTypes` | `FSharpSerialization -> FSharpSerialization -> FSharpSerialization` | Compose binary primary with JSON selected only for CLR types unsupported by binary |
| `useFSharpBinarySerialization` | silo/client CE operation | Select `Binary` |
| `useFSharpJsonSerialization` | silo/client CE operation | Select `Json` |
| `useFSharpSerialization` | `FSharpSerialization ->` silo/client CE operation | Select an explicit composed policy |

Only one policy can be selected per builder. Unsupported-type selection happens before writing; it
does not retry with JSON after a serialization exception. See [Serialization](/orleans-fsharp/serialization/#explicit-generalized-serializers).

#### `SiloConfig`

| Function | Signature | Description |
|---|---|---|
| `Default` | `SiloConfig` | Empty default configuration |
| `validate` | `SiloConfig -> string list` | Validate configuration |
| `applyToSiloBuilder` | `SiloConfig -> ISiloBuilder -> unit` | Apply to silo builder |
| `applyToHost` | `SiloConfig -> HostApplicationBuilder -> unit` | Apply to host |

Both `applyTo*` entry points force the manifest pre-load a standalone F# host needs.

#### `ClientConfig`

| Function | Signature | Description |
|---|---|---|
| `Default` | `ClientConfig` | Empty default configuration |
| `validate` | `ClientConfig -> string list` | Validate configuration |
| `applyToBuilder` | `ClientConfig -> IClientBuilder -> unit` | Apply to client builder |
| `applyToHost` | `ClientConfig -> HostApplicationBuilder -> unit` | Apply to host |
| `build` | `ClientConfig -> IHost * IClusterClient` | Build and return client |

---

## Orleans.FSharp.Testing

### Types

| Type | Description |
|---|---|
| `TestHarness` | `Cluster`, `Client`, `LogFactory` — a TestCluster with log capture |
| `WebTestHarness` | The same plus an `HttpClient` against a live web host |
| `WebUnitTestHarness` | `HttpClient` + `LogFactory`, no cluster |
| `MockGrainFactory` | Mock `IGrainFactory` for unit tests |
| `CapturingLogger` / `CapturingLoggerFactory` | In-memory `ILogger` and its factory |
| `CapturedLogEntry` | `Timestamp`, `Level`, `Template`, `Properties`, `Exception` |

#### `TestHarness`

| Function | Signature | Description |
|---|---|---|
| `createTestCluster` | `unit -> Task<TestHarness>` | Create default test cluster |
| `createTestClusterWith` | `SiloConfig -> Task<TestHarness>` | Create with custom config |
| `getGrainByString<'T>` | `TestHarness -> string -> GrainRef<'T, string>` | Get grain by string key |
| `getGrainByInt64<'T>` | `TestHarness -> int64 -> GrainRef<'T, int64>` | Get grain by int64 key |
| `getGrainByGuid<'T>` | `TestHarness -> Guid -> GrainRef<'T, Guid>` | Get grain by GUID key |
| `captureLogs` | `TestHarness -> CapturedLogEntry list` | Get all captured logs |
| `reset` | `TestHarness -> Task<unit>` | Clear captured logs |
| `dispose` | `TestHarness -> Task<unit>` | Stop and dispose cluster |

#### `WebTestHarness`

| Function | Signature | Description |
|---|---|---|
| `create` | `(ISiloBuilder -> unit) -> (IWebHostBuilder -> unit) -> Task<WebTestHarness>` | Cluster + web host |
| `createDefault` | `(IWebHostBuilder -> unit) -> Task<WebTestHarness>` | Default cluster + web host |
| `createWithFactory` | `IGrainFactory -> (IWebHostBuilder -> unit) -> Task<WebUnitTestHarness>` | Web host over a supplied factory |
| `createWithMockFactory` | `(MockGrainFactory -> MockGrainFactory) -> (IWebHostBuilder -> unit) -> Task<WebUnitTestHarness>` | Web host over a mock factory |
| `captureLogs` / `captureUnitLogs` | `harness -> CapturedLogEntry list` | Captured logs |
| `reset` / `resetUnit`, `dispose` / `disposeUnit` | `harness -> Task<unit>` | Reset and teardown |

#### `GrainMock`

| Function | Signature | Description |
|---|---|---|
| `create` | `unit -> MockGrainFactory` | Create empty mock factory |
| `withGrain<'T>` | `obj -> 'T -> MockGrainFactory -> MockGrainFactory` | Register a mock grain implementation |

#### `GrainArbitrary`

| Function | Signature | Description |
|---|---|---|
| `forState<'T>` | `unit -> Arbitrary<'T>` | Auto-generate Arbitrary for state type |
| `forCommands<'T>` | `unit -> Arbitrary<'T list>` | Auto-generate Arbitrary for command sequences |

#### `FsCheckHelpers`

| Function | Signature | Description |
|---|---|---|
| `commandSequenceArb<'T>` | `unit -> Arbitrary<'T list>` | Non-empty command list Arbitrary |
| `stateMachineProperty` | `'State -> ('State -> 'Cmd -> 'State) -> ('State -> bool) -> 'Cmd list -> bool` | State machine invariant check |

#### `LogCapture`

| Function | Signature | Description |
|---|---|---|
| `create` | `unit -> CapturingLoggerFactory` | Create capturing factory |
| `captureLogs` | `CapturingLoggerFactory -> CapturedLogEntry list` | Get all entries |

A functional definition is tested against a real `TestCluster` rather than a mock factory -- see
[Testing](/orleans-fsharp/testing/).

---

## Orleans.FSharp.Analyzers

Compile-time F# analyzer package — install in your grain projects to catch `async {}` misuse at build time.

```bash
dotnet add package Orleans.FSharp.Analyzers
```

### Diagnostics

| Code | Severity | Message | Description |
|---|---|---|---|
| `OF0001` | Warning | Use `task { }` instead of `async { }` | Detects `async { }` computation expressions in Orleans grain code |

### Types

| Type | Description |
|---|---|
| `AllowAsyncAttribute` | Suppresses OF0001 on the annotated binding. Apply when `async { }` is genuinely required (e.g., interop with `Async<'T>` APIs). |

### Usage

```fsharp
// Triggers OF0001 — use task { } in grain handlers
let invalidWork () =
    async { return 0 }  // ⚠️ OF0001

// Suppress when async is genuinely needed
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer

[<AllowAsync>]
let allowedInterop () =
    async { return 0 }  // ✅ suppressed
```

See [Analyzers guide](/orleans-fsharp/analyzers/) for full documentation.

## Legacy API

The original authoring surface is retained in the separate [Legacy API Reference](/orleans-fsharp/legacy/api-reference/).
