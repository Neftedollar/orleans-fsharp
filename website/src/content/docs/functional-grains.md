---
title: "Functional Grain Runtime"
description: "A second, complete authoring model: user-authored API records instead of C# CodeGen interfaces."
---

# Functional Grain Runtime

**A second, complete authoring model: user-authored API records instead of C# CodeGen interfaces.**

## What you'll learn

- Why the actor brand type exists, and why it is never constructed
- Key-codec identity rules: what changes a grain's routing/storage identity, what does not
- Operation rename via `operationId`, and the exact contract-version matching rule
- The persistence model: explicit writes only, unique state names, and multi-provider non-atomicity
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
example (contract, definition, registration, and a driven call sequence), which is the same
source spec 003's "Public authoring model" section shows.

## Two spellings, one operation

An API field may be written curried, and when it is, it means exactly the same operation as the
tupled spelling:

```fsharp
{ typing: UserId -> bool -> Task<unit> }        // curried spelling
{ typing: (UserId * bool) -> Task<unit> }       // canonical spelling
```

Contract construction walks the field's function chain to the first `Task<_>`, collects the
argument types in order, and **canonicalizes** two or more of them into the F# reference tuple.
That tuple *is* the operation's wire argument type, so the two records above produce the same
operation ID, the same protocol token, and byte-identical payloads for the same values. A field
can move between the spellings in either direction without a wire change, which is why they are
interchangeable across versions of one application.

What differs is only how you write the call and the handler:

```fsharp
// caller: curried application, no tuple to build
do! room.typing userId true

// silo: the handler always takes the canonical tuple, and the `handle` spelling carries the arity
handle2 (_.typing) (fun context state (user, isTyping) ->
    task {
        context.logger.LogDebug("{User} typing={IsTyping}", user, isTyping)
        return state, ()
    })
```

`handle2` … `handle7` bind a curried field of that arity; plain `handle` binds the tupled
spelling. The arity is in the operation *name* rather than in an overload of `handle` for a
concrete reason: F# type-checks a lambda argument of an **overloaded** method without the expected
type, so an overloaded `handle` would stop the argument type flowing from the selector into the
handler body — `fun context state post -> post.author` would stop inferring and every handler in
your codebase would need an annotation. One operation per arity keeps that inference exact.

Everything else is unchanged. `readOnly`, `oneWay`, `alwaysInterleave`, and `operationId` each
have one spelling per arity and apply to a curried field exactly as to a tupled one. The bound
field is a preclosed curried closure, so the hot path stays reflection-free and a partial
application (`room.typing userId`) sends nothing — the request goes out at the last argument.

**The limits.** At most **seven** curried arguments: seven is where `System.Tuple` stops nesting,
so the canonical tuple stays flat and identical to the type you would have written by hand. An
eighth fails contract construction with a diagnostic telling you to group the inputs in a record.
`unit` means "no domain input" only as a field's **sole** argument (`unit -> Task<'Reply>`, which
is never canonicalized to a one-tuple); in any later curried position it is rejected, because
there it would silently become an ordinary tuple slot that reads like an absent argument. And
because the walk consumes the whole chain to `Task<_>`, an API field can never *return* a
function: a trailing function type is simply another argument — and, being unserializable, one
that fails the serializer preflight at binding.

`FunctionalGrainRef.call` and `callCancellable` take the canonical tupled spelling only. They are
curried members, and F# forbids overloading a member that takes curried arguments (FS0816), so
there is no curried form of them: call a curried field through the bound API record, and spell an
operation tupled if you need the raw cancellable call on it.

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

That is why a definition which attaches `stateFrom`, `usePersistentState`, or declares
`onReminder` requires its contract to carry an **explicit** `grainType` -- enforced both when the
definition seals and again, redundantly, at silo registration. Ephemeral definitions (none of the
three) have nothing durable to orphan, so they may rely on the derived default:

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

**Contract version matching is exact, with no rolling-upgrade tolerance.** Every request carries
the caller's contract version; the target compares it against its own hosted version with `=`,
not `>=` or a compatibility range. A version mismatch fails the call before any handler runs, with
no negotiation and no automatic fallback. This means:

- a version bump is a **breaking wire change** for every caller still on the old version -- there
  is no in-place, mixed-version rolling deployment for a single contract version bump;
- if you need callers on two versions to coexist during a rollout, host **two contracts** (one
  per version, e.g. two different `grainType` strings, or two definitions selected by
  deployment), migrate traffic explicitly, then retire the old one -- the runtime gives you no
  automatic compatibility bridge to lean on instead.

Contract version is independent of `GrainId`, storage identity, and the fixed Orleans interface
version (which this transport family pins to `1` internally, regardless of your contract's
`version`).

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

### Resolving services from a handler

Every callback -- handler, hook, timer, or reminder -- receives the same
`FunctionalGrainContext<'Actor, 'Key>`, so dependency injection is one member away:
`context.services : IServiceProvider`. Resolve anything registered on the silo exactly as you would
in ASP.NET Core:

```fsharp
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

**Cancellation is cooperative and never rolls anything back.** `callCancellable` on an
acknowledged call propagates a target-local `CancellationToken` that a long-running handler may
observe (e.g. inside a `Task.Delay` or another cancellable await) -- but cancelling it does not
undo any explicit write or external effect the handler already performed, and does not stop the
handler from running to completion if it doesn't check the token. For a one-way
`callCancellable`, an *already-cancelled* token short-circuits to a cancelled `Task` locally, but
once sent, a later cancellation has no remote effect at all -- the delivered one-way context
always uses `CancellationToken.None`.

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
its own):

```fsharp
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

The spec's point-free binding --

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
`call`, and `callCancellable`) instead of the bare API record.

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

## Wire validation diagnostics (stricter since the fix-wave hardening pass)

The F# binary codec now validates every wire-supplied length, element count, union case tag, and
record/POCO arity **against the bytes actually remaining in the stream and the real shape of the
target type**, before allocating or indexing anything. A malformed or truncated payload now fails
with a protocol diagnostic naming the stage and the field, instead of an `IndexOutOfRangeException`,
an oversized allocation attempt, or (for a short POCO field count) a silent partial read. The binary
format was never version-tolerant across arity changes -- this closes a failure mode, it does not add
one; nothing that decoded correctly before is affected.

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

## Migrating from the grain { } CE (Task 8 deprecation pass)

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
`#nowarn "44"` ... `#warnon "44"` brackets carrying the comment
`deprecated API self-reference (spec-003 deprecation pass)`. That is a self-reference bracket, not
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

**Capability gap -- no migration path today:** `grain { }`'s `onLifecycleStage` operation let a
grain hook an *arbitrary* `GrainLifecycleStage` (`First`/`SetupState`/`Activate`/`Last`/any other
int) with a `CancellationToken -> Task<unit>` callback. `grainFor { }` has `onActivate` /
`onDeactivate` (fixed points in the lifecycle) but no equivalent for hooking an arbitrary
numbered stage. A grain that genuinely needs a specific lifecycle stage (not just "on
activate"/"on deactivate") has no functional-runtime equivalent yet and must stay on the
`grain { }` CE, or hook the stage on a class grain directly via `ILifecycleParticipant<IGrainLifecycle>`.
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

The one real constraint is Orleans' own and predates all of this: the **observer interface must
be declared in C#**, because Orleans' proxy source generators run over C# and not F#. That is
why `ITestChatObserver` lives in `src/Orleans.FSharp.CodeGen`, and it applies identically to the
`grain { }` CE and to class grains. An example that declares its observer interface in F#
(`examples/chat-room`, `IChatObserver` in `ChatTypes.fs`) is subject to that constraint under
both authoring models, which is why the chat-room functional twin covers only the
message-posting slice of the domain.

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
- `specs/003-functional-grain-runtime/spec.md` -- the full normative specification

## A build note for contributors: codegen and cold caches

If you build this repository from a completely clean NuGet cache, run `dotnet build` (or
`dotnet restore`) once before `dotnet test`, and before running any sample directly, on the whole
solution. The functional-runtime package pipeline relies on Orleans' own source-generated codegen
running as part of a normal compile; the very first compile after a clean cache can, in rare
cases, complete without that codegen having been applied to every assembly in the same MSBuild
invocation. A second build (or `dotnet restore` first) is unaffected. CI is not exposed to this --
every job runs `dotnet build` before any `dotnet test` step, exactly to establish a warm,
consistent build state first.
