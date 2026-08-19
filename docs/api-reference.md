# API Reference

**Quick reference for the public modules, types, and functions in Orleans.FSharp.**

Reference tables, not tutorials. Every section names the guide that carries the semantics; look
there for what a thing *means* and here for what it is *called*.

The [functional grain runtime](#functional-grain-runtime) is the current authoring model and comes
first. The `grain { }` cluster -- `GrainBuilder`/`grain`, `GrainDefinition`, the old `GrainContext`,
`AdditionalStateSpec`, `FSharpGrainAttribute`, `IFSharpGrain*`, the `FSharpGrainHandle*` types, the
`FSharpGrain.*` module, `AddFSharpGrain(sFromAssembly)`, `Timers` and `Reminder` -- carries
`[<Obsolete>]` (warning, not error) and is kept, in full, in the "Deprecated: the `grain { }`
cluster" section at the bottom of this page. Everything between the two is neither: it is the
surface both models share.

**Where the names in the functional tables come from.** Every custom-operation name and every
context member below is pinned by `tests/Orleans.FSharp.Tests/FunctionalSurfaceTests.fs`, which
reflects over the builders and the context type and asserts the exact set. A name that appears here
and not there, or there and not here, is a bug in one of the two.

---

## Functional grain runtime

**The current grain authoring model.** A user-authored API record instead of a C# CodeGen
interface, a contract that declares the wire and delivery policy, and a definition that binds
handlers to it. See [Functional Grain Runtime](functional-grains.md) for the full guide.

### Entry points

| Function | Signature | Description |
|---|---|---|
| `grainContract<'Actor, 'Key, 'Api> ()` | `unit -> GrainContractBuilder<'Actor,'Key,'Api>` | Opens the contract CE |
| `contract<'Key, 'Api> ()` | `unit -> GrainContractBuilder<'Api,'Key,'Api>` | Short form: the API record is its own actor brand ([details](functional-grains.md#the-short-form-the-api-record-as-its-own-brand)) |
| `grainFor contract` | `GrainContract<...> -> FunctionalGrainDefinitionBuilder<...>` | Opens the definition CE |
| `journaledGrainFor contract` | `GrainContract<...> -> FunctionalJournaledGrainDefinitionBuilder<...>` | Opens the journaled definition CE ([Event Sourcing](event-sourcing.md)) |
| `observerContract<'Brand, 'Api> ()` | `unit -> ObserverContractBuilder<'Brand,'Api>` | Opens the observer contract CE |
| `FunctionalGrain.ref` | `contract -> IGrainFactory -> 'Key -> 'Api` | Binds a typed API record |
| `FunctionalGrain.rawRef` | `contract -> IGrainFactory -> 'Key -> FunctionalGrainRef<'Actor,'Key,'Api>` | Binds the typed wrapper |
| `FunctionalGrain.streamId` | `contract -> string -> 'Key -> StreamId` | Stream id whose key is the contract's own grain-key bytes |
| `FunctionalGrain.channelId` | `contract -> string -> 'Key -> ChannelId` | The same for a broadcast channel |

`FunctionalGrain` is a static class, so `ref`/`rawRef` generalize only where F# lets a static-class
application generalize -- see [Functional grains](functional-grains.md), "The `FunctionalGrain`
static-class inference rule".

### Contract builder — `grainContract<'Actor, 'Key, 'Api> () { }`

| Keyword | Signature | Description |
|---|---|---|
| `grainType` | `string` | The wire `GrainType` string -- routing and storage identity. Optional; see [Functional grains](functional-grains.md), "Optional grainType" |
| `version` | `int` | Contract version -- matched exactly unless `acceptsVersions` widens it. Defaults to `1` |
| `stringKey` / `guidKey` / `int64Key` | — | Native key codec: the domain key type *is* the Orleans key type |
| `stringKeyMapped` / `guidKeyMapped` / `int64KeyMapped` | `('Key -> K)` `(K -> 'Key)` | Mapped key codec over a domain key type |
| `guidCompoundKey` / `int64CompoundKey` | — | Native compound key (Guid/int64 + string extension) |
| `guidCompoundKeyMapped` / `int64CompoundKeyMapped` | `('Key -> K * string)` `(K -> string -> 'Key)` | Mapped compound key |
| `readOnly` | `selector` | The handler's returned state is discarded; interleaves with other read-only calls |
| `oneWay` | `selector` | The caller's `Task` completes once the message enters the local send path |
| `alwaysInterleave` | `selector` | Interleaves regardless of `readOnly`/`oneWay`; also state-neutral. Rejected at sealing when the contract declares `reentrant` or `mayInterleave` |
| `transactional` | `Orleans.TransactionOption -> selector` | Orleans transaction policy for one operation ([Functional grains](functional-grains.md), "Distributed ACID transactions"). Orleans' own enum, **not** this library's `Orleans.FSharp.Transactions.TransactionOption` DU |
| `operationId` | `string -> selector` | Override an operation's wire ID, decoupling it from the F# field name. A second overload takes a `StreamSelector` |
| `sinceVersion` | `int -> selector` | The version an operation was introduced at; an admitted older call is refused for it by name. A second overload takes a `StreamSelector` |
| `reentrant` | — | Whole-grain reentrancy -- every request may enter a busy activation. Does **not** make whole-state replacement concurrency-safe |
| `mayInterleave` | `(IFunctionalRequestMetadata -> bool)` | Per-request interleave predicate over protocol metadata only; mutually exclusive with `reentrant`. Orleans consults it for the *running* request too |
| `acceptsVersions` | `VersionPolicy` | `Exact` (default) or `BackwardCompatible n` -- which request versions this definition admits |

`operationId` and `sinceVersion` are the only two per-operation declarations that compose with a
streaming field; the four admission policies are refused at sealing.

Every API field takes exactly one argument; a multi-input operation groups its inputs in a tuple
(`typing: (string * bool) -> Task<unit>`). A field spelled curried fails contract construction. See
[Functional grains](functional-grains.md), "One operation, one argument".

### Definition builder — `grainFor contract { }`

| Keyword | Handler signature | Description |
|---|---|---|
| `defaultState` | `unit -> 'State` | Ephemeral state factory, called once per activation |
| `initialState` | `'Key -> 'State` | Key-aware ephemeral state factory |
| `handle` | `selector` + `Handler<'Actor,'Key,'State,'Arg,'Reply>` | Attach a handler to one API operation |
| `handleStream` | `streamSelector` + `StreamHandler<'Actor,'Key,'State,'Arg,'Item>` | Attach a handler to one server-streaming operation ([Streaming replies](streaming-replies.md)) |
| `stateFrom` | `PersistentStateRef<'State>` | Attach the primary persistent-state holder |
| `usePersistentState` | `PersistentStateRef<'S>` + `('Key -> 'S)` | Attach an additional named persistent-state facet (repeatable) |
| `transactionalStateFrom` | `TransactionalStateRef<'S>` + `('Key -> 'S)` | Attach a transactional facet (repeatable) |
| `collectionAge` | `TimeSpan` | Idle-deactivation threshold override |
| `placement` | `PlacementStrategy` | `Random` / `PreferLocal` / `ActivationCountBased` / `ResourceOptimized` |
| `statelessWorker` | `int` | Stateless-worker placement with a max-local-workers cap |
| `onActivate` | `ActivateHook<'Actor,'Key,'State>` | Activation hook; its returned state is published in memory |
| `onDeactivate` | `DeactivateHook<'Actor,'Key,'State>` | Deactivation hook; no replacement state |
| `onLifecycle` | `LifecycleStage` + `LifecycleHook<'Actor,'Key>` | Hook a numbered Orleans grain-lifecycle stage |
| `onReminder` | `string` + `TimeSpan` (due) + `TimeSpan` (period) + `ReminderHook<...>` | Declare a reminder |
| `onTimer` | `string` + `GrainTimerCreationOptions` + `TimerHook<...>` | Declare a timer |
| `onStream` | `string` (provider) + `string` (namespace) + `StreamHook<...>` | Implicit stream subscription |
| `onBroadcast` | `string` (provider) + `string` (namespace) + `StreamHook<...>` | Implicit broadcast-channel subscription |

### Journaled definition builder — `journaledGrainFor contract { }`

A deliberate **subset** of the operations above plus two of its own. Every absence is deliberate: a
journal cannot honour a whole-state-replacement hook, cannot be a transaction participant, and
cannot be shared by the many activations of a stateless worker. See
[Event Sourcing](event-sourcing.md).

| Keyword | Handler signature | Description |
|---|---|---|
| `initialEventState` | `'Key -> 'State` | The seed the journal folds onto. **Required, and first** |
| `apply` | `'State -> 'Event -> 'State` | The pure fold. **Required, and second** -- it introduces the event type |
| `logProvider` | `string` | The registered log-consistency provider. **Required** |
| `journalStorage` | `string` | The grain storage the provider writes through; defaults to the silo's default `IGrainStorage` |
| `handle` | `selector` + `JournaledHandler<'Actor,'Key,'State,'Event,'Arg,'Reply>` | A handler returning `events, reply` |
| `handleStream` | `streamSelector` + `StreamHandler<...>` | A streaming operation; raises no events |
| `onActivate` | `JournaledActivateHook<'Actor,'Key,'State>` | Runs after replay; returns no state |
| `onDeactivate` | `JournaledDeactivateHook<'Actor,'Key,'State>` | Deactivation hook |
| `collectionAge` | `TimeSpan` | Idle-deactivation threshold override |
| `placement` | `PlacementStrategy` | As above. `statelessWorker` has no journaled form at all: many activations of one grain cannot share a journal |

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
| `delayDeactivation(span)` | `TimeSpan -> unit` | Postpone idle collection |
| `persistentState(ref)` | `PersistentStateRef<'S> -> IPersistentState<'S>` | Look up an attached persistent-state facet |
| `transactionalState(ref)` | `TransactionalStateRef<'S> -> FunctionalTransactionalState<'S>` | Look up an attached transactional facet |
| `journalVersion` | `int` | The confirmed journal length, as it was when the turn started |
| `raiseConditional(events)` | `'Event list -> Task<bool>` | Append and confirm *inside* the turn; reports whether it was accepted |
| `tryGetRequestContext<'T>(name)` | `string -> 'T option` | Typed Orleans request-context read |
| `setRequestContext(name, value)` | `string -> 'V -> unit` | Request-context write |
| `removeRequestContext(name)` | `string -> unit` | Request-context removal |

`journalVersion` and `raiseConditional` live on the one context type rather than on a journaled
variant of it, and both refuse with a definition-stage diagnostic on an ordinary `grainFor`
definition.

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
| `StreamHandler<'Actor,'Key,'State,'Argument,'Item>` | `context -> 'State -> 'Argument -> IAsyncEnumerable<'Item>` |
| `JournaledHandler<'Actor,'Key,'State,'Event,'Argument,'Reply>` | `context -> 'State -> 'Argument -> Task<'Event list * 'Reply>` |
| `ActivateHook<'Actor,'Key,'State>` | `context -> 'State -> Task<'State>` |
| `DeactivateHook<'Actor,'Key,'State>` | `context -> DeactivationReason -> 'State -> Task<unit>` |
| `JournaledActivateHook<'Actor,'Key,'State>` | `context -> 'State -> Task<unit>` |
| `JournaledDeactivateHook<'Actor,'Key,'State>` | `context -> DeactivationReason -> 'State -> Task<unit>` |
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

The descriptor's `(stateName, providerName, storedType)` triple is its logical identity, and it is
durable identity -- see [Functional grains](functional-grains.md), "Persistence model".

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
[Streaming replies](streaming-replies.md).

### Observers

A handler record whose every field is `'Msg -> Task<unit>`. Push to a client-hosted observer with
no application code generation; see [Functional grains](functional-grains.md), "Push to clients:
functional observers".

#### `observerContract<'Brand, 'Api> () { }`

| Keyword | Signature | Description |
|---|---|---|
| `observerType` | `string` | Wire identity of the observer; defaults to the brand's simple CLR name |
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
| `FunctionalGrainContext<'Actor, 'Key>` | Per-invocation context (members above) |
| `FunctionalGrainRef<'Actor, 'Key, 'Api>` | Typed reference wrapper (members above) |
| `ObserverContract<'Brand, 'Api>` | Sealed result of `observerContract { }`; exposes `ObserverTypeName` and `Version` |
| `FunctionalObserverHandle<'Brand, 'Api>` | Serializable typed handle to a client-hosted observer; an operation argument or a tuple element, never an F# record field |
| `PersistentStateRef<'State>` | Immutable descriptor returned by `PersistentState.create` |
| `TransactionalStateRef<'State>` | Immutable descriptor returned by `TransactionalState.create` |
| `FunctionalTransactionalState<'State>` | The invocation-bound transactional facade |
| `PlacementStrategy` | `Random`, `PreferLocal`, `ActivationCountBased`, `ResourceOptimized` |
| `VersionPolicy` | `Exact`, `BackwardCompatible of int` |
| `LifecycleStage` | `First`, `SetupState`, `Activate`, `Last` (`Activate` is rejected by `onLifecycle`; use `onActivate`) |
| `IFunctionalRequestMetadata` | `mayInterleave`'s argument: `GrainType`, `ContractVersion`, `OperationId`, `IsReadOnly`, `IsOneWay`, `IsAlwaysInterleave`, `PayloadLength` |
| `FunctionalGrainTransportOptions` | Transport limits; `DefaultMaxPayloadBytes` is 16 MiB |

### Hosting

| Method | Signature | Description |
|---|---|---|
| `AddFunctionalGrain` | `ISiloBuilder -> FunctionalGrainDefinition<...> -> ISiloBuilder` | Register a hosted definition (`Orleans.FSharp.Runtime`) |
| `AddFunctionalJournaledGrain` | `ISiloBuilder -> FunctionalJournaledGrainDefinition<...> -> ISiloBuilder` | Register a hosted journaled definition (`Orleans.FSharp.Runtime`) |
| `AddFunctionalGrainClient` | `IClientBuilder -> IClientBuilder` | Register the client-side transport on a client-only process (`Orleans.FSharp`) |

Both silo registrations install the client transport too, and both are idempotent per definition
value. A standalone F# host also has to make Orleans see the assemblies it reaches only through F#
-- see [Functional grains](functional-grains.md), "Running a silo from a standalone F# process".

### Scripting

| Name | Signature | Description |
|---|---|---|
| `FunctionalGrainRegistration.of'` | `FunctionalGrainDefinition<...> -> FunctionalGrainRegistration` | Erase an ordinary definition's four type parameters so a heterogeneous list can be passed around |
| `FunctionalScripting.startOnPorts` | `int -> int -> FunctionalGrainRegistration list -> Task<Scripting.SiloHandle>` | Start a one-line localhost silo hosting those definitions, manifest pre-load included |
| `Scripting.startOnPorts` | `int -> int -> Task<SiloHandle>` | The same without functional definitions (`Orleans.FSharp`) |
| `Scripting.getGrain<'T>` | `SiloHandle -> int64 -> 'T` | Get a C# CodeGen grain by int64 key |
| `Scripting.shutdown` | `SiloHandle -> Task<unit>` | Stop the silo |

### Calling a functional grain from C#

| Name | Signature | Description |
|---|---|---|
| `FunctionalGrainInterop.For<'TFacade>` | `FunctionalContract * IGrainFactory * obj -> 'TFacade` | Bind a C#-declared facade interface to a functional contract |
| `FunctionalOperationAttribute` | `.ctor(string)`, `OperationId` | Map a facade method to a wire operation ID that differs from its name |

The facade names no definition kind: an ordinary and a journaled definition are indistinguishable
across the boundary. See [Calling from C#](calling-from-csharp.md).

---

## Orleans.FSharp (Core)

Surface that is neither part of the functional runtime nor part of the deprecated `grain { }`
cluster: it serves both models, or the C# CodeGen path.

### Types

| Type | Description |
|---|---|
| `GrainRef<'TInterface, 'TKey>` | Type-safe reference to a C# CodeGen Orleans grain |
| `CompoundGuidKey` | Compound key: GUID + string extension |
| `CompoundIntKey` | Compound key: int64 + string extension |
| `Immutable<'T>` | Alias for `Orleans.Concurrency.Immutable<'T>` for zero-copy passing |
| `FSharpIncomingFilter` | Wraps an F# function as `IIncomingGrainCallFilter` |
| `FSharpOutgoingFilter` | Wraps an F# function as `IOutgoingGrainCallFilter` |
| `Migration<'TOld, 'TNew>` | State migration definition from one version to another |
| `AssemblyMarker` | Marker type for assembly discovery |

#### `GrainRef`

| Function | Signature | Description |
|---|---|---|
| `ofString<'T>` | `IGrainFactory -> string -> GrainRef<'T, string>` | Create ref by string key |
| `ofGuid<'T>` | `IGrainFactory -> Guid -> GrainRef<'T, Guid>` | Create ref by GUID key |
| `ofInt64<'T>` | `IGrainFactory -> int64 -> GrainRef<'T, int64>` | Create ref by int64 key |
| `ofGuidCompound<'T>` | `IGrainFactory -> Guid -> string -> GrainRef<'T, CompoundGuidKey>` | Compound GUID key |
| `ofIntCompound<'T>` | `IGrainFactory -> int64 -> string -> GrainRef<'T, CompoundIntKey>` | Compound int64 key |
| `invoke` | `GrainRef -> ('T -> Task<'R>) -> Task<'R>` | Call a grain method |
| `invokeOneWay` | `GrainRef -> ('T -> Task) -> Task` | Fire-and-forget call |
| `invokeWithTimeout` | `GrainRef -> TimeSpan -> ('T -> Task<'R>) -> Task<'R>` | Call with timeout |
| `unwrap` | `GrainRef -> 'T` | Get the underlying grain proxy |
| `key` | `GrainRef -> 'TKey` | Get the primary key |

#### `Filter`

| Function | Signature | Description |
|---|---|---|
| `incoming` | `(IIncomingGrainCallContext -> Task<unit>) -> IIncomingGrainCallFilter` | Create incoming filter |
| `outgoing` | `(IOutgoingGrainCallContext -> Task<unit>) -> IOutgoingGrainCallFilter` | Create outgoing filter |
| `incomingWithAround` | `before -> after -> IIncomingGrainCallFilter` | Before/after incoming filter |
| `outgoingWithAround` | `before -> after -> IOutgoingGrainCallFilter` | Before/after outgoing filter |

Filters see a functional grain as an ordinary Orleans call -- see
[Functional grains](functional-grains.md), "Call filters over a functional grain".

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

#### `GrainState`

| Function | Signature | Description |
|---|---|---|
| `read<'T>` | `IPersistentState<'T> -> Task<'T>` | Read from storage |
| `write<'T>` | `IPersistentState<'T> -> 'T -> Task<unit>` | Write to storage |
| `clear<'T>` | `IPersistentState<'T> -> Task<unit>` | Clear storage |
| `current<'T>` | `IPersistentState<'T> -> 'T` | Get in-memory value |

#### `Observer` — C# CodeGen observers

| Function | Signature | Description |
|---|---|---|
| `createRef<'T>` | `IGrainFactory -> 'T -> 'T` | Create observer reference |
| `deleteRef<'T>` | `IGrainFactory -> 'T -> unit` | Delete observer reference |
| `subscribe<'T>` | `IGrainFactory -> 'T -> IDisposable` | Subscribe with auto-cleanup |

`FSharpObserverManager<'T>` (`.ctor(TimeSpan)`, `Subscribe`, `Unsubscribe`, `Notify`,
`NotifyAsync`, `Count`) manages a set of them. These need a C#-declared observer interface; for the
codegen-free equivalent see [Observers](#observers) above.

#### `Shutdown`

| Function | Signature | Description |
|---|---|---|
| `configureGracefulShutdown` | `TimeSpan -> IHostBuilder -> IHostBuilder` | Set drain timeout |
| `stopHost` | `IHost -> Task<unit>` | Stop host gracefully |
| `onShutdown` | `(CT -> Task<unit>) -> IHostBuilder -> IHostBuilder` | Register shutdown handler |

#### `StateMigration`

| Function | Signature | Description |
|---|---|---|
| `migration<'TOld, 'TNew>` | `int -> int -> ('TOld -> 'TNew) -> Migration<'TOld, 'TNew>` | Define a migration |
| `applyMigrations<'T>` | `Migration list -> int -> obj -> 'T` | Apply migration chain (throws on invalid chain) |
| `tryApplyMigrations<'T>` | `Migration list -> int -> obj -> Result<'T, string list>` | Validate and apply; returns `Ok` or `Error` with messages |
| `validate` | `Migration list -> string list` | Validate migration chain; empty list means valid |

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

Wrap any grain call in retry, circuit-breaker, and timeout strategies. See [Resilience guide](resilience.md).

| Type | Description |
|---|---|
| `ResilienceOptions` | Record: `MaxRetryAttempts`, `RetryDelay`, `CircuitBreakerThreshold`, `CircuitBreakerDuration`, `Timeout` |

| Function | Signature | Description |
|---|---|---|
| `GrainResilience.defaultOptions` | `ResilienceOptions` | 3 retries · 1s delay · no circuit breaker · no timeout |
| `GrainResilience.retry<'T>` | `int -> TimeSpan -> (unit -> Task<'T>) -> Task<'T>` | Retry N times with delay |
| `GrainResilience.withTimeout<'T>` | `TimeSpan -> (unit -> Task<'T>) -> Task<'T>` | Enforce per-call deadline |
| `GrainResilience.execute<'T>` | `ResilienceOptions -> (unit -> Task<'T>) -> Task<'T>` | Full options: retry + circuit breaker + timeout |
| `GrainResilience.buildPipeline<'T>` | `ResilienceOptions -> ResiliencePipeline<'T>` | Build reusable Polly pipeline |
| `GrainResilience.circuitBreaker` | `int -> TimeSpan -> ResiliencePipeline` | Shared circuit breaker (non-generic, long-lived) |

#### `GrainBatch` — concurrent fan-out

| Function | Signature | Description |
|---|---|---|
| `GrainBatch.map<'TG,'TR>` | `'TG seq -> ('TG -> Task<'TR>) -> Task<'TR list>` | Fan-out; fails if any call throws |
| `GrainBatch.tryMap<'TG,'TR>` | `'TG seq -> ('TG -> Task<'TR>) -> Task<Result<'TR, exn> list>` | Fan-out; captures individual failures |
| `GrainBatch.aggregate<'TG,'TR,'TA>` | `'TG seq -> ('TG -> Task<'TR>) -> ('TR list -> 'TA) -> Task<'TA>` | Fan-out then reduce |
| `GrainBatch.iter<'TG>` | `'TG seq -> ('TG -> Task) -> Task` | Concurrent fire-and-forget; fails if any throws |
| `GrainBatch.tryIter<'TG>` | `'TG seq -> ('TG -> Task) -> Task<Result<unit, exn> list>` | Concurrent fire-and-forget; captures failures |
| `GrainBatch.choose<'TG,'TR>` | `'TG seq -> ('TG -> Task<'TR option>) -> Task<'TR list>` | Fan-out; filters out None results |
| `GrainBatch.partition<'TG,'TR>` | `'TG seq -> ('TG -> Task<'TR>) -> Task<'TR list * exn list>` | Fan-out; separates successes from failures |

> **Tip**: For 2–4 fixed grain calls, prefer the F# `and!` applicative keyword inside `task {}` — it is more ergonomic and compiles to the same `Task.WhenAll` pattern. Use `GrainBatch` when the number of grains is dynamic.

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

#### `Stream`

| Function | Signature | Description |
|---|---|---|
| `getStream<'T>` | `IStreamProvider -> string -> string -> StreamRef<'T>` | Get stream reference |
| `publish<'T>` | `StreamRef<'T> -> 'T -> Task<unit>` | Publish event |
| `subscribe<'T>` | `StreamRef<'T> -> ('T -> Task<unit>) -> Task<StreamSubscription<'T>>` | Subscribe with callback |
| `asTaskSeq<'T>` | `StreamRef<'T> -> TaskSeq<'T>` | Pull-based consumption |
| `subscribeFrom<'T>` | `StreamRef<'T> -> StreamSequenceToken -> ('T -> Task<unit>) -> Task<StreamSubscription<'T>>` | Subscribe from token |
| `unsubscribe<'T>` | `StreamSubscription<'T> -> Task<unit>` | Cancel subscription |
| `getSubscriptions<'T>` | `StreamRef<'T> -> Task<StreamSubscription<'T> list>` | List subscriptions |
| `resumeAll<'T>` | `StreamRef<'T> -> ('T -> Task<unit>) -> Task<unit>` | Resume all subscriptions |
| `getSequenceToken<'T>` | `StreamSubscription<'T> -> StreamSequenceToken option` | **Always `None`** — a permanent stub, because `StreamSubscriptionHandle` does not expose the token. Read `context.streamSequenceToken` in an `onStream` hook instead |

A functional definition consumes a stream declaratively with `onStream` instead; see
[Streaming](streaming.md) and [Functional grains](functional-grains.md), "Implicit subscriptions".

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

## Orleans.FSharp.Versioning

| Type | Description |
|---|---|
| `CompatibilityStrategy` | `BackwardCompatible`, `StrictVersion`, `AllVersions` |
| `VersionSelectorStrategy` | `AllCompatibleVersions`, `LatestVersion`, `MinimumVersion` |

#### `Versioning`

| Function | Signature | Description |
|---|---|---|
| `compatibilityStrategyName` | `CompatibilityStrategy -> string` | Convert to Orleans name |
| `versionSelectorStrategyName` | `VersionSelectorStrategy -> string` | Convert to Orleans name |

These configure Orleans' interface-version selection for C# CodeGen grains. A functional contract
carries its own version instead -- `version`, `acceptsVersions`, `sinceVersion` above.

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

## Orleans.FSharp.Transactions

Thin wrappers over Orleans' own `ITransactionalState<'T>`, for a C# CodeGen grain that injects one
with `[<TransactionalState>]`. On the functional runtime use `transactionalStateFrom` and
`context.transactionalState` instead.

| Type | Description |
|---|---|
| `TransactionOption` | An F# DU -- `Create`, `Join`, `CreateOrJoin`, `Supported`, `NotAllowed`, `Suppress` -- with `TransactionOption.toOrleans` converting it to Orleans' own enum. A contract's `transactional` operation takes **Orleans' enum**, not this DU |

#### `TransactionalState` (`Orleans.FSharp.Transactions`)

| Function | Signature | Description |
|---|---|---|
| `read<'T>` | `ITransactionalState<'T> -> Task<'T>` | Read transactional state |
| `update<'T>` | `('T -> 'T) -> ITransactionalState<'T> -> Task<unit>` | Update transactional state |
| `performRead<'T, 'R>` | `('T -> 'R) -> ITransactionalState<'T> -> Task<'R>` | Read with projection |

---

## Orleans.FSharp.Runtime

### Types

| Type | Description |
|---|---|
| `SiloConfig` | Immutable silo configuration record |
| `ClientConfig` | Immutable client configuration record |
| `ClusteringMode` | Localhost, RedisClustering, AzureTableClustering, AdoNetClustering, CustomClustering |
| `ClientClusteringMode` | Localhost, StaticGateway, Custom |
| `StorageProvider` | Memory, RedisStorage, AzureBlobStorage, AzureTableStorage, AdoNetStorage, CosmosStorage, DynamoDbStorage, CustomStorage |
| `StreamProvider` | MemoryStream, PersistentStream, CustomStream |
| `ReminderProvider` | MemoryReminder, RedisReminder, CustomReminder |
| `TlsConfig` | TlsSubject, TlsCertificate, MutualTlsSubject, MutualTlsCertificate |
| `DashboardConfig` | DashboardDefaults, DashboardWithOptions |
| `TransactionalGrainDefinition<'State>` | Record of pure functions describing transactional grain behaviour (`Deposit`, `Withdraw`, `GetBalance`, `CopyState`) |
| `AtmGrainDefinition<'TAccountGrain>` | Record containing a `Transfer` function for orchestrating cross-grain atomic transfers |
| `FSharpTransactionalGrain<'State>` | Generic base class that bridges a `TransactionalGrainDefinition` to Orleans ACID transactions |
| `FSharpAtmGrain<'TAccountGrain>` | Generic base class for ATM grains that create and coordinate transactions across multiple account grains |

### Transactional grain extension methods

| Method | Signature | Description |
|---|---|---|
| `AddFSharpTransactionalGrain<'State>` | `IServiceCollection -> TransactionalGrainDefinition<'State> -> IServiceCollection` | Register a transactional grain definition as a singleton |
| `AddFSharpAtmGrain<'TAccountGrain>` | `IServiceCollection -> AtmGrainDefinition<'TAccountGrain> -> IServiceCollection` | Register an ATM grain definition as a singleton |

### Computation expressions

| CE | Builder | Description |
|---|---|---|
| `siloConfig { }` | `SiloConfigBuilder` | Configure an Orleans silo |
| `clientConfig { }` | `ClientConfigBuilder` | Configure an Orleans client |

See [Silo configuration](silo-configuration.md) and [Client configuration](client-configuration.md)
for the full keyword lists.

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

## Orleans.FSharp.EventSourcing

The `eventSourcedGrain { }` CE and its `JournaledGrain` bridge. Not deprecated, and not the same
thing as the `journaledGrainFor` definition builder above: this one needs a C#-declared grain
interface and the CodeGen that comes with it. See [Event Sourcing](event-sourcing.md).

### Types

| Type | Description |
|---|---|
| `EventSourcedGrainDefinition<'State, 'Event, 'Command>` | Event-sourced grain specification (`DefaultState`, `Apply`, `Handle`, `ConsistencyProvider`, `CustomStorage`, `SnapshotStrategy`) |
| `SnapshotStrategy<'State>` | `Never`, `Every of int`, `Condition of (int -> 'State -> bool)` |
| `CustomStorageAdapter` | Boxed read/write pair for a custom log-consistency store |
| `IEventStoreContext<'Event>` | Event store abstraction for the C# CodeGen bridge (`RaiseEvent`, `ConfirmEvents`, `Version`) |
| `FSharpEventSourcedGrain<'State,'Event,'Command>` | Generic `JournaledGrain` base bridging a definition to Orleans |
| `FSharpEventSourcedGrainAttribute` | Binds an implementation to a grain interface |

### Computation expressions

| CE | Builder | Description |
|---|---|---|
| `eventSourcedGrain { }` | `EventSourcedGrainBuilder` | Define event-sourced grain behavior |

#### `eventSourcedGrain { }` — keywords

| Keyword | Signature | Description |
|---|---|---|
| `defaultState` | `'State` | Initial state value |
| `apply` | `'State -> 'Event -> 'State` | The pure fold |
| `handle` | `'State -> 'Command -> 'Event list` | Command handler; an empty list is a refusal |
| `logConsistencyProvider` | `string` | Named Orleans log-consistency provider |
| `snapshot` | `SnapshotStrategy<'State>` | Snapshot strategy (honoured only by a custom store) |
| `customStorage` | `read` + `write` | Custom log-consistency storage pair |

#### `EventSourcedGrainDefinition`

| Function | Signature | Description |
|---|---|---|
| `foldEvents` | `definition -> 'State -> 'Event list -> 'State` | Replay events onto state |
| `handleCommand` | `definition -> 'State -> 'Command -> 'State * 'Event list` | Process a command, returning the folded state and the events |

#### `EventStore`

| Function | Signature | Description |
|---|---|---|
| `processCommand` | `definition -> 'State -> 'Command -> 'Event list` | Produce events from a command |
| `applyEvent` | `definition -> 'State -> 'Event -> 'State` | Apply a single event |
| `replayEvents` | `definition -> 'State -> 'Event list -> 'State` | Replay an event list |
| `shouldSnapshot` | `definition -> int -> 'State -> bool` | Evaluate the snapshot strategy |

#### Registration

| Method | Signature | Description |
|---|---|---|
| `AddFSharpEventSourcedGrain<'State,'Event,'Command>` | `IServiceCollection -> definition -> IServiceCollection` | Register one definition |
| `AddFSharpEventSourcedGrainsFromAssembly` | `IServiceCollection -> Assembly -> IServiceCollection` | Register every definition an assembly declares |

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
[Testing](testing.md).

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
let handler state cmd =
    async { return state, box 0 }  // ⚠️ OF0001

// Suppress when async is genuinely needed
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer

[<AllowAsync>]
let legacyAdapter (url: string) : Async<string> =
    async { return! fetchLegacy url }  // ✅ suppressed
```

See [Analyzers guide](analyzers.md) for full documentation.

### Internal API (test use only)

| Module | Function | Signature | Description |
|---|---|---|---|
| `AstWalker` | `collectAsyncRanges` | `ParsedInput -> range list` | Walk AST and return all unsuppressed `async { }` ranges |

---

## Deprecated: the `grain { }` cluster

Everything below carries `[<Obsolete>]` (warning, not error) and is kept runnable. The replacement
for each entry is the [functional grain runtime](#functional-grain-runtime) above; see
[Functional grains](functional-grains.md), "Migrating from the `grain { }` CE", for the rewrite
recipe.

### Types

| Type | Description | Replacement |
|---|---|---|
| `GrainDefinition<'State, 'Message>` | Immutable record describing a grain's behavior | `FunctionalGrainDefinition<...>` from `grainFor` |
| `GrainContext` | Grain factory, service provider, and named states | `FunctionalGrainContext<'Actor,'Key>` |
| `AdditionalStateSpec` | Named additional persistent state specification | `PersistentState.create` + `usePersistentState` |
| `FSharpGrainAttribute` | Marks a definition for assembly discovery | — (a definition is registered by value) |
| `FSharpGrainHandle<'S,'M>` | Zero-alloc struct handle for a string-keyed grain | `FunctionalGrain.ref` / `rawRef` |
| `FSharpGrainGuidHandle<'S,'M>` | Zero-alloc struct handle for a GUID-keyed grain | `FunctionalGrain.ref` / `rawRef` |
| `FSharpGrainIntHandle<'S,'M>` | Zero-alloc struct handle for an int64-keyed grain | `FunctionalGrain.ref` / `rawRef` |

### Computation expressions

| CE | Builder | Description |
|---|---|---|
| `grain { }` | `GrainBuilder` | Define grain behavior declaratively |

#### `grain { }` — key CE keywords

| Keyword | Handler Signature | Description |
|---|---|---|
| `defaultState` | `'State` | Initial state value |
| `handle` | `'State -> 'Msg -> Task<'State * obj>` | Register handler with manual `box` |
| `handleState` | `'State -> 'Msg -> Task<'State>` | Handler returning only state (no result value) |
| `handleTyped` | `'State -> 'Msg -> Task<'State * 'R>` | Handler with typed result — no `box` needed |
| `handleWithContext` | `GrainContext -> 'State -> 'Msg -> Task<'State * obj>` | Handler with DI/grain-to-grain access |
| `handleStateWithContext` | `GrainContext -> 'State -> 'Msg -> Task<'State>` | Context + state-only return |
| `handleTypedWithContext` | `GrainContext -> 'State -> 'Msg -> Task<'State * 'R>` | Context + typed result |
| `handleCancellable` | `'State -> 'Msg -> CancellationToken -> Task<'State * obj>` | Cancellation, manual `box` |
| `handleStateCancellable` | `'State -> 'Msg -> CancellationToken -> Task<'State>` | Cancellation, state-only return |
| `handleTypedCancellable` | `'State -> 'Msg -> CancellationToken -> Task<'State * 'R>` | Cancellation, typed result |
| `handleWithContextCancellable` | `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State * obj>` | Context + cancellation |
| `handleStateWithContextCancellable` | `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State>` | Context + cancellation, state-only return |
| `handleTypedWithContextCancellable` | `GrainContext -> 'State -> 'Msg -> CancellationToken -> Task<'State * 'R>` | Context + cancellation, typed result |
| `persist` | `string` | Name of the storage provider for state |
| `additionalState<'T>` | `string` (name) + `string` (storage) + `'T` (default) | Named additional persistent state |
| `onActivate` | `'State -> Task<'State>` | Activation hook; may replace the state |
| `onDeactivate` | `'State -> Task<unit>` | Deactivation hook; cleanup only |
| `onReminder` | `string` + `('State -> string -> TickStatus -> Task<'State>)` | Named reminder with a stateful handler |
| `onTimer` | `string` + `TimeSpan` (due) + `TimeSpan` (period) + `('State -> Task<'State>)` | Declarative timer |
| `onLifecycleStage` | `int` + `(CancellationToken -> Task<unit>)` | Hook a raw Orleans grain-lifecycle stage number |
| `interleaveMessage` | `System.Type` | Allow a message type to interleave (`interleaveMessage typeof<Query>`) |

Each `handle*` keyword also has a `*WithServices` form (`handleWithServices`,
`handleStateWithServices`, `handleTypedWithServices`, and their `Cancellable` variants) taking an
`IServiceProvider` instead of a `GrainContext`. See
[Grain Definition guide](grain-definition.md) for the full keyword list. Per-grain Orleans
attributes (`[Reentrant]`, `[StatelessWorker]`, placement, `[OneWay]`, `[ReadOnly]`,
`[ImplicitStreamSubscription]`, …) are applied via the C# CodeGen path, not `grain { }` keywords.
On the [functional grain runtime](functional-grains.md) they are ordinary contract and definition
operations instead — `reentrant`, `statelessWorker`, `placement`, `oneWay`, `readOnly`, and
`onStream` / `onBroadcast` for implicit subscriptions.

### Modules

#### `GrainContext`

| Function | Signature | Description |
|---|---|---|
| `getService<'T>` | `GrainContext -> 'T` | Resolve a DI service |
| `getState<'T>` | `GrainContext -> string -> IPersistentState<'T>` | Get named additional persistent state |
| `getGrainByString<'T>` | `GrainContext -> string -> GrainRef<'T, string>` | Get grain ref by string key |
| `getGrainByGuid<'T>` | `GrainContext -> Guid -> GrainRef<'T, Guid>` | Get grain ref by GUID key |
| `getGrainByInt64<'T>` | `GrainContext -> int64 -> GrainRef<'T, int64>` | Get grain ref by int64 key |
| `getGrainByGuidCompound<'T>` | `GrainContext -> Guid -> string -> GrainRef<'T, CompoundGuidKey>` | Compound GUID key |
| `getGrainByIntCompound<'T>` | `GrainContext -> int64 -> string -> GrainRef<'T, CompoundIntKey>` | Compound int64 key |
| `deactivateOnIdle` | `GrainContext -> unit` | Request grain deactivation when idle |
| `delayDeactivation` | `GrainContext -> TimeSpan -> unit` | Delay grain deactivation |
| `grainId` | `GrainContext -> GrainId` | Get the GrainId |
| `primaryKeyString` | `GrainContext -> string` | Get string primary key |
| `primaryKeyGuid` | `GrainContext -> Guid` | Get Guid primary key |
| `primaryKeyInt64` | `GrainContext -> int64` | Get int64 primary key |
| `empty` | `GrainContext` | Empty context for unit tests (all fields null/None) |

#### `GrainDefinition`

| Function | Signature | Description |
|---|---|---|
| `hasAnyHandler` | `GrainDefinition -> bool` | True if any handler is registered |
| `getHandler` | `GrainDefinition -> 'State -> 'Message -> Task<'State * obj>` | Get plain handler |
| `getContextHandler` | `GrainDefinition -> GrainContext -> 'State -> 'Message -> Task<'State * obj>` | Get context-aware handler |
| `getCancellableContextHandler` | `GrainDefinition -> GrainContext -> 'State -> 'Message -> CT -> Task<'State * obj>` | Get cancellable context handler |
| `invokeHandler` | `GrainDefinition -> 'State -> 'Message -> Task<'State * obj>` | Invoke handler (C# interop) |
| `invokeContextHandler` | `GrainDefinition -> GrainContext -> 'State -> 'Message -> Task<'State * obj>` | Invoke context handler (C# interop) |
| `invokeCancellableContextHandler` | `GrainDefinition -> GrainContext -> 'State -> 'Message -> CT -> Task<'State * obj>` | Invoke cancellable (C# interop) |
| `invokeOnActivate` | `GrainDefinition -> 'State -> Task<'State>` | Run the activation hook directly |
| `invokeOnDeactivate` | `GrainDefinition -> 'State -> Task` | Run the deactivation hook directly |
| `invokeReminderHandler` | `GrainDefinition -> 'State -> string -> TickStatus -> Task<'State>` | Run one named reminder handler directly |

#### `Reminder`

| Function | Signature | Description |
|---|---|---|
| `register` | `Grain -> string -> TimeSpan -> TimeSpan -> Task<IGrainReminder>` | Register/update reminder |
| `unregister` | `Grain -> string -> Task<unit>` | Unregister reminder |
| `get` | `Grain -> string -> Task<IGrainReminder option>` | Get reminder by name |

Replacement: `onReminder` on a functional definition, which reconciles declared reminders on every
activation.

#### `Timers`

| Function | Signature | Description |
|---|---|---|
| `register` | `Grain -> (CT -> Task<unit>) -> TimeSpan -> TimeSpan -> IGrainTimer` | Register timer |
| `registerWithState<'T>` | `Grain -> ('T -> CT -> Task<unit>) -> 'T -> TimeSpan -> TimeSpan -> IGrainTimer` | Timer with state |

Replacement: `onTimer` on a functional definition.

#### `FSharpGrain` — universal grain pattern

Registered once with `AddFSharpGrain`, called from anywhere with `FSharpGrain.ref`. Replacement:
`grainContract` + `grainFor` + `FunctionalGrain.ref`, which types the reply per operation instead of
boxing one message DU.

| Function | Signature | Description |
|---|---|---|
| `FSharpGrain.ref<'S,'M>` | `IGrainFactory -> string -> FSharpGrainHandle<'S,'M>` | Handle for string-keyed grain |
| `FSharpGrain.refGuid<'S,'M>` | `IGrainFactory -> Guid -> FSharpGrainGuidHandle<'S,'M>` | Handle for GUID-keyed grain |
| `FSharpGrain.refInt<'S,'M>` | `IGrainFactory -> int64 -> FSharpGrainIntHandle<'S,'M>` | Handle for int64-keyed grain |
| `FSharpGrain.send<'S,'M>` | `'M -> FSharpGrainHandle<'S,'M> -> Task<'S>` | Send command, return typed state |
| `FSharpGrain.post<'S,'M>` | `'M -> FSharpGrainHandle<'S,'M> -> Task` | Fire-and-forget command |
| `FSharpGrain.ask<'S,'M,'R>` | `'M -> FSharpGrainHandle<'S,'M> -> Task<'R>` | Send command, return typed result (can differ from state) |
| `FSharpGrain.sendGuid<'S,'M>` | `'M -> FSharpGrainGuidHandle<'S,'M> -> Task<'S>` | Send to GUID-keyed grain |
| `FSharpGrain.postGuid<'S,'M>` | `'M -> FSharpGrainGuidHandle<'S,'M> -> Task` | Post to GUID-keyed grain |
| `FSharpGrain.askGuid<'S,'M,'R>` | `'M -> FSharpGrainGuidHandle<'S,'M> -> Task<'R>` | Ask GUID-keyed grain for typed result |
| `FSharpGrain.sendInt<'S,'M>` | `'M -> FSharpGrainIntHandle<'S,'M> -> Task<'S>` | Send to int64-keyed grain |
| `FSharpGrain.postInt<'S,'M>` | `'M -> FSharpGrainIntHandle<'S,'M> -> Task` | Post to int64-keyed grain |
| `FSharpGrain.askInt<'S,'M,'R>` | `'M -> FSharpGrainIntHandle<'S,'M> -> Task<'R>` | Ask int64-keyed grain for typed result |

DI registration (call once per grain definition at silo startup):

```fsharp
// Automatically registers FSharpBinaryCodec (idempotent)
services.AddFSharpGrain<CounterState, CounterCommand>(counterGrain) |> ignore
```

`AddFSharpGrainsFromAssembly` registers every `[<FSharpGrain>]`-marked definition an assembly
declares.

#### Testing helpers for the deprecated model

| Function | Signature | Description |
|---|---|---|
| `GrainMock.withFSharpGrain<'S,'M>` | `string -> GrainDefinition<'S,'M> -> MockGrainFactory -> MockGrainFactory` | Register an F# grain definition as a mock, by string key |
| `GrainMock.withFSharpGrainGuid<'S,'M>` | `Guid -> GrainDefinition<'S,'M> -> MockGrainFactory -> MockGrainFactory` | The same, by GUID key |
| `GrainMock.withFSharpGrainInt<'S,'M>` | `int64 -> GrainDefinition<'S,'M> -> MockGrainFactory -> MockGrainFactory` | The same, by int64 key |
| `TestHarness.getFSharpGrain<'S,'M>` | `TestHarness -> string -> FSharpGrainHandle<'S,'M>` | Handle from a test cluster, by string key |
| `TestHarness.getFSharpGrainGuid<'S,'M>` | `TestHarness -> Guid -> FSharpGrainGuidHandle<'S,'M>` | The same, by GUID key |
| `TestHarness.getFSharpGrainInt<'S,'M>` | `TestHarness -> int64 -> FSharpGrainIntHandle<'S,'M>` | The same, by int64 key |
