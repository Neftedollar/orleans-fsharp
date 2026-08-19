---
title: "Functional Grain Runtime"
description: "A second, complete authoring model: user-authored API records instead of C# CodeGen interfaces."
---

# Functional Grain Runtime

**A second, complete authoring model: user-authored API records instead of C# CodeGen interfaces.**

## What you'll learn

- Why the actor brand type exists, why it is never constructed, and the short form that reuses the API record as the brand
- Key-codec identity rules: what changes a grain's routing/storage identity, what does not
- Operation rename via `operationId`, contract-version matching, and opting into version tolerance
- Reentrancy variants: whole-grain `reentrant` and the per-request `mayInterleave` predicate
- The persistence model: explicit writes only, unique state names, and multi-provider non-atomicity
- Distributed ACID transactions: `transactionalStateFrom`, per-operation `transactional`, and the exact re-execution semantics
- Event sourcing: `journaledGrainFor`, a second definition kind whose state is the fold of an event journal
- Lifecycle hooks, timers, reminders, and collection age
- Delivery semantics: acknowledged vs. one-way, what a successful call does *not* imply, and cancellation without rollback
- Immutable-state guidance -- deep mutation is unguarded by design
- Reminder rename/removal migration -- the explicit unregister step
- Why the point-free `let ref = FunctionalGrain.ref contract` binding infers its complete type

## Overview

The functional grain runtime is a second, independent way to author and call grains, alongside
the `grain { }` CE / CodeGen path described in [Grain Definition](/orleans-fsharp/grain-definition/). Instead of
a C# interface generated at build time, you write a plain F# record of functions -- the **API
record** -- and a **contract** that gives it a stable wire identity:

```fsharp
namespace Chat.Contracts

open System
open System.Threading.Tasks
open Orleans.FSharp

type RoomActor = private RoomActor of unit

[<NoEquality; NoComparison>]
type RoomApi =
    { join: UserId -> Task<unit>
      say: PostMessage -> Task<Result<int64, PostError>>
      history: HistoryRequest -> Task<ChatMessage list>
      typing: Typing -> Task<unit> }

[<RequireQualifiedAccess>]
module RoomApi =
    let contract =
        grainContract<RoomActor, RoomId, RoomApi> () {
            grainType "chat.room"
            version 1
            stringKeyMapped RoomId.value RoomId.create

            readOnly (_.history)
            oneWay (_.typing)
            alwaysInterleave (_.typing)
        }

    let ref = FunctionalGrain.ref contract
    let rawRef = FunctionalGrain.rawRef contract
```

Server-side, a `grainFor` definition attaches handlers and persistent state to the contract, and
`AddFunctionalGrain` / `AddFunctionalGrainClient` register it on the silo / client builders. See
`src/Orleans.FSharp.Sample/ChatRoomFunctional.fs` for the complete, runnable version of this
example (contract, definition, registration, and a driven call sequence).

## One operation, one argument

An API field takes exactly one argument. A multi-input operation groups its inputs in a tuple:

```fsharp
{ typing: (UserId * bool) -> Task<unit> }
```

That tuple is the operation's wire argument type, and the handler destructures it:

```fsharp
// caller
do! room.typing (userId, true)

// silo
handle (_.typing) (fun context state (user, isTyping) ->
    task {
        context.logger.LogDebug("{User} typing={IsTyping}", user, isTyping)
        return state, ()
    })
```

A field spelled curried (`UserId -> bool -> Task<unit>`) is **not** an operation and fails
contract construction: every API field must have the shape `'Argument -> Task<'Reply>` or
`'Argument -> IAsyncEnumerable<'Item>`. Neither is a field that returns a function, or one whose
range is `Async<'Reply>`, `ValueTask<'Reply>`, a non-generic `Task`, or a synchronous `seq<_>`.
`unit` means "no domain input" (`unit -> Task<'Reply>`).

`FunctionalGrainRef.call` and `callCancellable` take the same one-argument shape.

### The second field kind: a streaming reply

`'Argument -> IAsyncEnumerable<'Item>` makes the operation **server-streaming** — the handler
produces items over time and the caller receives each one as it is produced. It is bound with
`handleStream` instead of `handle`, and the handler returns items only, with no replacement state:

```fsharp
{ tail: int -> IAsyncEnumerable<Entry> }

handleStream (_.tail) (fun context state (count: int) ->
    taskSeq {
        for entry in state |> List.truncate count do
            yield entry
    })
```

It rides Orleans' own async-enumerable grain extension, so an open enumeration never blocks an
ordinary call to the same activation, disposing the enumerator cancels the producer, and an
abandoned enumerator is collected by Orleans. A streaming handler is state-neutral, and none of the
four admission policies compose with it. The full rules are in
[Server-Streaming Replies](/orleans-fsharp/streaming-replies/).

## Why the actor brand

```fsharp
type RoomActor = private RoomActor of unit
```

`RoomActor` is a phantom type: `private` blocks construction (and pattern-matching) from outside the
module, and its one case takes `unit` -- so nothing, anywhere, ever constructs a value of it. Only
its CLR type identity is used; a type that is never instantiated costs nothing at runtime.

That identity is what lets Orleans keep two functional grains distinct with no code generation at
all. The runtime closes `FunctionalGrainMarker<'Actor>` and `IFunctionalGrainTarget<'Actor>` over
each definition's actor brand, so `IFunctionalGrainTarget<RoomActor>` and
`IFunctionalGrainTarget<CounterActor>` are two different closed CLR types that Orleans' manifest,
activator, and call-filter machinery can address independently -- the same distinction a
hand-written interface plus C# codegen would otherwise provide. Every contract-adjacent type carries
the same brand as a type parameter -- `GrainContract<'Actor,'Key,'Api>`, the definition,
`FunctionalGrainRef<'Actor,'Key,'Api>`, every handler and hook -- so a handler written for one grain
cannot be passed where another grain's is expected, even when their key and API record types happen
to coincide. The silo registry enforces the same rule at registration: each actor-brand CLR type
maps to exactly one contract, and a second definition that reuses an existing brand is a
configuration error.

What the brand is **not**: it plays no part in wire or storage identity. `GrainId` is built from the
explicit `grainType` string and the encoded key alone (see
[Key-codec identity rules](#key-codec-identity-rules) below) -- renaming the brand type, like
renaming the module or the API record type, changes nothing about routing or stored state.

### The short form: the API record as its own brand

Nothing requires the brand to be a dedicated type. The contract's `'Actor` parameter is
unconstrained, and any CLR type identity works -- including the API record's own:

```fsharp
let counterContract =
    contract<string, CounterApi> () {
        grainType "counter"
        version 1
        stringKey
    }
```

`contract<'Key, 'Api>` is the dedicated entry point for this form -- it is exactly
`grainContract<'Api, 'Key, 'Api>`, so the two spellings seal the same contract type and everything
said below applies to either.

No phantom type is declared at all, and everything downstream is unchanged: the runtime closes the
marker over the record type (`FunctionalGrainMarker<CounterApi>`), so the grain class Orleans'
manifest and Dashboard display carries the record's name -- if anything, more readable than a
phantom's. This is the right default for the common case, where one API record belongs to exactly
one grain type.

A **separate** brand type is load-bearing in exactly two situations:

- **Several grain types over one API record.** Two contracts can share an API shape and differ
  only in policy -- the feature tour's reentrancy section hosts `tour.reentrant` and `tour.serial`
  over one `GateApi` with two brands. With the record as the brand, both would close the same
  marker CLR type, and the silo registry rejects a second definition per brand -- so the record
  would have to be duplicated just to tell them apart.
- **Replacing the record type while keeping the grain's identity.** Under
  [version tolerance](#version-tolerance-acceptsversions-and-sinceversion), a newer client may declare a *new* record type with a
  wire-compatible shape. The brand participates in the closed transport-interface identity, so a
  stable brand lets the record type change underneath it; with the record as the brand, changing
  the record moves that identity too.

One interaction to know: with a derived `grainType` (see
[Optional grainType](#optional-graintype-when-the-derived-default-is-safe) below), the default
grain type becomes the *record's* simple name, and renaming the record then changes wire and
storage identity -- the same trade-off as renaming a phantom brand under a derived name, with the
same fix: declare `grainType` explicitly.

## Key-codec identity rules

A grain's Orleans identity is:

```text
GrainId(GrainType(contract.grainType), keyCodec.encode(domainKey))
```

**Changes identity** (routing *and* storage -- a different `GrainId` means a different
activation, and for persistent state, a different durable record):

- the `grainType` string;
- the key codec's encoding -- switching between `stringKey`/`guidKey`/`int64Key`/compound
  variants, or changing a *mapped* codec's encode/decode pair so it produces different native
  values for the same domain key.

**Does not change identity** (when `grainType` is explicit -- see
[Optional grainType](#optional-graintype-when-the-derived-default-is-safe) below for the derived
case, where renaming the brand *does* change identity):

- renaming the F# module, the API record type, or the actor-brand type (`RoomActor` above) --
  identity is carried by the explicit `grainType` string and the encoded key, never by CLR names;
- the contract `version` or any operation ID -- both are **application protocol metadata carried
  in every request**, not storage-key components.

Every mapped key codec (`stringKeyMapped`, `guidKeyMapped`, `int64KeyMapped`, and their compound
forms) must satisfy, for its whole domain:

- deterministic, injective encoding into the selected Orleans key space;
- `decode (encode key) = key` under domain equality (round-trip);
- `encode (decode native) = native` in canonical Orleans representation (canonicalization);
- rejection of malformed or non-canonical native values.

Native keys (`stringKey`, `guidKey`, `int64Key`, and their compound forms) need no conversion
functions at all -- the domain key type *is* the Orleans key type.

### Optional grainType: when the derived default is safe

`grainType` is optional. Omit it and the contract's grain type defaults to the actor brand's CLR
*simple* name (`typeof<'Actor>.Name`, ordinal, as written -- `RoomActor` becomes `"RoomActor"`).
The brand must be a simple, non-generic, non-nested CLR type for that to work:

- a **generic** brand is rejected -- its `.Name` carries a backtick arity suffix (for example
  ``"CounterActor`1"``), which is not a simple name;
- a **nested** brand is rejected too -- and this catches more than you might expect. Every type an
  F# `module` declares is a CLR-nested type (a '+' in its qualified name), even with no explicit
  nested `module` block, unlike a `namespace`. The `RoomActor` example above is declared under
  `namespace Chat.Contracts`, which is exactly why it qualifies; the same declaration under
  `module Chat.Contracts` would not.

Either case fails contract construction with a diagnostic demanding an explicit `grainType`.

The trade-off is [Why the actor brand](#why-the-actor-brand)'s "renaming costs nothing" guarantee,
inverted. That guarantee holds because the explicit `grainType` string, not the brand's CLR name,
carries identity. With a **derived** grain type there is no such string -- the brand's simple name
*is* the grain type -- so renaming the brand silently renames `grainType` too, moving routing and,
worse, storage identity out from under any persisted state or durable reminder registered under
the old name.

That is why a definition which attaches `stateFrom`, `usePersistentState`, or
`transactionalStateFrom`, or declares `onReminder`, requires its contract to carry an **explicit**
`grainType` -- enforced both when the definition seals and again, redundantly, at silo
registration. A journaled definition requires one unconditionally, because the grain type name is
part of the storage key of its journal. Ephemeral definitions (none of the four attachments) have
nothing durable to orphan, so they may rely on the derived default:

```fsharp
// Fine: ephemeral, so an accidental brand rename can never orphan anything durable.
type CounterActor = private CounterActor of unit

let counterContract =
    grainContract<CounterActor, string, CounterApi> () { stringKey }  // grainType = "CounterActor"

// Rejected at definition sealing: stateFrom needs an explicit grainType above, because renaming
// CounterActor later would silently point every existing activation at a different (empty)
// durable record under the new derived name.
let counterDefinition =
    grainFor counterContract {
        defaultState (fun () -> { count = 0 })
        stateFrom counterState
        handle (_.increment) incrementHandler
    }
```

One more hazard, independent of durability: the derived name has no namespace qualification, only
the CLR simple name. Two actor brands with the same simple name in different namespaces (two
`CounterActor` types) derive the identical grain type and collide -- caught by the same
grain-type-uniqueness check that already rejects two *explicit* contracts sharing a `grainType`,
but worth knowing before leaning on the derived default across a codebase with repeated type
names.

## Operation rename and contract version

**Operation IDs** default to the API record's field name, ordinal and case-sensitive. Renaming a
handler's *source field* would otherwise silently change every caller's wire ID along with it;
`operationId` decouples the two, so a refactor of the F# field name never touches the wire:

```fsharp
grainContract<RoomActor, RoomId, RoomApi> () {
    grainType "chat.room"
    // the record field is `enter`, but the wire ID stays "join" across the rename
    operationId "join" (_.enter)
}
```

Final operation IDs must be unique, non-blank, NUL-free ordinal strings within a contract.

**Contract version matching is exact by default.** Every request carries the caller's contract
version; the target compares it against its own hosted version with `=`, not `>=`. A version
mismatch fails the call before any handler runs, with no negotiation and no automatic fallback:

```text
Orleans.FSharp functional transport: grain type 'chat.room' hosts contract version 2
but received version 1.
```

Contract version is independent of `GrainId`, storage identity, and the fixed Orleans interface
version (which this transport family pins to `1` internally, regardless of your contract's
`version`).

### Version tolerance: `acceptsVersions` and `sinceVersion`

A host can opt out of the exact rule and admit older callers as well, which is what makes a
mixed-version rolling deployment of one contract possible:

```fsharp
grainContract<LedgerActor, string, LedgerApi> () {
    grainType "billing.ledger"
    version 3
    stringKey

    // Admit 2 and 3. The default is Exact, which admits 3 only.
    acceptsVersions (BackwardCompatible 2)

    // 'refund' did not exist at version 2, so a call admitted at 2 is refused for it by name.
    sinceVersion 3 (_.refund)
}
```

`VersionPolicy` is a **closed set**, not a predicate, and that is not stylistic: a protocol token
is the digest of grain type, **version**, operation ID, and direction, so a caller at an older
admitted version sends a different request token and checks the reply against a different reply
token. A version-tolerant host has to answer in the caller's own version, which means precomputing
every admitted version's token pair when the definition is sealed — possible for a bounded range,
impossible for `fun v -> v >= 3`.

**Accepting a version asserts wire compatibility. There is no magic.** The argument payload is
deserialized as the hosted definition's exact declared CLR type whatever version admitted it, and
the reply is serialized the same way. Nothing converts between shapes and nothing inspects an
older one. Declaring `BackwardCompatible n` is the application stating that every version from
`n` upwards still sends and reads the same argument and reply types for every operation it can
invoke. An operation whose *shape* changed needs a **new operation** — a new `operationId` — not a
wider policy; `sinceVersion` then keeps the old callers off it.

One consequence is easy to miss: the **admission-flag byte** (`readOnly` / `oneWay` /
`alwaysInterleave`) travels in the envelope and is compared against the hosted descriptor, so an
operation whose flags changed between two versions inside the accepted range still fails — with
the transport's admission-flags diagnostic, not a version one. Wire compatibility means the flags
too, not only the argument and reply types.

What the policy changes is **admission, and nothing else**. The wire format, the stable operation
IDs, the admission flags, the grain identity, and the storage identity are all untouched: a v2
call and a v3 call to the same key reach the same activation, the same state, and the same
handler. Nothing is published to the grain manifest for it either — it is a host-side rule, so a
silo that has gossiped the grain type sees exactly what it saw before.

Two rejection diagnostics come out of this, at the same stage and in the same taxonomy as the
exact-mode one above:

```text
Orleans.FSharp functional transport: grain type 'billing.ledger' hosts contract version 3
and accepts versions 2 through 3, but received version 1.

Orleans.FSharp functional transport: operation 'refund' on grain type 'billing.ledger'
was introduced at contract version 3, but the request declares version 2.
```

Sealing rejects a policy that cannot do anything:

| Declaration | Rejected when |
|---|---|
| `acceptsVersions (BackwardCompatible n)` | `n <= 0`, or `n` is above the contract version (the contract would admit nothing at all) |
| `sinceVersion n (_.op)` | `n <= 0`; `n` is above the contract version; or `n` is at or below the lowest admitted version, so it could never reject a call |

That last rule is what catches the common mistake: `sinceVersion` **without** `acceptsVersions` is
always dead, because the default policy admits the hosted version only.

If you would rather not widen the policy at all, the original exact-version pattern still works: host **two
contracts** (one per version, e.g. two different `grainType` strings), migrate traffic explicitly,
then retire the old one.

## Persistence model

Persistent state is opt-in and explicit at every step -- nothing about the functional runtime writes
storage on your behalf.

`PersistentState.create<'State> stateName providerName` builds an immutable logical descriptor.
`providerName` names an `IGrainStorage` registration already configured on the silo (a provider name,
not a connection string); creation rejects a blank or NUL-containing `stateName` immediately.

```fsharp
let roomState = PersistentState.create<RoomState> "state" "Default"
```

`stateFrom roomState` in `grainFor { }` attaches that descriptor as the definition's **primary**
holder -- the loaded `IPersistentState<RoomState>.State` supplies the handler's `state` argument, and
it is also reachable through `context.persistentState roomState`. `usePersistentState descriptor
initializer` attaches an *additional*, independently typed holder and is repeatable; its
`'Key -> 'StoredState` initializer runs only when that holder has no stored record yet. Neither
operation writes anything by itself.

Every write is a handler calling `WriteStateAsync()` (or `ReadStateAsync()` / `ClearStateAsync()`)
on the facet `context.persistentState descriptor` returns -- exactly as in the chat-room `join` and
`say` handlers above. Returning a new `state` from a handler publishes it **in memory** for the rest
of the activation; it does **not** imply a storage write -- the same rule the
[Delivery semantics](#delivery-semantics) section below states from the caller's side: a successful
call means the handler ran, not that anything was persisted.

### After `ClearStateAsync`, re-initialize the state yourself

This one bites, and it is stock Orleans behaviour rather than anything the functional runtime adds.
`ClearStateAsync()` deletes the stored record **and** re-seeds the holder's in-memory `State` with a
*fresh uninitialized instance* of the stored type — the same
`RuntimeHelpers.GetUninitializedObject` call described in
[What a stored type may be](#what-a-stored-type-may-be) below. For an F# record that means every
reference field comes back **`null`**, and `null` is not a value of an F# `list`, `map`, `set`,
record or union:

```fsharp
let storage = context.persistentState roomState
do! storage.ClearStateAsync()

// storage.State is now a RoomState whose Messages and Members are BOTH null.
// The next WriteStateAsync() on it fails inside the codec.
```

**Assign a freshly initialized state after every clear.** The initializer you gave
`usePersistentState` is not re-run, and neither is `defaultState`:

```fsharp
let storage = context.persistentState roomState
do! storage.ClearStateAsync()

let reseeded = { Members = Set.empty; Messages = [] }   // whatever "empty" means for you
storage.State <- reseeded
return reseeded, ()
```

The runtime deliberately does **not** re-initialize for you: hidden state writes and replacements
are not part of this model, and only the application knows what an empty room is. What it does do
is fail *legibly*. Writing a record with a null in a field whose type has no null names the field, the
record, and this cause:

```text
FSharpBinaryCodec: field 'Messages' of the record 'ChatRoom.Grains.RoomState' is null, but its
declared type '…FSharpList`1[…]' has no null value. The usual cause is a persistent state that was
cleared and not re-initialized: after ClearStateAsync the holder's State is a fresh uninitialized
instance, so assign a freshly initialized state before the next write.
```

Nulls that F# uses *on purpose* are unaffected: `None` is compiled to `null`, a `string` field may
be null, and an ordinary class field may be null. All three still round-trip.

### What a stored type may be

Orleans creates the in-memory instance of a persistent state through its serializer activator,
which calls `System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject`. That method
cannot produce an instance of certain shapes at all, so definition sealing rejects them up front
instead of letting the first activation fail:

| Rejected stored type | Why |
|---|---|
| An F# union with **two or more cases where at least one carries data** | It compiles to an *abstract base class*, and `GetUninitializedObject` cannot instantiate an abstract class. Wrap it in a record. |
| `string` | `GetUninitializedObject` rejects `System.String` outright ("Uninitialized Strings cannot be created"). Wrap it in a record or another class. |
| An array type | `GetUninitializedObject` cannot create array instances. |
| A delegate type | Same — no delegate instances. |
| An interface | No instance of an interface exists to create; name the concrete stored type. |
| An abstract class | The same reason as the multi-case union above. |
| `Nullable<'T>` | Orleans resolves a value-type state's activator through `DefaultValueTypeActivator`, whose type parameter excludes `System.Nullable`. |

Nothing else is rejected, and that is deliberate: the list is exactly the set proven to fail on a
stock storage provider. Records, classes without a public constructor, structs, enums, F# lists,
maps, options, and **single-case or all-nullary** unions are all valid stored types.

The union rule is the one that surprises people, because such a union is perfectly serializable —
it is the *activation* step, not serialization, that cannot handle it. The fix is one record:

```fsharp
type OrderStatus =                  // 2+ cases, one carries data -> abstract base class
    | Pending
    | Shipped of trackingId: string

// rejected at definition sealing:
//     PersistentState.create<OrderStatus> "order" "Default"

type OrderRecord = { status: OrderStatus }

// accepted:
let orderState = PersistentState.create<OrderRecord> "order" "Default"
```

### Unique state names, and why

Every attached descriptor -- whether `stateFrom` or one of the repeated `usePersistentState`
attachments -- needs a **unique `stateName` within the definition**, even when two descriptors use
different providers. Definition sealing rejects a repeat. This is not an arbitrary restriction:
Orleans derives a grain's per-facet **activation-migration key** from the state name, so two
differently-provided facets sharing one name would collide on that key instead of addressing two
independent durable records.

### Multiple providers, and why they are not atomic

A definition can attach more than one provider -- for example a primary store plus a replica, or a
primary store plus an independently named audit trail:

```fsharp
let postgres = PersistentState.create<RoomState> "primary" "Postgres"
let redis = PersistentState.create<RoomState> "replica" "Redis"

let joinHandler context state userId =
    task {
        let next = { state with members = Set.add userId state.members }

        let primary = context.persistentState postgres
        primary.State <- next
        do! primary.WriteStateAsync()

        let replica = context.persistentState redis
        replica.State <- next
        do! replica.WriteStateAsync()

        return next, ()
    }
```

**Writes across descriptors are not atomic.** If the second `WriteStateAsync()` fails, the first
remains committed -- there is no two-phase commit or rollback across providers. If your application
needs coherent mirroring, failover, or repair across providers, register **one composite
`IGrainStorage`** that implements those semantics itself and address it through a single descriptor,
rather than relying on the functional runtime to coordinate two independent writes.

### Activation ordering and lifecycle hooks

Every attached facet loads before your code runs, in this order:

1. The activator creates every attached persistent facet and constructs the target.
2. Orleans' `SetupState` lifecycle stage loads durable state for each facet.
3. `OnActivateAsync` initializes ephemeral state, and every facet whose `RecordExists` is `false`
   receives its declared initializer's result **without writing it**.
4. The `onActivate` hook runs, if declared.
5. Declared reminders are reconciled, in declaration order.
6. Declared timers are created.
7. The activation is live; ordinary calls are admitted.

```fsharp
onActivate (fun _context state ->
    task {
        // observe or extend state after durable loading; the result is published in memory only
        return state
    })

onDeactivate (fun _context _reason _state ->
    task {
        // cleanup; no replacement state -- an explicit WriteStateAsync here is the only way
        // this hook persists anything
        return ()
    })
```

`onDeactivate` runs before Orleans' `OnStop` lifecycle stage. Deactivation performs **no implicit
write**: if you need the hook's observations persisted, call `WriteStateAsync()` explicitly inside
it. Process or silo failure cannot guarantee the hook runs at all, and neither hook nor storage
exceptions get a library-level retry -- a failure surfaces to the Orleans stop lifecycle, which logs
it and continues the remaining stop stages.

### Lifecycle-stage hooks: onLifecycle

`onLifecycle stage hook` hooks one of the four documented Orleans grain-lifecycle stages --
`First`, `SetupState`, `Activate`, or `Last` -- not an arbitrary int. Each stage accepts at most
one hook, and the hook is state-free:

```fsharp
type LifecycleStage = First | SetupState | Activate | Last
type LifecycleHook<'Actor, 'Key> = FunctionalGrainContext<'Actor, 'Key> -> Task<unit>
```

```fsharp
grainFor contract {
    defaultState (fun () -> initial)

    onLifecycle First (fun _context ->
        task {
            // earliest possible hook -- before persistent-state facets load
            return ()
        })

    onLifecycle SetupState (fun _context ->
        task {
            // Orleans loads persistent-state facets around here too
            return ()
        })

    onLifecycle Last (fun _context ->
        task {
            // last of the four numbered stages -- still runs BEFORE onActivate, not after
            return ()
        })

    // onActivate stays the one hook whose 'State parameter is meaningful.
    onActivate (fun _context state -> task { return state })

    handle (_.op) handler
}
```

**Where the four stages fall, verified by an integration probe that subscribes directly at the
raw Orleans stage number** (not assumed from the stage names -- the obvious-looking guess,
"`Last` is the final stage so it must run after `onActivate`", is wrong):

```
CreateInstance: facets created (not yet loaded)
  │
  ├─ First        (GrainLifecycleStage.First,      int.MinValue)
  ├─ SetupState   (GrainLifecycleStage.SetupState, 1000)  -- persistent-state facets load here
  ├─ Activate     (GrainLifecycleStage.Activate,   2000)  -- onLifecycle rejects this stage
  ├─ Last         (GrainLifecycleStage.Last,       int.MaxValue)
  │    (all four run, in ascending numeric order, to completion, BEFORE anything below --
  │     this is one Orleans-internal sequence, not four independent points)
  └─ OnActivateAsync (a separate step Orleans runs only after the whole sequence above
       completes -- not gated by the numbered Activate stage):
         1. state initializes (env.State.Initialize)
         2. onActivate hook runs, if declared
         3. reminders reconcile
         4. timers are created
```

Because every one of the four numbered stages runs before `OnActivateAsync`, `'State` cannot be
meaningful at any of them -- not even `Last`, the final one. That is why `onLifecycle`'s hook
carries no state at all, uniformly, rather than giving `Last` a second, state-carrying shape: there
is no "post-state" stage among the four to give one to. A hook that genuinely needs to read a
stored value can still do so explicitly through `context.persistentState`.

`onLifecycle Activate` is **rejected** at sealing with a diagnostic pointing at `onActivate` --
not because the two coincide in time (per the ordering above, they do not), but because "the
operation for activation-time behavior" should have exactly one name, and aiming at the stage
literally named "Activate" while state is not yet initialized there would be a footgun.

### Resolving services from a handler

Every callback -- handler, hook, timer, or reminder -- receives the same
`FunctionalGrainContext<'Actor, 'Key>`, so dependency injection is one member away:
`context.services : IServiceProvider`. Resolve anything registered on the silo exactly as you would
in ASP.NET Core:

```fsharp
open Orleans.Timers   // IReminderRegistry lives here, NOT in Orleans.Runtime

let registry = context.services.GetRequiredService<IReminderRegistry>()
```

(the reminder-retirement example
[below](#reminder-rename-and-removal-the-explicit-unregister-migration) does exactly this).
`context.grainFactory` binds further grain references from inside a handler; `context.grainId` and
`context.key` expose the activation's identity; `context.logger` is a logger already scoped to the
activation.

#### Composing with the KEEP-list APIs

`context.services` is also how a functional handler reaches the surfaces that are orthogonal to
the grain model — the ones the deprecation pass kept. **Streams** are the common case, and they
need the service route rather than a member: Orleans exposes `GetStreamProvider` only as an
extension on `Grain` / `IGrainBase` / `IClusterClient`, none of which a functional handler is, so
there is deliberately no `context.streamProvider`. A named provider is a **keyed** service, so
resolve it by its provider name:

```fsharp
open Orleans.Streams              // IStreamProvider
open Orleans.FSharp.Streaming     // the Stream module

handle (_.publish) (fun context state (text: string) ->
    task {
        let provider =
            context.services.GetRequiredKeyedService<IStreamProvider> "StreamProvider"

        let stream = Stream.getStream<string> provider "room" (string context.key)
        do! Stream.publish stream text
        return state, ()
    })
```

The same route works from `onActivate`, which is where a grain-side consumer takes its
subscription (`Stream.subscribe`), and the same `GetRequiredKeyedService` shape resolves a named
`IBroadcastChannelProvider`. `examples/feature-tour` runs both end to end.

## Delivery semantics

| Call kind | What a successful `Task` completion means |
|---|---|
| Default (acknowledged, sequential) | The target's handler ran to completion and its reply was received. |
| `readOnly` | Same as default, but the handler's returned state replacement is **discarded**, and Orleans schedules it to interleave with other read-only calls. |
| `oneWay` | The message **entered the local Orleans send path** -- nothing more. The caller's `Task<unit>` completes at that local acknowledgement, before the target has necessarily even started, let alone finished. |

**What a successful call does *not* imply**, in every case:

- that any storage write happened -- writes are only what the handler explicitly issued through
  `context.persistentState`, never automatic;
- for `oneWay`, that the target has run at all, or that it succeeded -- a one-way target failure
  is logged and traced on the target side, but is **never returned to that caller**; the caller
  already completed;
- that a retry, reload, or rollback occurred on failure -- there are none, by design (see
  [Immutable-state guidance](#immutable-state-guidance-deep-mutation-is-unguarded-by-design)
  below).

### Reentrancy: `reentrant` and `mayInterleave`

By default an activation runs one request at a time: a second call waits for the first to return.
Two contract operations widen that, and both reach Orleans' own machinery rather than a scheduler
of ours.

**`reentrant` — the whole grain.** Every request may enter an activation that is already executing
one:

```fsharp
grainContract<GatewayActor, string, GatewayApi> () {
    grainType "api.gateway"
    stringKey
    reentrant
}
```

This publishes the same `reentrant` grain-type property Orleans' own `[Reentrant]` attribute
publishes, so the activation is reentrant in exactly the sense a `[Reentrant]` grain class is.

**It does not make whole-state replacement concurrency-safe, and this is the part to read
twice.** A handler receives the state as it was when it started and publishes its replacement when
it returns, so two interleaved handlers that both write are last-writer-wins — the second one's
replacement silently overwrites the first's:

```text
slowAppend "slow"  reads []           parks                     returns [ "slow" ]
fastAppend "fast"           reads [], returns [ "fast" ]
                                                                state is [ "slow" ]
```

Reentrancy is for activations whose overlapping operations do not both write — a long call
awaiting an external service while short reads continue. Declare the non-mutating ones `readOnly`
so their replacement is discarded rather than published. (`examples/feature-tour` §13 prints this
lost update as part of its transcript, so it is evidence and not a warning.)

**`mayInterleave` — per request.** A predicate decides, from the request's own protocol metadata:

```fsharp
grainContract<GatewayActor, string, GatewayApi> () {
    grainType "api.gateway"
    stringKey
    mayInterleave (fun metadata -> metadata.OperationId = "cancel" || metadata.IsReadOnly)
}
```

The predicate receives `IFunctionalRequestMetadata` — grain type, contract version, operation ID,
the three admission flags, and the payload **length**. **Metadata only:** the argument payload is
never deserialized to decide admission, which is what keeps the transport's protocol-before-payload
invariant intact on a path Orleans runs *before* dispatch is reached at all. The predicate runs on
the activation's scheduling path, so it must be cheap, pure, and non-blocking.

Three behaviours are worth knowing before writing one:

- **Orleans consults it for the running request too.** `ActivationData.MayInvokeRequest` admits an
  incoming request when `predicate(incoming) || predicate(blocking)`. So an operation the predicate
  accepts also lets *anything* interleave with it while it is the one executing. Write the
  predicate as a statement about which operations are safe to overlap, not as a one-sided
  allow-list.
- **A throwing predicate rejects the call it was deciding.** Orleans logs the failure and rethrows,
  and the message is rejected to its caller as transient — the call fails, the activation is
  unharmed, and nothing retries in a loop. The runtime wraps the fault so the rejection names the
  grain type and the operation:
  `the 'mayInterleave' predicate of grain type 'api.gateway' failed while deciding whether
  operation 'cancel' may interleave.`
- **It is a process-wide registration keyed by the contract's actor brand.** The callback Orleans
  reflects off the grain class is static by necessity (Orleans discards the grain instance for a
  static `[MayInterleave]` callback), so it identifies its definition by its closed marker type
  rather than by a field — and that type comes from the actor brand alone. Two silos in one process
  hosting the **same** definition therefore register the same predicate, harmlessly; the later
  registration wins, which is what an in-process silo restart needs. Two **different** grain types
  sharing one actor brand and both declaring `mayInterleave` is rejected at configuration time,
  naming both grain types and the brand, because they would otherwise share one predicate and the
  second registration would silently decide admission for the first. Give each grain type its own
  actor brand — which is already required within a single silo.

Sealing rejects the combinations that could not mean anything:

| Declaration | Rejected when |
|---|---|
| `reentrant` / `mayInterleave` | both are declared — a reentrant activation interleaves everything, so a predicate could only be ignored |
| `alwaysInterleave (_.op)` | the contract declares `reentrant` or `mayInterleave` — Orleans admits an always-interleave request *before* it consults any predicate, so the flag is either redundant or unrefusable |

`readOnly` and `oneWay` stay legal under `reentrant`, deliberately: neither is only a scheduling
flag here. `readOnly` also makes the invocation state-neutral (its replacement is discarded and its
persistent-state facade rejects the setter), and `oneWay` is a delivery mode with no reply. Both
keep their full meaning on a reentrant grain.

**Cancellation is cooperative and never rolls anything back.** `callCancellable` on an
acknowledged call propagates a target-local `CancellationToken` that a long-running handler may
observe (e.g. inside a `Task.Delay` or another cancellable await) -- but cancelling it does not
undo any explicit write or external effect the handler already performed, and does not stop the
handler from running to completion if it doesn't check the token. For a one-way
`callCancellable`, an *already-cancelled* token short-circuits to a cancelled `Task` locally, but
once sent, a later cancellation has no remote effect at all -- the delivered one-way context
always uses `CancellationToken.None`.

## Push to clients: functional observers

A grain call goes one way -- a client calls in and waits for a reply. Push goes the other way, and
until now F# could not reach it without adding a C# project: Orleans' proxy generators are Roslyn
source generators that never run over F#, so an `IGrainObserver` declared in F# has no generated
proxy and `CreateObjectReference` fails on it. That is Orleans' constraint, identical for the
`grain { }` CE and for class grains.

**Functional observers absorb it.** Exactly as one fixed request carries every grain operation,
one C#-declared interface inside `Orleans.FSharp.Abstractions` carries every application observer.
Above it, an observer is an ordinary F# record and you write no interface and no generated code.

### The observer

An observer is a brand and a handler record, exactly like a grain contract -- except that every
field is a *push* operation, `'Msg -> Task<unit>`:

```fsharp
type RoomObserver = private RoomObserver of unit

type ChatMessage = { author: string; text: string }

[<NoEquality; NoComparison>]
type RoomObserverApi =
    { onMessage: ChatMessage -> Task<unit>
      onClosed: string -> Task<unit> }

let roomObserverContract =
    observerContract<RoomObserver, RoomObserverApi> () {
        observerType "chat.room.observer"   // defaults to the brand's simple name
        version 1                           // defaults to 1
    }
```

A handler record *is* an API record whose replies are all `unit`, so the shape rules, the selector
form (`_.onMessage`), and the diagnostics are the ones you already know. The single
observer-specific rule: a push operation that returns anything but `Task<unit>` fails contract
construction -- an observer never returns data. There is no key operation either; an observer is
addressed by the reference inside its handle, not by a domain key.

### Subscribing

The client wraps its handlers and gets back a typed, serializable **handle**:

```fsharp
let handle =
    FunctionalObserver.create roomObserverContract client
        { onMessage = fun m -> task { printfn "%s: %s" m.author m.text }
          onClosed  = fun reason -> task { printfn "closed: %s" reason } }

do! room.subscribe handle          // an ordinary operation argument
```

`FunctionalObserverHandle<'Brand,'Api>`'s two type parameters are phantom -- they exist so an
observer of one brand cannot be handed to a grain expecting another, and neither appears on the
wire. Declare the grain operation with the handle as its argument type:

```fsharp
type ChatRoomApi =
    { subscribe: FunctionalObserverHandle<RoomObserver, RoomObserverApi> -> Task<int>
      say: ChatMessage -> Task<int> }
```

### Pushing

```fsharp
do! FunctionalObserver.notify handle (_.onMessage) message
```

`notify` resolves its selector on every call -- fine off the hot path, but the same rule that
governs a bound grain call applies to observer sends too: resolve once, not per push. Where push
volume matters, resolve once with `notifier` and reuse the closure it returns:

```fsharp
let push = FunctionalObserver.notifier handle (_.onMessage)   // resolved once, here
do! push message                                              // invokes no selector, ever again
```

Or fan out to many subscribers with a liveness window, the functional equivalent of
`FSharpObserverManager`:

```fsharp
type RoomState = { observers: FunctionalObserverManager<RoomObserver, RoomObserverApi> }

grainFor chatContract {
    defaultState (fun () ->
        { observers = FunctionalObserverManager<RoomObserver, RoomObserverApi>(TimeSpan.FromMinutes 5.0) })

    handle (_.subscribe) (fun _ state handle ->
        task {
            state.observers.Subscribe handle
            return state, state.observers.Count
        })

    handle (_.say) (fun _ state message ->
        task {
            do! state.observers.Notify (_.onMessage) message
            return state, state.observers.Count
        })
}
```

A subscription that is not re-subscribed within the window is dropped on the next notification or
the next `RemoveExpired()`. `Notify` carries the same hot-path discipline into the fan-out: it
resolves its selector once per call, before looping over subscribers, not once per subscriber --
so a room of a thousand listeners costs one selector resolution per broadcast, not a thousand.

### Delivery is best-effort, and that is deliberate

`notify` completes when the notification has **entered the local send path** -- not when the
observer has handled it. The dispatch is `[OneWay]`, and that is load-bearing rather than an
optimisation: under an acknowledged dispatch a single subscriber whose reference has been released
blocks the notifying grain's handler until Orleans times the message out, thirty seconds, for a
subscriber the application already forgot about.

So: an observer whose handler throws is logged **on the observer's side** and never reported to
the notifying grain, and a dead subscriber costs the notifying handler nothing. This is Orleans'
own observer semantics, not a new rule.

### Lifetime

Orleans holds an observed object **weakly** -- nothing inside Orleans keeps your handler object
alive. The handle anchors it, so the rule is simply *keep the handle, keep receiving*. Release it
when done:

```fsharp
FunctionalObserver.unsubscribe client handle
```

`unsubscribe` is idempotent: releasing a reference that is already gone -- a second release, a
torn-down client, an object collected before you got round to it -- is not an error, because the
post-condition already holds. Put it in a `finally` without a guard.

### Where a handle may appear

| Shape | Works? | Why |
|---|---|---|
| `subscribe: Handle -> Task<_>` | yes | Orleans owns the argument and routes it to the handle's codec |
| `subscribe: (Handle * string) -> Task<_>` | yes | Orleans owns tuples and routes each element separately |
| `subscribe: { observer: Handle; … } -> Task<_>` | **no** | the F# binary codec owns records whole, and has no codec for an object reference |
| `subscribe: Handle option -> Task<_>` | **no** | same reason |
| a handle in **persistent state** | **no** | same reason -- and that is the point |

The last row is a feature. A live object reference cannot survive an activation, let alone a
storage round-trip, so a state type carrying a handle is refused outright rather than written as
something that *looks* restorable. Keep subscriptions in ephemeral state (a manager), never in a
persisted record.

### Registration

Nothing extra. `AddFunctionalGrainClient` on the client and `AddFunctionalGrain` on the silo
already register the observer transport -- a process that can call a functional grain can also be
pushed to by one.

## Immutable-state guidance: deep mutation is unguarded by design

State-neutral handlers (`readOnly`, `alwaysInterleave`) and every handler's discarded-on-failure
replacement all rely on one assumption: **the state graph is immutable**. The runtime enforces
this only at the outermost level --

- the persistent-state facade can reject its `State` **setter** in a `readOnly`/`alwaysInterleave`
  callback;
- a handler that fails contributes no return-value publication;

-- but it **cannot intercept in-place mutation of an object reachable from state**, whether that
object came from the primary `state` argument, an additional holder's `.State` getter, or
anywhere else. If your state record holds a mutable field, a `ref` cell, an array, or any other
mutable .NET object, and a `readOnly` handler mutates it directly instead of returning a new
value, that mutation is real and visible immediately -- discarding the handler's return value does
not undo it, because there was never a copy to discard *from*.

**Use immutable F# state** -- records, `Set`, `Map`, and immutable lists, as the sample and every
shipped example do -- and this hazard never arises. If you must hold a mutable value, treat it as
an explicit, deliberate exception and document why it's safe under concurrent read-only/interleave
scheduling.

## Timers, reminders, and collection age

`onTimer` and `onReminder` in `grainFor { }` declare recurring work directly on the definition --
no `Grain.RegisterGrainTimer` / `RegisterOrUpdateReminder` calls of your own, and no separate class
grain:

```fsharp
onTimer
    "poll"
    (GrainTimerCreationOptions(DueTime = TimeSpan.Zero, Period = TimeSpan.FromSeconds 5.0, KeepAlive = false))
    (fun _context state ->
        task {
            return { state with polls = state.polls + 1 }
        })

onReminder "tick" TimeSpan.Zero (TimeSpan.FromMinutes 5.0) (fun _context state _status ->
    task {
        return { state with ticks = state.ticks + 1 }
    })
```

Both hooks replace the whole state with their return value, under Orleans' normal (non-interleaving)
turn scheduling for that callback. `onTimer` takes a `GrainTimerCreationOptions` -- `DueTime`,
`Period`, `Interleave`, and `KeepAlive` are copied into the definition's immutable metadata at
sealing time. A definition may declare at most one `collectionAge`, `stateFrom`, `onActivate`, and
`onDeactivate`, but any number of `onTimer` / `onReminder` declarations, each under its own name.
Declared reminders are reconciled once per successful activation, in declaration order: an **added**
reminder or a **changed** due-time/period updates the durable registration automatically on the next
activation. Renaming or removing one is different and needs an explicit migration step -- see
[below](#reminder-rename-and-removal-the-explicit-unregister-migration).

`collectionAge age` sets the Orleans idle-deactivation threshold for this definition's activations:

```fsharp
collectionAge (TimeSpan.FromMinutes 30.0)
```

Once an activation has received no activity for at least `age`, a periodic collection scan may
deactivate it and release its memory -- an eligibility threshold, not an exact timer. Incoming calls,
reminders, and stream events count as activity; outgoing calls, arbitrary I/O, and a timer declared
with `KeepAlive = false` do not extend it. A timer declared with `KeepAlive = true` does, under stock
Orleans timer semantics. A later call creates a fresh activation: durable state reloads from storage,
and ephemeral state runs its initializer again. `collectionAge` is *not* a data TTL -- it never
deletes storage and never selects when state is written; it only governs when the in-memory
activation itself may be collected. Omitting it leaves the host's stock Orleans collection policy in
effect, and any state change the application did not explicitly write is lost when that activation
ends.

## Implicit subscriptions: onStream and onBroadcast

`onStream provider ns hook` subscribes the grain **type** to one Orleans stream namespace.
Publishing to `StreamId.Create(ns, key)` then activates the grain whose identity encodes `key` --
creating it if it does not exist -- and delivers the item to the hook. `onBroadcast provider ns
hook` is the same thing over a broadcast-channel provider.

```fsharp
grainFor InboxApi.contract {
    defaultState (fun () -> { mail = [] })

    onStream "StreamProvider" "chat.messages" (fun context state (item: Message) ->
        task { return { state with mail = state.mail @ [ item ] } })

    onBroadcast "BroadcastProvider" "chat.control" (fun context state (item: Control) ->
        task { return state })

    handle (_.read) (fun _ state () -> task { return state, state.mail })
}
```

Nothing else is needed: no attribute, no class grain, no code generation. The item type is inferred
from the hook, so it usually wants an annotation (`(item: Message)`). Several declarations are
allowed, one per `(provider, namespace)` pair.

**Publishing to one of these grains needs the contract's key encoding.** Orleans routes an
implicit delivery to `GrainId.Create(grainType, streamId.Key)` -- the stream key bytes verbatim --
so the stream key must be the grain key as this contract encodes it. `stringKey` and `guidKey`
agree with `StreamId.Create(ns, key)`; `int64Key` does **not** (`StreamId.Create(ns, 42L)` writes
decimal `"42"`, the codec writes hexadecimal `"2A"`, and the naive publish silently reaches the
grain whose key reads as `0x42` = 66), and the compound codecs have no overload at all. Ask the
contract instead:

```fsharp
let streamId  = FunctionalGrain.streamId  InboxApi.contract "chat.messages" inboxKey
let channelId = FunctionalGrain.channelId InboxApi.contract "chat.control"  inboxKey
```

**Delivery follows the `onTimer` rules exactly.** A delivery is an ordinary non-reentrant grain
call; the hook receives the whole current state and returns the replacement, which is published in
memory **only on a successful return**; the runtime performs no storage write of its own; and
`context.cancellationToken` is `CancellationToken.None`, because the Orleans delivery path supplies
none.

`context.streamSequenceToken` carries the Orleans cursor of the item being delivered -- `Some` for
an `onStream` delivery on a rewindable provider (Orleans' memory streams are), `None` otherwise,
and always `None` for `onBroadcast` (a channel has no cursor). **The runtime never rewinds with
it**: a fresh activation resumes at the subscription's current position. It is exposed so an
application can checkpoint or de-duplicate, which matters because delivery is at-least-once: an
`onStream` hook that throws makes Orleans **redeliver the same item** with backoff for up to
`StreamPullingAgentOptions.MaxEventDeliveryTime` (one minute by default), after which the item is
dropped and the stream continues. An implicit subscription is never faulted by a delivery failure.

`onBroadcast` is not retried at all -- a publish is a direct fan-out grain call. A throwing hook is
logged at `Error` by `BroadcastChannelWriter` under Orleans' default
`BroadcastChannelOptions.FireAndForgetDelivery = true`, and faults the publisher's `Publish` when
that option is `false`. **An item of the wrong type takes the same path rather than vanishing**:
Orleans routes a runtime-type mismatch into the subscription's error callback as an
`InvalidCastException`, and this runtime faults that callback so the mismatch is logged or thrown
instead of being reported as delivered. The hook is not entered and the subscription stays
healthy.

Rejections, all at the earliest stage that can see them:

| Rule | Stage |
|---|---|
| Blank provider or namespace | definition sealing |
| A repeated `(provider, namespace)` pair on one transport | definition sealing |
| `statelessWorker` combined with `onStream` / `onBroadcast` | definition sealing |
| A provider name no registered provider answers to | silo startup |

The `statelessWorker` rejection is Orleans': `SiloStreamProviderRuntime.BindExtension` throws
"The extension ... cannot be bound to a Stateless Worker", and implicit delivery addresses one
activation identity derived from the stream key, which multiplexed local activations cannot honor.

One caveat: Orleans' implicit-subscription binding names a *namespace*, not a provider. If the silo
runs a second stream provider and an item reaches a declared namespace through it, Orleans still
routes it to this grain type; the runtime matches on `(provider, namespace)`, logs a warning, and
leaves that item undelivered. Batch delivery (`IAsyncBatchObserver`) is not exposed -- a hook
receives one item at a time.

`examples/feature-tour` status-matrix row 11 demonstrates all of this end to end, including the
`activations: 1` line proving the publish itself created the activation.

## Placement: statelessWorker and placement

`statelessWorker maxLocalWorkers` multiplexes a grain type across up to `maxLocalWorkers` local
activations per silo (Orleans' `StatelessWorkerPlacement`) -- useful for stateless, CPU-bound work
where one activation per grain id would otherwise serialize concurrent calls:

```fsharp
grainFor contract {
    defaultState (fun () -> Guid.NewGuid().ToString())  // one id per activation
    statelessWorker 4
    handle (_.work) handler
}
```

`placement strategy` selects one stock Orleans placement strategy instead:

```fsharp
type PlacementStrategy = Random | PreferLocal | ActivationCountBased | ResourceOptimized

placement PreferLocal
```

`Random` is Orleans' own default and needs no explicit configuration; it is included so an
application can still name it. Both operations publish the exact manifest properties the
corresponding Orleans attribute would (`placement-strategy`, plus `max-local-instances` /
`remove-idle-workers` / `unordered` for `statelessWorker`) through the registry's own properties
provider -- the same mechanism `examples/feature-tour`'s status-matrix row 12 demonstrates end to
end, including the measured 8-concurrent-calls-to-4-activations signature.

`statelessWorker` and `placement` are mutually exclusive (in either declaration order), and
`statelessWorker` additionally rejects `stateFrom`, `usePersistentState`, `onReminder`, and
`collectionAge` -- durable identity and idle collection age are both meaningless for activations
Orleans may create, deactivate, and re-create at will to satisfy the local-activation cap.

## Distributed ACID transactions

A functional grain participates in a real Orleans distributed transaction. There is no separate
transaction runtime: a call whose operation declares a policy is carried on Orleans' own
transactional invokable base, so the ambient transaction is joined on the way out, created or
joined on the way in, and reported back to the caller exactly as it is for a
`[Transaction]`-attributed CodeGen grain method.

Two declarations are involved.

**On the contract** — the per-operation policy:

```fsharp
let account =
    grainContract<AccountActor, string, AccountApi> () {
        grainType "bank.account"
        stringKey
        transactional Orleans.TransactionOption.CreateOrJoin (_.deposit)
        transactional Orleans.TransactionOption.CreateOrJoin (_.withdraw)
        transactional Orleans.TransactionOption.CreateOrJoin (_.balance)
    }
```

`transactional` takes **Orleans' own `Orleans.TransactionOption`** — the six members are identical
on Orleans 10.1.0 and 10.2.2, and the admission byte encodes the value directly, so there is no
mapping to drift. (The legacy `Orleans.FSharp.Transactions.TransactionOption` union belongs to the
classic `grain { }` / CodeGen path; the two share a simple name, so open only the namespace you
mean.)

**On the definition** — the transactional state:

```fsharp
type Ledger = { balance: decimal; entries: string list }

let ledger = TransactionalState.create<Ledger> "ledger" "TransactionStore"

grainFor account {
    defaultState (fun () -> ())
    transactionalStateFrom ledger (fun _ -> { balance = 0m; entries = [] })

    handle (_.deposit) (fun context state (amount: decimal) ->
        task {
            do!
                (context.transactionalState ledger)
                    .update (fun value -> { value with balance = value.balance + amount })

            return state, ()
        })

    handle (_.balance) (fun context state () ->
        task {
            let! value = (context.transactionalState ledger).readWith (fun l -> l.balance)
            return state, value
        })
}
```

`Ledger` is an ordinary immutable F# record. Orleans' own `ITransactionalState<'T>` requires
`'T : class, new()` and applies an update by **mutating** the instance it stores, which no F#
record can satisfy; the runtime therefore stores its own mutable box and application code only
ever sees `'State -> 'State`. That is the whole reason the box exists, and it is also what makes
in-place mutation of transactional state impossible from application code.

### The facade

`context.transactionalState descriptor` returns a facade bound to the invocation, exactly like
`context.persistentState`:

| Member | Shape | Notes |
|---|---|---|
| `read()` | `unit -> Task<'State>` | The only member whose result is the stored value, so it is the only one Orleans deep-copies before returning. |
| `readWith project` | `('State -> 'R) -> Task<'R>` | `project` runs inside Orleans' read lock; its result is the application's own value and is returned uncopied. |
| `update next` | `('State -> 'State) -> Task<unit>` | `next` runs inside Orleans' write lock. |
| `updateWith next` | `('State -> 'State * 'R) -> Task<'R>` | Same, returning a result alongside the replacement. |

All four take **synchronous** functions, which is deliberate rather than an omission. Orleans runs
them inside the transactional state's reader-writer lock and throws `LockRecursionException` if
the same state is re-entered from inside one; a function that cannot be awaited cannot call
another grain, another transactional state, or any I/O from inside that lock.

A transactional state has no "record exists" flag — Orleans materializes an unwritten state with
`new TState()` — so `transactionalStateFrom` takes the initial value the first read observes. It
is stored only when an update actually writes.

### Which operations are transaction-scoped

Three of the six options make Orleans create or join a transaction before the handler runs, and
they are exactly the three for which Orleans' own `TransactionRequestBase.IsTransactionRequired`
is true:

| Option | Transaction-scoped | Meaning |
|---|---|---|
| `Create` | yes | Always starts a new transaction, even inside one. |
| `CreateOrJoin` | yes | Joins the caller's transaction, or starts one. |
| `Join` | yes | Requires the caller to be inside a transaction. |
| `Supported` | no | Not transactional, but the caller's context is forwarded onward. |
| `Suppress` | no | Not transactional; a caller's context is hidden from it. |
| `NotAllowed` | no | Refuses to be called from inside a transaction. |

**Inside a transaction-scoped operation the only durable effect available is a
`transactionalStateFrom` facet.** The handler's replacement primary state is discarded exactly as a
`readOnly` handler's is, and its persistent-state facades reject the `State` setter and every
storage call, with a diagnostic naming the reason. Neither an in-memory publication nor a storage
write has any participant that could undo it, so allowing either would let one aborted transaction
leave the activation half-updated.

`readOnly` composes: a transaction started by a `readOnly` transactional operation is started
read-only, and the transactional facade refuses every update.

### Re-execution semantics

**Orleans does not re-execute a handler when a transaction aborts.** There is no retry loop in
`TransactionRequestBase.Invoke`, and `ReaderWriterLock.EnterLock` invokes the read or update
callback exactly once. When a participant throws, when a lock cannot be acquired in time, or when
the commit protocol fails, the transaction is aborted and the caller receives an
`OrleansTransactionException` (usually `OrleansTransactionAbortedException`).

The consequences, in order of how often they surprise people:

1. **A retry is the caller's decision, not the runtime's.** Nothing in this library retries an
   aborted transaction. If a transaction should be attempted again, the application must call it
   again — and every participant handler will then run again, from the beginning.
2. **A handler runs at most once per attempt**, so "exactly once" holds per successful attempt and
   not across retries. An attempt either commits everything or leaves nothing behind, but two
   attempts are two runs of your code.
3. **Effects outside transactional state are not covered.** Sending a message, writing a log,
   calling a non-transactional grain, or mutating a captured variable happens whether or not the
   transaction commits, and happens again on every retry. The state-neutrality rule above removes
   the two the runtime *can* see (primary state and persistent facets); it cannot see the rest.
4. **The read and update functions must be pure over the value they are handed.** They run inside
   Orleans' lock and their result is what gets stored. The API makes the common failure impossible
   — `'State -> 'State` has no access to the stored object — but a function that mutates something
   it captured from outside is still your own hazard.

`examples/feature-tour` §14 prints the measurement rather than asserting the claim: an aborted
transfer enters the `withdraw` handler exactly once, and the counter is shown to be live by an
application-driven retry moving it to two.

**Catching a participant's exception does not un-abort the transaction.** A participant that
faulted has already recorded the fault on the shared `TransactionInfo`, and the caller's outgoing
filter joins that info when the call returns — so `try ... with` around a transactional grain call
changes what your handler returns and nothing else: the transaction is still doomed and the
outermost caller still sees `OrleansTransactionAbortedException`. Handle the failure at the
boundary that owns the retry, not inside a participant.

### Sealing and startup rules

Contract sealing rejects:

- `transactional` applied twice to one operation, and an undefined `TransactionOption` value;
- `transactional` with `oneWay` — a one-way call has no reply, so the participants it enlists
  could never be reported back to the transaction;
- `transactional` with `alwaysInterleave` — Orleans admits an always-interleave request before any
  interleaving policy is consulted, so two turns of one activation could hold transactional locks
  on the same states at once.

Definition sealing rejects:

- two transactional facets under one state name (the name is part of the `ParticipantId` Orleans
  addresses during the commit protocol, and of the storage key);
- a transactional facet no operation can reach — every callback other than an operation declared
  `Create`, `CreateOrJoin`, `Join`, or `Supported` runs without a `TransactionContext`, which
  Orleans requires for both `PerformRead` and `PerformUpdate`;
- `transactionalStateFrom` on a contract whose `grainType` is derived from the actor brand, and
  `transactionalStateFrom` with `statelessWorker`, for the same durable-identity reasons
  `stateFrom` rejects them.

A `transactional` operation on a definition with **no** transactional facet is accepted: a
state-free participant is a supported Orleans shape, and it is what every orchestrator ("unit of
work") grain looks like.

Silo startup rejects a definition that declares any transaction option or attaches any
transactional state when the silo has no `UseTransactions()`, and rejects a transactional storage
name that resolves to neither a keyed `ITransactionalStateStorageFactory` nor a keyed
`IGrainStorage` — the exact resolution order `NamedTransactionalStateStorageFactory.Create`
performs.

### Wire compatibility

The transaction option travels in bits 3-5 of the admission byte, which earlier versions of this
library treat as reserved and reject. A transactional call therefore requires **both ends on a
version that has this feature**; a non-transactional call is unaffected, because those bits are
zero for it and the byte is unchanged. During a rolling upgrade, deploy before declaring
`transactional` on an operation callers already use — an old silo answers such a call with the
reserved-bit diagnostic, and an old client sending to a new silo is refused by the ordinary
admission-flag comparison.

### Hosting

```fsharp
silo.UseTransactions()
silo.AddMemoryGrainStorage "TransactionStore"
silo.AddFunctionalGrain accountDefinition
```

A client that only *calls* a transactional operation needs nothing beyond
`AddFunctionalGrainClient()`: a `Create` or `CreateOrJoin` call from outside a transaction starts
the transaction on the silo that receives it. `UseTransactions()` on the client builder is needed
only when the client itself drives `ITransactionClient.RunTransaction`.

### What this does not give you

- **No automatic retry**, as above.
- **No transactional primary state.** `stateFrom` and `usePersistentState` are not participants; a
  transaction-scoped operation cannot write them at all.
- **No transactional streams, timers, reminders, or observer pushes.** None of them carries a
  transaction context, so the facade refuses the facet inside them.
- **No cross-cluster transactions.** Orleans' transaction manager is per-cluster.
- **The snapshot copy is a serializer round trip.** Before the first write of each transaction the
  runtime copies the stored value through the exact-type payload codec — the same byte boundary
  the transport puts between an argument and its handler. That is one serialize plus one
  deserialize per transaction per written state; in exchange, an application that mutates its own
  state object in place cannot corrupt the version an abort has to restore.

## Event sourcing: `journaledGrainFor`

A **journaled** definition is the second definition kind over the same contract layer. The
contract, the transport, the client binding, the C# facade, and every contract-level operation
are unchanged; three things differ:

| | `grainFor` | `journaledGrainFor` |
|---|---|---|
| initial state | `defaultState` / `initialState` | `initialEventState` |
| what a handler returns | `state', reply` | `events, reply` |
| where the state comes from | memory, or a `stateFrom` holder | the fold of the journal |

```fsharp
let accountDefinition =
    journaledGrainFor accountContract {
        initialEventState (fun key -> { balance = 0m })
        apply (fun state event -> match event with Deposited amount -> { state with balance = state.balance + amount })

        logProvider "LogStorage"
        journalStorage "Journals"

        handle (_.deposit) (fun _ state amount ->
            task { return [ Deposited amount ], state.balance + amount })
    }
```

```fsharp
silo.AddMemoryGrainStorage "Journals" |> ignore
silo.AddLogStorageBasedLogConsistencyProvider "LogStorage" |> ignore
silo.AddFunctionalJournaledGrain accountDefinition |> ignore
```

The state lives in an Orleans log-consistency provider — the same machinery `JournaledGrain`
uses, driven directly rather than by deriving from it. The runtime appends a handler's returned
events as one atomic batch and waits for the provider to confirm them **after the handler returns
and before the reply leaves the activation**, so a caller that got a reply is looking at
confirmed state. A handler that returns an empty event list performs no storage write at all.

`apply` is `'State -> 'Event -> 'State` and must be pure: it runs when an event is raised **and
again on every later activation that replays the journal**, so a fold that read the clock or
called a service would produce a different state on replay than the one the application saw.

The definition kind is invisible to callers, to the C# facade, and to every contract-level
operation. What it changes on the definition side — which `grainFor` operations carry over,
which are refused and why, what each built-in provider actually stores, why there is no
`snapshotEvery`, and what the model does not give you — is in
**[event-sourcing.md](/orleans-fsharp/event-sourcing/)**.

## Reminder rename and removal: the explicit unregister migration

Reminder reconciliation (`RegisterOrUpdateReminder`, run per declared reminder on every successful
activation) keeps an **added** reminder or a **changed** due-time/period in sync automatically --
the next activation updates the durable registration in place.

**Renaming or removing a reminder declaration is different, and nothing automatic happens.** The
registration lives in Orleans' reminder table, not in your definition, so it survives the
deployment that dropped (or renamed) the declaration and keeps firing on schedule. Every tick then
arrives at a name the current definition no longer declares, which fails that callback and is
logged with the grain and reminder identity (`Grain {GrainId} of functional grain type {GrainType}
received unknown reminder {ReminderName}`) -- for as long as the stale registration exists.
Nothing retires it on its own, deliberately: the runtime cannot distinguish a genuine rename from
a grain type that is only temporarily not deployed, and guessing wrong would silently destroy a
durable schedule.

The migration is an explicit, idempotent application step through the stock `IReminderRegistry`,
resolved from `context.services` (the functional context intentionally exposes no reminder API of
its own). `IReminderRegistry` is declared in the **`Orleans.Timers`** namespace (assembly
`Orleans.Reminders`) — not `Orleans.Runtime`, which is the natural guess and does not compile:

```fsharp
open Orleans.Timers

handle (_.retireStaleReminder) (fun context state () ->
    task {
        let registry = context.services.GetRequiredService<IReminderRegistry>()
        let! stale = registry.GetReminder(context.grainId, "old-name")

        if not (obj.ReferenceEquals(stale, null)) then
            do! registry.UnregisterReminder(context.grainId, stale)

        return state, ()
    })
```

Operationally:

1. Deploy the new definition -- the stale name starts failing, and each failure is visible in the
   logs, keyed by grain id.
2. Drive the retiring operation (above) over every grain that carried the old name.
   `registry.GetReminders context.grainId` enumerates what one grain still has registered, which
   also tells you when a given grain is done.
3. Keep the retiring operation deployed until every affected grain has been visited.

A **rename** is this removal, plus the new `onReminder` declaration in the same or a later
deployment -- there is no in-place rename operation.

## The `FunctionalGrain` static-class inference rule

The point-free binding --

```fsharp
let ref = FunctionalGrain.ref contract
```

-- infers the complete concrete type `IGrainFactory -> 'Key -> 'Api` with **no annotation
anywhere**, and callers use it as `ref factory key`. This works because of a specific interaction
between how F# handles member parameters and the value restriction, worth knowing before you hit
it:

`FunctionalGrain.ref` declares `contract` as its **only** formal parameter and returns the
remaining `factory -> key -> api` function as its result, rather than declaring `factory` as a
second curried parameter. F# inserts *flexibility* (implicit upcasting, so any `IGrainFactory`
implementation such as `IClusterClient` is accepted) only for a member's **declared** parameters.
If `factory: IGrainFactory` were a second declared parameter, every partial application
(`FunctionalGrain.ref contract`) would need to stay generic in a flexible `'_a :> IGrainFactory`,
which hits the value restriction (`FS0030`) for a `let`-bound point-free value like `ref` above.
Keeping `contract` as the only declared parameter avoids that: the partial application is
concrete, and ordinary argument subsumption still lets any `IGrainFactory` be applied to the
*returned* function at a normal call site.

One consequence follows directly, and is worth knowing at call sites: because the factory is
applied to the *returned* function rather than to a declared parameter, F# does **not** insert
subtype flexibility for it there. Annotate a caller's factory parameter as plain `IGrainFactory` --
any implementation, `IClusterClient` included, is accepted by ordinary subsumption. A flexible
`#IGrainFactory` annotation buys nothing at that position and is reported as `FS0064` ("less
generic than indicated by its type annotations"), which is a hard error under
`TreatWarningsAsErrors`.

If your own code must stay generic over the factory type -- for example a class with an
`'F when 'F :> IGrainFactory` type parameter, which would otherwise fail to compile with
`FS0660`/`FS0663` -- there are two diagnostic-free forms:

```fsharp
// 1. Call through an application-owned point-free binding: flexibility is inserted at every
//    use of a named binding, even when the compiler has to look through its function type.
let ref = FunctionalGrain.ref RoomApi.contract
let api = ref factory key

// 2. Or upcast once at the call site.
let api = FunctionalGrain.ref RoomApi.contract (factory :> IGrainFactory) key
```

`FunctionalGrain.rawRef` follows the identical rule and returns the typed
`FunctionalGrainRef<'Actor, 'Key, 'Api>` wrapper (`key`, the cached `api` record, selector-based
`call` and `callCancellable`, and their streaming counterparts `stream` and `streamCancellable`)
instead of the bare API record.

## Running a silo from a standalone F# process

Orleans builds its grain manifest by scanning assemblies that carry the code-generated
`[assembly: ApplicationPart]` / `[assembly: TypeManifestProvider]` attributes, and it takes that
snapshot the **first** time `AddSerializer` runs -- which is inside `UseOrleans`, i.e. before your
own configuration code has touched anything else.

Orleans' source generators are Roslyn generators, so **they never run on an F# project**: an F#
assembly carries none of those attributes, and neither does anything it references *by way of F#
only*. In a standalone F# host the practical consequence is that assemblies you only reach through
an F# hop are absent from that first snapshot, and the first call that needs a grain class from one
of them fails with:

```text
System.ArgumentException: Could not find an implementation for interface
    Orleans.Storage.IMemoryStorageGrain
```

even though nothing about your own code is wrong.

**If you build your configuration with `siloConfig { }`, this is already handled for you.**
Building the configuration value pre-loads every Orleans assembly `Orleans.FSharp.Runtime`
references, which is exactly the set an F# host reaches only through an F# hop:

| Pre-loaded assembly | The grain you would otherwise fail to find |
|---|---|
| `Orleans.Persistence.Memory` | `Orleans.Storage.IMemoryStorageGrain` — `addMemoryStorage` |
| `Orleans.Reminders` | `Orleans.IReminderTableGrain` — `addMemoryReminderService`, and therefore any `onReminder` |
| `Orleans.Streaming` | the memory stream queue grains — `addMemoryStreams` |
| `Orleans.FSharp.Abstractions` | `FSharpGrainImpl` and the functional transport proxies |

`SiloConfig.applyToHost` and `applyToSiloBuilder` run the same pre-load, but the placement in
`siloConfig { }` is what makes a `WebApplicationBuilder` host work: such a host has no
`applyToHost`-equivalent wrapper and calls `applyToSiloBuilder` from *inside*
`builder.Host.UseOrleans(...)`, by which point `UseOrleans` has already taken the manifest
snapshot. `let config = siloConfig { ... }` runs before `UseOrleans` in every host shape.

You still have to do it yourself for any assembly the runtime does not reference — a third-party
storage, reminder, or streaming provider, or a C# interop assembly of your own carrying observer
proxies or an implicit-subscriber grain — and for a host that hand-rolls `builder.UseOrleans(...)`
with no `siloConfig { }` value at all. Touch any public type from the assembly, *before* that call:

```fsharp
typeof<Orleans.IReminderTable>.Assembly |> ignore    // Orleans.Reminders; IReminderTableGrain
                                                     // itself is internal, this public type is
                                                     // in the same assembly
typeof<Orleans.Streams.IStreamProvider>.Assembly |> ignore   // Orleans.Streaming
typeof<IMyObserver>.Assembly |> ignore                       // your own C# interop assembly
```

Anything done inside the `UseOrleans` delegate is already too late, because the snapshot is taken
before the delegate runs. The symptom is always the same shape — `Could not find an implementation
for interface <the grain interface that lives in the missing assembly>` — and it surfaces at the
first call that needs that grain rather than at startup. `examples/feature-tour` force-loads its
own C# interop assembly this way, and the same pattern appears in
`tests/Orleans.FSharp.Integration/ClusterFixture.fs` for that suite's assemblies.

This is also why the functional runtime is easier to host than the per-grain-interface
(`grain { }` + `Orleans.FSharp.CodeGen`) model: the functional transport's proxies are generated
once, ahead of time, into the C# `Orleans.FSharp.Abstractions` assembly, so a functional grain
needs no per-project C# bridge assembly at all. The legacy per-grain-interface demos in
`src/Orleans.FSharp.Sample` are the counter-example -- their proxies live in
`src/Orleans.FSharp.CodeGen`, which *references* the sample project and therefore cannot be
referenced back from it; the sample prints an explicit note and skips them, and they are exercised
by `tests/Orleans.FSharp.Integration`, which does load that bridge assembly.

### A quick script, not a host builder: `FunctionalScripting.startOnPorts`

`Scripting.startOnPorts` (the fixed-recipe `.fsx` silo helper) has no configuration hook of its
own, so it could not host a functional grain definition -- `AddFunctionalGrain` needs a silo
builder to call, and `Scripting.startOnPorts` builds and starts its host internally.
`Orleans.FSharp.Runtime.FunctionalScripting.startOnPorts` closes this: it takes the same
`siloPort`/`gatewayPort` pair plus the functional grain definitions to host, boxed with
`FunctionalGrainRegistration.of'` (erasing their four type parameters into one list), applies
each through `AddFunctionalGrain` inside the same builder callback `Scripting.startOnPorts`
itself uses, and performs the manifest pre-load above automatically:

```fsharp
#r "nuget: Orleans.FSharp"
#r "nuget: Orleans.FSharp.Runtime"

open Orleans.FSharp

let! handle =
    FunctionalScripting.startOnPorts 11511 30001 [ FunctionalGrainRegistration.of' myDefinition ]

let api = MyApi.ref handle.GrainFactory someKey
```

It returns the same `Scripting.SiloHandle`, so `Scripting.getGrain` and `Scripting.shutdown` work
unchanged against it. It is a separate module from `Scripting` rather than an overload of
`startOnPorts` itself: `Scripting` lives in `Orleans.FSharp`, while `AddFunctionalGrain` lives one
assembly layer above it in `Orleans.FSharp.Runtime`, which references `Orleans.FSharp` and cannot
be referenced back from it -- so `Scripting.startOnPorts` cannot apply a functional definition no
matter how its parameters are shaped. See `samples/quickstart-functional.fsx` for the full runnable script.

## Wire validation diagnostics

The F# binary codec validates every wire-supplied length, element count, union case tag, and
record/POCO arity **against the bytes actually remaining in the stream and the real shape of the
target type**, before allocating or indexing anything. A malformed or truncated payload fails
with a protocol diagnostic naming the stage and the field, rather than an `IndexOutOfRangeException`,
an oversized allocation attempt, or (for a short POCO field count) a silent partial read. The binary
format is not version-tolerant across arity changes, and this validation does not make it so -- it
turns an unhelpful failure into a legible one.

Two more validations that are user-visible if you hit them:

- **Wire text fields are capped.** The grain type and operation ID carried on every request are each
  limited to 512 characters and may not contain a C0 control character (`< 0x20`, which subsumes the
  NUL check). Both are dotted identifiers you choose at the contract; the longest in this repository
  is well under 40 characters, so 512 is headroom, not a practical constraint.
- **Wire-embedded type names are resolved through an allow-list before `Type.GetType` ever runs**,
  matched on whole dotted segments (so `Orleans.FSharpHostile` does not pass an
  `Orleans.FSharp`-prefixed check). An unlisted assembly is rejected with a diagnostic naming it,
  whether it appears as the payload's own qualifier or only inside a generic argument's.

None of this changes any documented public API -- it changes what a malformed or hostile payload
receives back, from an unhelpful low-level exception to a diagnostic that names the stage and the
field.

## Migrating from the grain { } CE

The universal message-passing surface built on the `grain { }` CE -- the builder itself, the
`GrainDefinition`/`GrainContext` types it produces, `FSharpGrainAttribute`,
`AddFSharpGrain(sFromAssembly)`, the `FSharpGrain.*` handle module, `Timers`, and `Reminder` --
is superseded by this functional runtime and now carries `[<Obsolete>]` (warning, not error).

Where the warning fires -- the whole cluster, not just its entry points: on `grain { }` and the
`GrainBuilder` type behind it, on `GrainDefinition` and the old `GrainContext` (types and
modules), on `AdditionalStateSpec`, on `[<FSharpGrain>]`, on `AddFSharpGrain` /
`AddFSharpGrainsFromAssembly`, on the `Timers` and `Reminder` modules, on every operation of the
universal handle module (`FSharpGrain.ref`/`refGuid`/`refInt` and `send`/`post`/`ask` with their
`Guid`/`Int` variants), on the three handle types, on the `IFSharpGrain*` interface aliases, on
the runtime host class `FSharpGrain<'State,'Message>` and `NamedPersistentState`, on the C#
interop helpers for the old cluster (`GrainContext.forCSharp` and the
`Orleans.FSharp.Runtime.GrainDefinition` module behind `additionalState`), and -- from
`Orleans.FSharp.Testing` -- on `TestHarness.getFSharpGrain*` and `GrainMock.withFSharpGrain*`.
So a silo-only, client-only, test-only, or combined process all get the signal at their own
call sites.

Two members of the cluster are deliberately left unattributed, both recorded with a
`// NOT [<Obsolete>]` comment at their declaration in `GrainDiscovery.fs`:
`SimpleGrainState`, because it is `internal` (no consumer can name it, so the attribute would be
invisible where it matters), and `UniversalGrainHandlerRegistry`, the silo-side dispatcher wired
by the already-obsolete `AddFSharpGrain` — a consumer only reaches it after being warned at that
entry point. The prose that hands a reader a recipe naming it directly is
[testing.md](/orleans-fsharp/testing/), "Testing the Universal Grain Pattern" (in this repository and in its
published mirror under `website/src/content/docs/`, which is what the docs site ships), plus the
"Understanding the Universal Grain Pattern" section of `DEVGUIDE.md`; each of those three now sits
under a deprecation banner. `CHANGELOG.md` also names it, in the historical release entry that
introduced it — a changelog records what shipped when and is deliberately left as written.

Inside this repository the library files that must keep naming these symbols (the definitions
themselves, the runtime host, the registries, the test harness) wrap exactly those references in
`#nowarn "44"` ... `#warnon "44"` brackets carrying a `deprecated API self-reference` comment.
That is a self-reference bracket, not
a blanket suppression: nothing outside the bracketed lines is silenced, and no library project
disables FS0044 project-wide.

Old code keeps compiling and running unchanged; every example under `examples/`, the sample
under `src/Orleans.FSharp.Sample`, `testbed/`, and the `orleans-fsharp` template carry a small
functional-runtime twin grain beside the old one so the two authoring styles can be compared
side by side in a real project.

Before/after mapping:

| Old (`grain { }` CE) | New (functional runtime) |
|---|---|
| `grain { defaultState ...; handle ...; persist "Default" }` | `grainFor contract { defaultState (fun () -> ...); handle (_.op) handler; usePersistentState ... }` |
| `GrainDefinition<'State,'Message>` / hand-written grain interface | `grainContract<'Actor,'Key,'Api> () { grainType ...; version ...; <key op> }` defining an `'Api` record of functions |
| `[<FSharpGrain>]` + `AddFSharpGrainsFromAssembly` | `AddFunctionalGrain definition` (no attribute-scan step) |
| `AddFSharpGrain<'State,'Message>(definition)` | `AddFunctionalGrain definition` on the silo builder; `AddFunctionalGrainClient` on a client-only process |
| `FSharpGrain.ref<'State,'Message> factory key` + `FSharpGrain.send/post/ask` | `FunctionalGrain.ref contract factory key`, then call the typed API record's function directly |
| `onTimer "name" dueTime period handler` (in `grain { }`) | `onTimer` operation in `grainFor { }` |
| `onReminder "name" handler` (in `grain { }`) | `onReminder` operation in `grainFor { }` |
| `Timers.register` / `Timers.registerWithState` (class grain) | `Grain.RegisterGrainTimer` directly -- unchanged, this is a class-grain-native Orleans API, not something the functional runtime replaces |
| `Reminder.register` / `.unregister` / `.get` (class grain) | `Grain.RegisterOrUpdateReminder` / `.UnregisterReminder` / `.GetReminder` directly -- likewise class-grain-native |
| `persist "Default"` | `usePersistentState` with a `PersistentState.create<'State> "name" "provider"` descriptor |
| one-way `FSharpGrain.post` | `oneWay (_.op)` in the contract |
| `handleWithContext` / `GrainContext.getService` etc. | the `context` parameter passed to every `handle` callback (`context.services`, `context.grainFactory`, ...) |
| `FSharpObserverManager<'Obs>` held in `grain { }` state, `Subscribe`/`Unsubscribe`/`Notify` message cases | the same `FSharpObserverManager<'Obs>` held in `grainFor` state, with `subscribe: 'Obs -> Task<_>` / `unsubscribe` / a notifying operation on the contract -- unchanged, observers are not part of this deprecation (see below) |
| `onLifecycleStage n hook` (`grain { }`, arbitrary int, `CancellationToken -> Task<unit>`) | `onLifecycle stage hook` (`grainFor { }`, closed `First`/`SetupState`/`Last` set -- `Activate` is rejected, use `onActivate`; hook is `FunctionalGrainContext<'Actor,'Key> -> Task<unit>`) |

`grain { }`'s `onLifecycleStage` operation let a grain hook an *arbitrary* `GrainLifecycleStage`
(`First`/`SetupState`/`Activate`/`Last`/any other int) with a `CancellationToken -> Task<unit>`
callback. `grainFor { }` has `onLifecycle` for the closed set of
documented Orleans stages -- see [Lifecycle-stage hooks](#lifecycle-stage-hooks-onlifecycle)
above for why the hook carries no state at any stage, and for the verified activation ordering. A grain that genuinely needs an *undocumented* numbered stage
(outside `First`/`SetupState`/`Activate`/`Last`) still has no functional-runtime equivalent and
must stay on the `grain { }` CE, or hook the stage on a class grain directly via
`ILifecycleParticipant<IGrainLifecycle>` -- that residual gap is deliberately narrow: Orleans
itself documents only these four stages as stable, and `onLifecycle` already covers three of
them (`Activate` is redundant with `onActivate`).
`InterleaveMessage` has no separate capability gap: it dies with the builder, since the
functional runtime's `alwaysInterleave (_.op)` contract operation covers the same need per
operation rather than per message type.

Two other `grain { }`-adjacent pieces are **not** part of this deprecation and are unaffected:
`GrainRef.fs` (the hand-written-interface style used by `ICounterGrain`, `IOrderGrain`, etc. --
a third authoring style, not superseded by anything here) and `RequestCtx.set/get/getOrDefault/remove`
(same-static wrappers that work unchanged inside functional handlers; `RequestCtx.withValue` has
no functional-runtime equivalent).

### Observers, streams, and the other orthogonal surfaces

Pub/sub **observers are not a capability gap**. `Observer.createRef` / `Observer.deleteRef` /
`Observer.subscribe` and `FSharpObserverManager<'Obs>` are grain-model agnostic -- they need an
`IGrainFactory` and an `IGrainObserver`-derived interface, both of which a functional grain has
(`context.grainFactory`, and any observer interface you already use). An observer reference is
an ordinary contract operation argument: it clears the functional transport's serializer
preflight and round-trips as a live callback target. That is proven end to end, not asserted --
`tests/Orleans.FSharp.Integration/FunctionalObserverIntegrationTests.fs` runs a `grainContract` /
`grainFor` grain on a real TestingHost cluster which subscribes an observer reference, notifies
it, and unsubscribes it.

The one real constraint on that CLASSIC path is Orleans' own and predates all of this: the
**observer interface must be declared in C#**, because Orleans' proxy source generators run over
C# and not F#. That is why `ITestChatObserver` lives in `src/Orleans.FSharp.CodeGen`, and it
applies identically to the `grain { }` CE and to class grains. An example that declares its
observer interface in F# (`examples/chat-room`, `IChatObserver` in `ChatTypes.fs`) cannot use the
classic path at all under either authoring model.

**Functional observers remove that constraint** — see
[Push to clients: functional observers](#push-to-clients-functional-observers) above. The one
C#-declared interface lives inside `Orleans.FSharp.Abstractions`, every application observer of
every brand rides on it, and an observer becomes an ordinary F# handler record. `examples/chat-room`
pushes live through it. Use the classic path when you already have a C#-declared observer
interface and want to keep it; use functional observers otherwise.

### Call filters over a functional grain

A stock `IIncomingGrainCallFilter` sees every functional call, but **not** as the request type the
library uses internally. `FunctionalRequest` is `internal` to `Orleans.FSharp.Abstractions`, so an
application filter cannot write `context.Request :? FunctionalRequest` — that does not compile
outside the library. The supported test is on **argument 0**, which is the public read-only view:

```fsharp
open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp

type FunctionalAuditFilter() =
    interface IIncomingGrainCallFilter with
        member _.Invoke(context: IIncomingGrainCallContext) =
            task {
                match context.Request.GetArgument 0 with
                | :? IFunctionalRequestMetadata as metadata ->
                    // grainType / contractVersion / operationId / readOnly / oneWay /
                    // alwaysInterleave / payload size — everything the envelope carries.
                    if metadata.IsOneWay && metadata.PayloadLength > 65536 then
                        raise (InvalidOperationException $"'{metadata.OperationId}' is too large")
                | _ -> ()   // not a functional call: a system grain, or a CE/class grain

                do! context.Invoke()
            }
            :> Task
```

The `| _ -> ()` arm is load-bearing rather than defensive tidiness: the same filter runs for
Orleans' own system grains, whose argument 0 is something else entirely.

The same "orthogonal, unaffected" verdict covers streaming, broadcast channels, filters,
Kubernetes hosting, logging, shutdown, transactions, event sourcing
(`FSharpEventSourcedGrain*` -- a separate interface family, `IFSharpEventSourcedGrain`, which
shares nothing with the deprecated `IFSharpGrain*` message-passing aliases), versioning,
resilience, batching, `GrainState.fs`, `FSharpBinaryCodec`, and `StateMigration`. None of them
carries `[<Obsolete>]`.

## See also

- [Grain Definition](/orleans-fsharp/grain-definition/) -- the original `grain { }` CE / CodeGen authoring model
- [Silo Configuration](/orleans-fsharp/silo-configuration/) / [Client Configuration](/orleans-fsharp/client-configuration/) --
  `AddFunctionalGrain` / `AddFunctionalGrainClient` sit alongside the CE-based registration shown
  there
- `src/Orleans.FSharp.Sample/ChatRoomFunctional.fs` -- the complete runnable sample this guide's
  examples are drawn from
- [Event Sourcing](/orleans-fsharp/event-sourcing/) -- `journaledGrainFor`, the journaled definition kind
- [Server-Streaming Replies](/orleans-fsharp/streaming-replies/) -- the `IAsyncEnumerable<'Item>` field kind

## A build note for contributors: codegen and cold caches

If you build this repository from a completely clean NuGet cache, run `dotnet build` (or
`dotnet restore`) once before `dotnet test`, and before running any sample directly, on the whole
solution. The functional-runtime package pipeline relies on Orleans' own source-generated codegen
running as part of a normal compile; the very first compile after a clean cache can, in rare
cases, complete without that codegen having been applied to every assembly in the same MSBuild
invocation. A second build (or `dotnet restore` first) is unaffected. CI is not exposed to this --
every job runs `dotnet build` before any `dotnet test` step, exactly to establish a warm,
consistent build state first.
