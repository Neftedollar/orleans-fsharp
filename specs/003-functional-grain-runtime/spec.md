# Feature Proposal: First-Class Functional Grains on Stock Orleans

**Proposal**: `003-functional-grain-runtime`  
**Created**: 2026-08-15  
**Status**: Draft for implementation and review  
**Initial compatibility target**: Microsoft Orleans 10.1.0–10.2.2, .NET 10, F# 9+  
**Primary outcome**: remove `FSharpGrainImpl` as the activation target and remove C# from the functional-grain authoring/runtime path without replacing the Orleans runtime.

## Decision summary

Implement functional grains as typed F# descriptions which are hosted by stock
Orleans activations.

- Application authors write F# state, typed operation values, ordinary F#
  handlers, and optional contracts. They do not write or inherit from a grain
  class.
- The actual activation instance is an F# object expression created by a custom
  `IGrainActivator`. It implements one internal functional target contract and
  the Orleans capability interfaces required by the definition.
- A hidden F# marker type remains in the Orleans manifest. It is metadata only
  and is never instantiated. This is required because the stock Orleans silo
  still maps `GrainType` to a concrete CLR `Type`.
- A typed `Operation<'Actor,'Argument,'Reply>` binds an argument and reply type
  to one actor protocol. The caller cannot choose a result type as it can with
  the current `ask<'Result>` API.
- An optional contract gives stable actor identity, protocol version, operation
  IDs, key encoding, and Orleans policies. A small deployment-local grain can
  omit an explicit contract.
- A build-time F# tool emits the required unique markers/manifest glue and, for
  contracted grains, a bound plain F# `Api` record plus transport registrations.
  Its source output is F# (`.g.fs`), not a C# project or user-visible C# API.
- Orleans continues to own routing, activation, the scheduler, placement,
  collection, lifecycle, persistence providers, timers, reminders, streaming,
  filters, migration, and transactions.
- Standard `task { }` is the baseline asynchronous API. This proposal does not
  add `grainTask`, `grainFlow`, `become`, `stay`, or a mailbox receive loop.

The accurate claim is **first-class functional F# API over stock Orleans**, not
“classless Orleans”. F# object expressions and hidden marker types are still CLR
objects/types after compilation.

## Why this proposal exists

The current universal path is easy to start with, but it has structural
limitations:

1. `FSharpGrain.ref<'State,'Message>` ultimately asks Orleans for the same
   universal `IFSharpGrain` type. Orleans cannot see the functional definition
   identity in the grain type. Two different definitions using the same key can
   therefore address the same activation.
2. `ask<'Result>` lets the caller select an unrelated response type. The mistake
   compiles and fails later through a cast.
3. The universal `FSharpGrainImpl` has one physical method and one class-level
   metadata set. Per-operation `ReadOnly`, `OneWay`, `AlwaysInterleave`, method
   policies, per-actor placement, and versioning do not naturally fit it.
4. The zero-stub universal path does not currently offer reliable parity for
   durable state, lifecycle capabilities, reminders, timers, transactions, and
   other Orleans facets. The C# CodeGen path recovers some features at the cost
   of returning to C# classes and methods.
5. The current generalized F# serialization/copying path must not treat every
   F# record or union as deeply immutable. A record can contain `byte[]`, a ref
   cell, or another mutable object; Orleans call isolation requires a real deep
   copy.

The new design fixes the identity and type-safety problems first, then maps
logical operation and actor policies onto the corresponding Orleans mechanisms.

## Goals

### G1. F#-only application and runtime surface

No user-authored grain class, C# interface, C# implementation project, or C#
source generator is required. The new runtime, transport bridge, contract model,
generator, examples, and tests are written in F#.

### G2. Typed operation calls

Argument, reply, key, and actor protocol types are inferred from values. A caller
must not provide generic result type arguments or cast a reply.

### G3. Reuse Orleans instead of emulating it

The implementation must retain stock `ActivationData`/`IGrainContext` and the
Orleans turn scheduler. It must not build another mailbox, actor directory,
placement system, or lifecycle engine.

### G4. Orleans capability coverage

The contract/definition model must have an explicit mapping for scheduling
options, persistence, lifecycle, timers, reminders, streams, filters,
placement, collection, versioning, cancellation, migration, and transactions.
Where exact native method metadata is impossible with one dispatch method, the
gap must be documented and an optional generated F# fidelity path defined.

### G5. Stable distributed protocol

Actor type IDs, operation IDs, versions, and schema fingerprints are explicit
protocol data. They must not silently change because an F# module, CLR type, or
record field is renamed.

### G6. Coexistence

Functional and ordinary Orleans grains must run in the same silo and call each
other. Rich C# ergonomics for calling a functional grain are optional; a
generated C# facade is not required by this proposal.

## Non-goals for the first implementation

- No MailboxProcessor/Akkling-style receive-loop API.
- No separate workflow/composition runtime or durable workflow semantics.
  Ordinary `task { }` and F# functions/pipelines are enough for the first
  release. A later `grainFlow`-like layer may be built entirely on the typed
  call API.
- No `become`/`stay`/transition effect algebra in the baseline handler API.
- No attempt to remove every CLR `Type`. Stock Orleans manifest construction
  requires one.
- No hot addition of arbitrary actor types after the silo manifest has been
  built.
- No exactly-once delivery claim.
- No automatic public C# method facade.
- Journaled grains/log-consistency parity is a second-stage workstream described
  later in this proposal, not an acceptance gate for the first functional
  runtime.

## Terminology

| Term | Meaning |
|---|---|
| Functional grain | An Orleans virtual actor whose behavior is described by F# values/functions instead of a user grain class. |
| Definition | Server-side value containing initial state, typed handlers, persistence, lifecycle, timers, reminders, and other capabilities. |
| Contract | Optional stable shared value containing actor identity, key codec, protocol version, operations, and invocation/actor policies. It is an Orleans.FSharp concept, not an Orleans requirement. |
| Operation witness | Opaque `Operation<'Actor,'Argument,'Reply>` value tying an operation to one actor, argument, reply, stable ID, codecs, and policies. |
| API record | Generated ordinary F# record of functions bound to one actor key. |
| Marker | Hidden concrete F# type added to the Orleans manifest. It is never the activation instance. |
| Target | F# object expression returned by the activator and invoked by Orleans. |
| Envelope | Internal serializable request carrying stable actor/operation/schema metadata and encoded data. |

## Proposed public F# API

The following code is the target public experience. Exact custom-operation names
may change only where required by the F# compiler; the type-safety and visibility
rules are normative.

### Core handler type

The baseline handler uses the standard .NET task builder:

```fsharp
type Handler<'Actor, 'Key, 'State, 'Argument, 'Reply> =
    FunctionalGrainContext<'Actor, 'Key> ->
    'State ->
    'Argument ->
    Task<'State * 'Reply>
```

The first result is the state after the turn and the second is the typed reply.
An operation which does not replace state returns the original state. No custom
execution computation expression is required. `FunctionalGrainContext<'Actor,
'Key>` exposes the target-local cancellation token, a `'Key` value,
`IGrainFactory`, services,
logger, time provider, RequestContext access, and the registered Orleans
capabilities, avoiding a matrix of `handleWithContextCancellable` overloads.

### Contractless simple grain

An internal helper or prototype can omit a public contract:

```fsharp
let echo =
    grain {
        initialState (fun (_key: string) -> 0)

        handle (fun _context count (text: string) ->
            task {
                let next = count + 1
                return next, $"#{next}: {text}"
            })
    }

services.AddFunctionalGrain echo

let echo42 = echo.ref grainFactory "42"
let! reply = echo42.call "hello"
```

The inferred API is equivalent to:

```fsharp
type SimpleApi<'Argument, 'Reply> =
    { call : 'Argument -> Task<'Reply> }
```

“Contractless” means that the author did not supply a durable public protocol.
The pre-compilation F# tool still emits a unique private actor tag/closed marker,
internal contract descriptor, actor type ID, and operation for that source
binding. A generic marker closed only over state/argument/reply types is not
unique enough: two same-shaped definitions must still receive different CLR
marker types and GrainTypes. Its identity may be derived from assembly/source
binding metadata and is therefore not safe for rolling upgrades or a long-lived
cross-assembly protocol.

Contractless mode is available only when this minimal marker generation has run;
it is not a dynamic runtime-only escape hatch. Emit a diagnostic when it is used
with statically visible persistence/versioning features, and let production
hosts opt into `RequireExplicitContracts` to reject all contractless definitions.

The simple mode intentionally has one argument/reply pair. The argument can be a
discriminated union and the handler can pattern match on it. If different cases
need different reply types or Orleans policies, use a contracted grain with
multiple operations.

### Stable contracted grain

Operation witnesses are authored in the shared contract assembly. A phantom
`'Actor` type prevents operations belonging to different actors from being
mixed, even when their argument and reply types happen to be identical.

```fsharp
[<Struct>]
type UserId = private UserId of string

module UserId =
    let create value = UserId value
    let value (UserId value) = value

[<Struct>]
type RoomId = private RoomId of string

module RoomId =
    let create value = RoomId value
    let value (RoomId value) = value

type UserProfile =
    { id : UserId
      displayName : string }

type PostMessage =
    { author : UserId
      text : string }

type ChatMessage =
    { author : UserId
      text : string
      sentAt : DateTimeOffset }

type HistoryRequest = { take : int }

type PostError =
    | NotAMember
    | EmptyText
```

The actor marker below is a phantom protocol type, not a grain implementation and
is never instantiated:

```fsharp
type UserActor = private UserActor of unit

module User =
    let rename : Operation<UserActor, string, unit> =
        operation "rename"

    let profile : Operation<UserActor, unit, UserProfile> =
        operation "profile"

    let contract =
        grainContract<UserActor, UserId> {
            grainType "chat.user"
            version 1
            stringKey UserId.value UserId.create

            operation rename
            operation profile

            readOnly profile
            collectionAge (TimeSpan.FromMinutes 30)
        }
```

`version 1` is the application protocol/interface version for `chat.user`. It is
not “version 1 of Orleans.FSharp”, and it is not part of the CLR type name.

The `Operation<_,_,_>` annotations are the shared protocol signatures, written
once; the server handler and generated client bind to them, so there is no
second C#/F# interface declaration and no result type at the call site.

The room has four independently typed operations:

```fsharp
type RoomActor = private RoomActor of unit

type Typing =
    { user : UserId
      isTyping : bool }

module Room =
    let join : Operation<RoomActor, UserId, unit> =
        operation "join"

    let say : Operation<RoomActor, PostMessage, Result<int64, PostError>> =
        operation "say"

    let history : Operation<RoomActor, HistoryRequest, ChatMessage list> =
        operation "history"

    let typing : Operation<RoomActor, Typing, unit> =
        operation "typing"

    let contract =
        grainContract<RoomActor, RoomId> {
            grainType "chat.room"
            version 1
            stringKey RoomId.value RoomId.create

            operation join
            operation say
            operation history
            operation typing

            readOnly history
            oneWay typing
            alwaysInterleave typing
            placement Placement.Default
            collectionAge (TimeSpan.FromMinutes 30)
        }
```

The contract centralizes durable wire identity and Orleans policies. It is still
optional for simple grains. Operation IDs are stable values and may be given an
alias when a source identifier is renamed. A build compatibility check must
reject duplicate IDs or an argument/reply schema change under an unchanged
operation ID and protocol version.

### Server definitions

The server binds typed witnesses directly to typed handlers. There is no
`(fun api -> api.join)` selector and no free result type:

```fsharp
type UserState =
    { displayName : string }

let userDefinition =
    grainFor User.contract {
        defaultState { displayName = "" }
        persist "Default"

        handle User.rename (fun _context state displayName ->
            task {
                let next =
                    { state with
                        displayName = displayName.Trim() }

                return next, ()
            })

        handle User.profile (fun context state () ->
            task {
                let profile =
                    { id = context.key
                      displayName = state.displayName }

                return state, profile
            })

        onActivate (fun context ->
            task {
                context.logger.LogInformation("User activated")
            })

        onDeactivate (fun context reason ->
            task {
                context.logger.LogInformation(
                    "User deactivated: {Reason}", reason)
            })
    }
```

```fsharp
type RoomState =
    { nextMessageId : int64
      members : Set<UserId>
      messages : ChatMessage list }

let roomDefinition =
    grainFor Room.contract {
        defaultState
            { nextMessageId = 1L
              members = Set.empty
              messages = [] }

        persist "Default"

        handle Room.join (fun _context state userId ->
            task {
                let next =
                    { state with
                        members = Set.add userId state.members }

                return next, ()
            })

        handle Room.say (fun context state post ->
            task {
                if not (Set.contains post.author state.members) then
                    return state, Error NotAMember
                elif String.IsNullOrWhiteSpace post.text then
                    return state, Error EmptyText
                else
                    let message =
                        { author = post.author
                          text = post.text
                          sentAt = context.utcNow }

                    let messageId = state.nextMessageId

                    let next =
                        { state with
                            nextMessageId = messageId + 1L
                            messages = message :: state.messages }

                    return next, Ok messageId
            })

        handle Room.history (fun _context state request ->
            task {
                let result =
                    state.messages
                    |> List.truncate (max 0 request.take)
                    |> List.rev

                return state, result
            })

        handle Room.typing (fun context state typing ->
            task {
                context.logger.LogDebug(
                    "{User} typing={IsTyping}",
                    typing.user,
                    typing.isTyping)

                return state, ()
            })

        onReminder "trim-history" (fun _context state _tick ->
            task {
                return
                    { state with
                        messages = List.truncate 1_000 state.messages }
            })
    }
```

The exact hook signatures must be documented in the implementation API.
Lifecycle and reminder handlers are ordinary functions returning `Task`. They do
not introduce an alternate actor loop.

### Generated bound API

From the contract, the F# build tool emits opaque operation/transport
registrations, a plain record of functions, and a generated binding extension,
conceptually:

```fsharp
type UserApi =
    { rename : string -> Task<unit>
      profile : unit -> Task<UserProfile> }

type RoomApi =
    { join : UserId -> Task<unit>
      say : PostMessage -> Task<Result<int64, PostError>>
      history : HistoryRequest -> Task<ChatMessage list>
      typing : Typing -> Task<unit> }

module UserApi =
    val ref : IGrainFactory -> UserId -> UserApi

module RoomApi =
    val ref : IGrainFactory -> RoomId -> RoomApi
```

F# modules are not partial across files, so the generator must not pretend to
append `Room.ref` or `Room.Api` to the authored `Room` module. The generated
`RoomApi` record and its same-named companion module live together in generated
F# source, so the normative call is `RoomApi.ref client roomId`. This uses an
ordinary, compile-proven F# type/module pattern and does not require a specialized
extension on `GrainContract<_,_>`. The public ergonomic requirements are:

- no user-written binder record;
- no selector lambda;
- no call-site generic arguments;
- no caller-selected reply type;
- no visible Orleans `GrainReference` subclass;
- no generated or checked-in C# project.

The mandatory source-generator feasibility spike must prove this companion
layout and compile ordering. Falling back to `GetGrain<T>`, selector lambdas,
or `ask<'Result>` is not acceptable.

| Owned by | Written/generated artifacts |
|---|---|
| Application/contract author | Domain types, optional phantom actor token, typed operation witnesses, stable contract/policies, state, handlers, and hooks. |
| F# build tool | Bound `Api` record/ref binder, hidden manifest marker, reference/request/invokable glue, registrations, compatibility manifest, and generated codecs where selected. |
| Orleans | Grain identity routing, proxy invocation pipeline, activation context, scheduler, placement, collection, lifecycle orchestration, storage providers, reminders, streams, filters, migration, and transactions. |

The author does not hand-write the `ref` binding or the bound record. The
operation values are the shared protocol declarations; the handlers are their
server-side implementations.

### Minimal application code

Hosting configuration is intentionally omitted. `client` is an
`IGrainFactory` (an `IClusterClient` also provides grain-factory behavior):

```fsharp
let runChat (client: IGrainFactory) =
    task {
        let alice =
            UserApi.ref client (UserId.create "alice")

        let lobby =
            RoomApi.ref client (RoomId.create "general")

        do! alice.rename "Alice"
        let! profile = alice.profile ()

        do! lobby.join profile.id

        let! posted =
            lobby.say
                { author = profile.id
                  text = "Hello from F#" }

        match posted with
        | Error error ->
            printfn "Post rejected: %A" error
        | Ok messageId ->
            let! recent = lobby.history { take = 20 }
            printfn "Posted #%d" messageId

            for message in recent do
                printfn "%A: %s" message.author message.text
    }
```

`User` and `Room` above are modules organizing protocol values. `UserApi` and
`RoomApi` are generated F# record types. `alice` and `lobby` are records of
functions closed over an Orleans reference and a typed actor key. Variables such
as `source` and `target` in transfer examples are only different keys/references;
they are not hidden framework concepts.

The compiler infers every return type from the record field:

- `alice.profile ()` is `Task<UserProfile>`;
- `lobby.say value` is `Task<Result<int64,PostError>>`;
- `lobby.history value` is `Task<ChatMessage list>`.

It is impossible to ask `profile` for an `int` or invoke `Room.join` on a User
reference without an explicit unsafe escape hatch.

### Grain-to-grain calls and future composition

The same generated record is usable inside a handler:

```fsharp
handle Transfer.move (fun context state request ->
    task {
        let source =
            AccountApi.ref context.grains request.source

        let target =
            AccountApi.ref context.grains request.target

        let! withdrawn = source.withdraw request.amount

        match withdrawn with
        | Error error ->
            return state, Error error
        | Ok transfer ->
            do! target.deposit transfer
            return state, Ok transfer.id
    })
```

This already composes as a task and through ordinary F# functions/pipelines.
A future computation expression or workflow package can be implemented on top
of these functions without changing the transport. Durable orchestration,
automatic compensation, retries, and exactly-once semantics are separate design
problems and are not implied by chaining tasks.

## Operation model: no query/command split

There is one public operation kind:

```fsharp
Operation<'Actor, 'Argument, 'Reply>
```

A mutation can return `unit`, an acknowledgement, a domain error, or a value. A
logically read-shaped operation can update a cache or access metadata and can
therefore remain an ordinary operation. Return type and naming cannot reliably
classify “query” versus “command”.

`readOnly` is optional Orleans scheduling metadata on an operation. It maps to
`InvokeMethodOptions.ReadOnly`, allowing Orleans to admit read-only calls using
its normal scheduler rules. In this F# API, the dispatcher additionally ignores
the handler's returned replacement state and skips its automatic state write.
This preserves one public handler shape without pretending to prove purity. It
still cannot prevent mutation inside a mutable object graph, external effects,
or direct use of lower-level storage services. It is not snapshot isolation,
automatic retry, caching, or replica routing.

`oneWay` is also explicit metadata. It must never be inferred from
`Task<unit>`:

- normal `Task<unit>` means an acknowledged request; target exceptions can reach
  the caller;
- `oneWay` means no target result or failure acknowledgement.

The generated one-way field can still return `Task<unit>` for uniform F#
composition, but that task only represents local send initiation. Documentation
and analyzers must make this distinction prominent.

Pattern matching remains fully available inside any operation argument:

```fsharp
type ModerationRequest =
    | Mute of UserId * TimeSpan
    | Unmute of UserId
    | Ban of UserId * reason: string
```

One operation over this DU shares one reply type and one Orleans policy set. Use
separate operations when cases require different reply types, read-only/one-way
semantics, transaction options, timeouts, or interleaving rules.

## Contract semantics

An explicit contract is recommended whenever the grain is persisted, called
from another assembly/service, deployed in a heterogeneous cluster, or expected
to survive source renames.

A contract contains:

- stable grain type ID;
- stable closed actor protocol identity;
- key kind and typed key encoder/decoder;
- protocol/interface version and compatibility policy;
- stable operation IDs;
- argument/reply schema fingerprints and codecs;
- per-operation invocation policies;
- actor scheduling/placement/directory/collection defaults;
- optional compatibility aliases for controlled renames.

Implementation-only state and hooks remain in the server definition. Provider
names and deployment-specific placement may be set in the definition/host and
override contract defaults under an explicit policy.

Contract validation occurs before the silo or client finishes building:

1. reject duplicate actor and operation IDs;
2. reject two definitions for one actor ID on a silo;
3. reject an operation whose handler argument/reply type differs from its
   witness;
4. reject unsupported policy combinations;
5. when a prior compatibility manifest is supplied, reject a stable operation
   schema change without a version/compatibility rule; without a baseline,
   report that rolling compatibility was not verified;
6. advertise only definitions actually installed on that silo.

The definition registry becomes immutable after host build. Stock Orleans does
not support hot-adding arbitrary new grain types to the active manifest.

## Runtime architecture

### Keep the stock activation context

Do not implement a custom `IGrainContext`. A replacement context would need to
rebuild the scheduler, receive path, lifecycle, collection, migration, and
services which this proposal is intended to retain.

For every registered actor contract:

1. register registry-backed `IGrainTypeProvider` and
   `IGrainInterfaceTypeProvider` implementations which map generated closed CLR
   marker/target types to the contract's stable IDs;
2. register a closed hidden marker type in `GrainTypeOptions.Classes`;
3. publish implemented-interface, version/default-grain-type, placement,
   concurrency, directory, and capability metadata through
   `IGrainPropertiesProvider`/`IGrainInterfacePropertiesProvider`;
4. register the matching reference activator/provider using those stable IDs;
5. install a custom activator through `IConfigureGrainTypeComponents`;
6. let Orleans create its normal `ActivationData`/`IGrainContext`;
7. return an F# object expression from `IGrainActivator.CreateInstance`.

Do not rely on Orleans' default CLR/generic type-name IDs for a durable
contract. The frozen registry is the source of truth for CLR Type ↔ stable
`GrainType`/`GrainInterfaceType` mapping on both client and silo.

The target interface inherits `IGrain`. The F# build tool emits a distinct
**non-generic concrete marker type per actor contract**. The marker implements
that actor's closed target interface so Orleans' normal interface-to-grain
manifest mapping sees it; its body is unreachable because the custom activator
never constructs it:

```fsharp
type internal ChatUserMarker() =
    interface IFunctionalGrainTarget<UserActor> with
        member _.Dispatch(_envelope, _cancellationToken) =
            raise (InvalidOperationException "Metadata-only marker")
```

An alternative can keep an `IGrain`-only marker only if it supplies and tests
the complete custom interface-to-grain manifest/reference mapping. The actual
object expression implements the same closed target interface which the
request/invokable advertises.

Do not declare a concrete open generic marker implementing `IGrain`: Orleans
SDK/type discovery could advertise that definition independently of the frozen
registry. Shared helper bases must be abstract or not implement a grain
interface, and generated registration must ensure that only installed
non-generic per-actor markers enter `GrainTypeOptions.Classes`. A single open
marker would also make a heterogeneous silo appear capable of activating
definitions it does not have.

### Why the object expression works

In Orleans 10.2.2:

- `ActivationDataActivatorProvider` requires the **mapped marker type** to be
  assignable to `IGrain`;
- `IGrainActivator.CreateInstance` returns `object`;
- `ActivationData.SetGrainInstance` checks state/non-null but does not require
  the returned object to be an instance of the mapped marker;
- invocation sets the target on an `IInvokable` and dispatches through the
  implemented target contract.

Therefore the actual object can be an F# object expression which implements the
functional target contract, `IGrainBase`, and capability interfaces. It need not
inherit from `Grain` or from `FSharpGrainImpl`.

This behavior must be covered by integration tests for every supported Orleans
minor/major version. It is a deliberate low-level integration seam, not an
excuse to depend on unrelated Orleans internals.

### Activation target

The activator constructs state facets and capability adapters, then returns an
object expression equivalent to:

```fsharp
{ new IFunctionalGrainTarget<'Actor> with
    member _.Dispatch(envelope, cancellationToken) =
        dispatcher.invoke(envelope, cancellationToken)

  interface IGrainBase with
    member _.GrainContext = context

    member _.OnActivateAsync cancellationToken =
        lifecycle.activate cancellationToken

    member _.OnDeactivateAsync(reason, cancellationToken) =
        lifecycle.deactivate reason cancellationToken

  interface IRemindable with
    member _.ReceiveReminder(name, tick) =
        reminders.dispatch(name, tick)

  interface IIncomingGrainCallFilter with
    member _.Invoke(callContext) =
        filters.invoke(callContext) }
```

The implementation may use a small set of precompiled F# target shapes instead
of advertising every optional interface on every actor. It must avoid a
combinatorial type explosion and keep marker/manifest capabilities consistent
with the actual target.

`DisposeInstance` releases activation-local resources. Durable state facets must
be created early enough to participate in the Orleans setup-state lifecycle.

### Typed dispatch

The internal wire target has one logical method:

```fsharp
IFunctionalGrainTarget<'Actor>.Dispatch :
    RequestEnvelope * CancellationToken ->
        ValueTask<ReplyEnvelope>
```

The request contains serializable data only:

- stable actor type ID;
- stable operation ID;
- protocol version and schema fingerprint;
- request flags selected from the trusted registered descriptor;
- encoded argument bytes;
- optional idempotency/correlation metadata.

No F# function, closure, `FSharpFunc`, delegate, `AsyncReplyChannel`, or local
continuation crosses the network.

The dispatcher:

1. validates actor, operation, version, schema, and payload limits;
2. resolves the server-side typed operation descriptor;
3. decodes the argument;
4. invokes the typed handler under the Orleans turn;
5. commits/publishes the returned state according to persistence policy, except
   that an explicitly read-only operation discards the returned replacement
   state and performs no automatic state write;
6. encodes the typed reply or returns a stable protocol error.

Scheduling and one-way headers are chosen by the trusted generated/precompiled
client request family before the call reaches the activation. Raw request
constructors remain internal, and client and silo contract registries are a
trusted protocol pair. The server still validates actor/operation/schema
consistency in `Dispatch`, but that validation occurs after scheduling
admission: it cannot undo a forged `ReadOnly`/`AlwaysInterleave` header or turn
a forged one-way call back into an acknowledged call.

### Reference and invokable bridge

The first implementation uses one handwritten/precompiled F# reference and a
small family of F# requests/invokables. All of them dispatch to the same target:

- ordinary acknowledged request;
- read-only acknowledged request;
- one-way/other invocation-option variants as required;
- transactional request;
- transactional read-only request.

The transaction variants derive from the Orleans transaction request base so
that ambient transaction context is actually propagated. Merely tagging a
plain request as “transactional” is not sufficient. `RequestBase.Options` is
not serialized; a transactional read-only request must restore the `ReadOnly`
option on the target (for example during deserialization/`SetTarget`) before the
transaction base decides whether a newly created transaction is read-only.

Requests must implement normal Orleans cancellation behavior: target-local
`CancellationTokenSource` setup, `GetCancellationToken`, `TryCancel`, and
`IsCancellable`. A `CancellationToken` is not serialized inside the payload.

The reference/request/codec provider registrations are available on clients and
silos. The bridge must use public Orleans extension points where available and
be pinned by compatibility tests where the APIs are version-sensitive.

### State and persistence

For ordinary durable state, create `IPersistentState<'State>` through
`IPersistentStateFactory` using the Orleans activation context. This keeps
provider selection, ETags, setup ordering, migration participation, and state
loading in Orleans.

Required semantics:

- state is loaded before application `onActivate`;
- an acknowledged update is not replied to as durable until its configured
  write has completed;
- a failed or ambiguous write does not silently publish a successful reply;
- named/multiple states remain possible;
- `readOnly` does not auto-save, auto-retry, or prove no mutation;
- provider exceptions and unknown outcomes follow documented Orleans/provider
  semantics.

The initial implementation can persist after every successful updating
operation. A later explicit write/effect policy may optimize this, but it must
not alter the typed call contract.

### Serialization and copying

The transport envelope uses stable primitive fields and `byte[]` payloads.
Argument/reply/state codecs are registered at build time where possible.

Correctness requirements:

- remote and local calls have equivalent copy isolation;
- F# records, unions, lists, maps, and sets containing mutable values are deeply
  copied;
- no generalized copier returns an arbitrary F# value “as-is” solely because
  its outer type is a record/union;
- persisted and wire schemas have rolling-version tests;
- duplicate/unknown field and union-case behavior is defined;
- generated codecs are preferred to reflection for trimming/NativeAOT;
- reflection fallback is explicit, diagnosable, and documented.

## Orleans capability mapping

Legend:

- **Native**: stock Orleans mechanism remains in control.
- **F# adapter**: stock semantics reached through a bounded F# bridge.
- **Generated F#**: exact parity needs hidden `.g.fs` per-contract/per-operation
  artifacts.
- **Deferred**: separate phase; do not claim parity in the first release.

| Capability | Level | Required mapping and boundary |
|---|---|---|
| Activation, directory, mailbox, turn scheduler | Native + F# adapter | Closed manifest marker and custom activator; retain stock `ActivationData`/`IGrainContext`. Interleaving still alternates single-threaded turns; it is not parallel state access. |
| Lifecycle | Native | Actual object expression implements `IGrainBase`; components subscribe to `IGrainContext.ObservableLifecycle`. Definition-level `onLifecycleStage` registers at explicit Orleans lifecycle stages. State facets are initialized before application activation hooks. |
| Ordinary persistence | Native + F# adapter | Create `IPersistentState<'S>` through `IPersistentStateFactory`, support named providers/states, await writes according to the definition policy. Orleans does not auto-save arbitrary returned values. |
| Timers | Native + F# wrapper | Register stock grain timers through `IGrainBase`/timer registry and expose `Interleave`/`KeepAlive` options. Timers are activation-local and are recreated after activation. |
| Reminders | Native + F# adapter | Target/manifest advertise `IRemindable` and route stable reminder names to functions through the stock reminder registry. Reject stateless-worker + reminder combinations. |
| Explicit streams | Native + F# wrapper | Use `GetStreamProvider` and typed wrappers around `IAsyncStream<'T>` and subscription handles. Delivery guarantees remain provider-specific. |
| Implicit stream subscriptions | F# adapter, Phase 4 | Publish Orleans stream-binding properties and implement `IStreamSubscriptionObserver`. Require provider-specific multi-silo integration tests before claiming parity. |
| Broadcast channels | Native + F# wrapper | Resolve the configured Orleans broadcast channel provider through activation/client services and wrap producer/subscription lifetime in typed F# functions. Provider delivery/lifetime semantics remain unchanged. |
| Cancellation | F# adapter | Implement the Orleans invokable cancellation contract and pass the target-local token to handlers. Cancellation is cooperative and cannot roll back committed state/external effects. |
| Per-operation response timeout | Native client behavior via F# request | Store the timeout in the operation descriptor and return it from the invokable's `GetDefaultResponseTimeout`. A per-call cancellation/deadline can be layered on top, but a timeout still has an unknown execution outcome. |
| Response streaming (`IAsyncEnumerable`) | F# adapter or generated F#, Phase 4 | An ordinary byte-array reply envelope cannot model Orleans response-streaming flow control by itself. Add a universal adapter over Orleans' public async-enumerable request shape or generate the matching request/reference shape; until backpressure, cancellation, and early-disposal tests pass, direct callers to Orleans streams. |
| RequestContext | Native | Standard Orleans message path imports/exports it. Values remain small and serializable; it is not durable actor state. |
| Key kinds and compound keys | Native identity + F# codec | Support Orleans string, GUID, integer, and compound key forms through typed codecs. Actor GrainType remains separate from key so equal keys across contracts never collide. |
| DI and grain/runtime services | Native | Expose activation services, `IGrainFactory`, logging, time, storage/stream/reminder registries, and registered grain services through typed `FunctionalGrainContext<'Actor,'Key>`; do not serialize service instances. |
| Global call filters | Native | Calls pass through normal reference/runtime filter pipelines. Expose logical operation metadata from the envelope. |
| Grain-level filters | F# adapter | Object expression implements `IIncomingGrainCallFilter`. A functional call context exposes actor/operation metadata. |
| Per-operation CLR MethodInfo/attributes | Generated F# | Universal dispatch reports one physical `Dispatch` method. Logical policies/telemetry work through descriptors; exact attribute/reflection parity requires per-operation generated interface/request/invokable/target glue. |
| `ReadOnly` | Native scheduling via F# request + F# state rule | Add `InvokeMethodOptions.ReadOnly` per registered operation. Orleans treats it as scheduling metadata only. The F# dispatcher also discards the returned replacement state/skips automatic persistence, but cannot prevent in-place mutation or direct side effects. Never mark universal `Dispatch` itself read-only. |
| `OneWay` | Native transport via F# request | Add `InvokeMethodOptions.OneWay` only for an explicitly configured operation. No target reply, exception, or durability acknowledgement reaches the caller. Never infer it from `unit`. |
| `AlwaysInterleave` | Native scheduling via F# request | Add `InvokeMethodOptions.AlwaysInterleave`. Advanced opt-in: it can interleave with writes and all request kinds. |
| `Unordered` | Native client-routing optimization via F# request | The attribute is obsolete/no-effect in Orleans 10.x, but `InvokeMethodOptions.Unordered` remains a live low-level option used for client gateway selection. Expose it only as an advanced explicit policy and promise no ordering guarantee either way. |
| `MayInterleave` | Generated F# marker + native scheduling | Put `[<MayInterleave("CanInterleave")>]` on the mapped hidden marker and generate a public static callback accepting `IInvokable` and inspecting logical operation ID. Do not use an instance predicate: the actual target is a different object. Orleans' component types are internal, so a custom component path is version-pinned, not the default. Do not emulate read-only by `MayInterleave(isQuery)` because its admission semantics can permit read/write interleaving. |
| `Reentrant` | Native via grain properties | Publish the Orleans reentrant grain property or equivalent type component. Document mutable-state hazards after every await. |
| Call-chain reentrancy | Native + F# scope wrapper | Expose `RequestContext.AllowCallChainReentrancy()` as a safely disposable/scoped helper for grain-to-grain call cycles. It is a call-site choice, not actor-level `Reentrant` metadata. |
| Stateless worker | Native placement, restricted model | Publish `StatelessWorkerPlacement` properties/max workers. Reject durable per-key state, reminders, and migration combinations. Multiple activations can serve the same GrainId. |
| Placement | Native | Convert contract/host policy to stock grain properties and registered `PlacementStrategy`. Custom strategies still require their Orleans DI registrations. |
| Directory, immovable, collection age, explicit deactivate | Native | Publish corresponding grain properties; expose `DeactivateOnIdle`/`MigrateOnIdle` through the functional context. Deactivation is an intent, not synchronous destruction. |
| Interface/grain versioning | Native at actor level, F# protocol at operation level | Register stable closed grain/interface IDs and actor version. Orleans sees one dispatch signature, so the registry validates operation additions/removals and schemas and returns `UnsupportedOperation` for incompatible calls. Exact per-method native metadata uses generated F#. |
| Heterogeneous silos | Native + registry validation | Each silo advertises only installed closed actor definitions and versions. Mixed-version tests cover routing and unsupported operations. |
| Transactions | F# adapter, Phase 4/high maintenance | Use `TransactionRequestBase` variants, transaction options, and `ITransactionalStateFactory`. A plain universal request is never transactional. A read-only variant starts a read-only transaction only when it creates one; joining an ambient read-write transaction does not downgrade it, so F# operation policy must still forbid writes. Immutable F# state may require an internal mutable `class,new()` box. |
| Migration | Native + F# adapter | `IPersistentState` already participates. Ephemeral state uses a migration participant and stable codecs. Recreate timers/resources after activation; closures/resources are not migrated. |
| Grain extensions/observers | F# adapter | Generate/register the required transport interfaces or explicit adapters. Test client disconnect, extension installation, serialization, and lifetime semantics. |
| Tracing/metrics | Native pipeline + F# naming | Return logical actor/operation activity names and tags. Do not put actor keys in metric labels; keys may be PII/high-cardinality and require explicit trace policy/redaction. |
| Coexistence with ordinary grains | Native | Both models share silos/providers. Functional handlers can call normal Orleans references. C# callers may use the low-level bridge; an idiomatic C# facade is optional generation. |
| Journaled grains/log consistency | Deferred second stage | Preserve the existing `eventSourcedGrain` API while investigating a functional log-consistency adapter. See the dedicated section below. |

## Invalid policy combinations

Fail host build with actionable diagnostics for at least:

- stateless worker + persistent per-key state;
- stateless worker + reminder;
- stateless worker + migration;
- one-way operation with non-`unit` reply;
- one-way + transactional operation (transaction propagation requires a
  response carrying updated transaction information);
- duplicate grain/operation IDs;
- unsupported transaction option/request family;
- implicit stream binding without required provider/capability registration;
- definition installed without its contract/codecs;
- incompatible schema under the same protocol version without a declared
  compatibility rule.

## Native method fidelity mode

The universal `Dispatch` path is the recommended default because it keeps the
runtime small and the public API functional. It cannot create a distinct CLR
`MethodInfo` or physical attribute for every logical operation.

An optional F# fidelity generator may emit, per contract:

- an internal F# transport interface with one method per operation;
- F# `GrainReference` and `IInvokable` implementations;
- serializers/copiers and registration metadata;
- an object-expression target shape implementing that generated interface;
- per-operation transaction/cancellation/options glue;
- optional C#-callable facade only as a separate opt-in product.

All generated artifacts remain implementation details and `.g.fs` source. This
mode is required only for features/tools which fundamentally inspect CLR methods
or attributes. Business logic still lives in the same typed F# handlers.

## JournaledGrain and log consistency: second stage

The repository already has an `eventSourcedGrain { }` API and currently
generates a C# `JournaledGrain` bridge. This proposal must not silently delete
or regress that capability.

It is not part of the first functional-runtime acceptance gate because Orleans
`JournaledGrain<'State,'Event>` is an inheritance-oriented abstraction with
protected behavior, lifecycle participation, and provider metadata. An F#
object expression can derive from a non-sealed CLR base, so this is not a
language impossibility; it still creates a compiler-generated derived CLR type
whose lifecycle/provider/reflection behavior must be verified. The second-stage
design must evaluate two paths:

1. compose the lower-level Orleans log-consistency provider/protocol machinery
   behind the functional target; or
2. use an F# object-expression adapter or generate a hidden F# subclass while
   keeping the public API entirely functional.

The investigation must choose based on correctness and Orleans upgrade cost,
not on avoiding a small hidden type at the expense of reimplementing the log
consistency protocol.

The stock log-consistency path also passes the actual derived CLR type name into
log-view-adaptor/storage setup. An anonymous object-expression subclass has a
compiler-generated name which is not a stable durable identifier and can change
between builds. Therefore a named hidden generated F# subclass with a stable CLR
name is the safer default for a `JournaledGrain`-based path. An anonymous
object-expression path is acceptable only after an integration proof that its
selected provider does not persist or otherwise depend on that unstable name.

Second-stage acceptance must cover:

- pure `apply : State -> Event -> State` and typed per-operation decision
  handlers;
- raise/confirm semantics and provider selection;
- replay/recovery after deactivation and silo restart;
- version/state/event schema evolution;
- concurrent proposals/conflict resolution as defined by Orleans;
- lifecycle, migration, snapshots, and heterogeneous deployment;
- preservation or an explicit migration path for the current
  `Orleans.FSharp.EventSourcing` package and tests;
- eventual removal of generated C# from this path if the chosen architecture
  supports equivalent semantics in F#.

Until this work is complete, documentation must describe JournaledGrain parity
as deferred, not as provided by ordinary `IPersistentState`.

## Generated F# tooling

The current `Orleans.FSharp.Generator` is a post-compilation assembly reader
which emits C# event-sourcing stubs. It does not establish pre-`Fsc` `.g.fs`
generation, F# source ordering, IDE design-time builds, or F# codec generation.
Those capabilities are new work, not reusable facts to assume.

The phase-zero spike must choose and prove one of these models:

1. a pre-`Fsc` F# Compiler Service/MSBuild task parses designated, statically
   analyzable contract declarations and emits `.g.fs` before type checking; or
2. a two-pass/project model compiles the shared contract assembly first, then a
   tool reads its typed metadata and emits F# bindings into client/server
   consumer projects.

Arbitrary runtime-computed contract values cannot be treated as generator input.
The generatable contract subset must use top-level typed operation bindings,
literal/statically discoverable stable IDs and versions, and policy forms the
tool can analyze. Dynamic composition remains possible through a lower-level
runtime API but does not receive generated bound records or build-time protocol
compatibility guarantees. Roslyn cannot directly emit F# source.

Generated output:

- bound API record and reference binder;
- contract/operation registration;
- stable manifest grain/interface metadata;
- required reference/invokable/request families;
- codec/copier registrations;
- optional per-operation fidelity artifacts;
- deterministic current compatibility manifest used to compare protocol
  changes.

Compatibility validation requires a previous baseline. Define a checked-in or
CI-supplied contract manifest (for example
`orleans-fsharp.contracts.baseline.json`) and a command which compares the
current generated manifest against that baseline or a released package/tag.
Without a baseline, the tool can detect duplicates/current-build inconsistency
but must not claim rolling-schema compatibility.

Tooling requirements:

- incremental and deterministic output;
- correct F# compile ordering and IDE design-time builds;
- no need to check generated files into source control;
- diagnostics point to the authored contract/operation;
- no public generated names based solely on unstable compiler-generated names;
- generator output is inspectable for debugging;
- trimming annotations or generated alternatives for reflection paths;
- tests on clean clone, package consumption, and both supported Orleans
  versions.

The public API section is normative. A phase-zero generator spike must prove the
documented generated `RoomApi.ref` companion-module form and source ordering
before implementing the rest of the generated surface.

## Migration from the current API

### Compatibility period

Introduce the new API alongside the current `FSharpGrain` module:

- mark `ask<'Result>` as legacy once typed operations ship;
- provide an adapter from a one-operation `GrainDefinition<'State,'Message>` to
  contractless simple mode where identity is unambiguous;
- emit warnings when two legacy definitions can share the same universal
  GrainId/key;
- keep old packages available for one deprecation window;
- do not make the new runtime activate `FSharpGrainImpl`.

### Target end state

- `FSharpGrainImpl` is absent from the public API and never used as an
  activation target.
- The functional runtime/transport projects contain no C# source.
- The C# `Orleans.FSharp.Abstractions` shim and non-event-sourced C# per-grain
  generation are removed or isolated in an explicitly named legacy
  compatibility package, then deleted in the final functional-runtime migration
  milestone.
- The existing C# event-sourcing/Journaled bridge remains in its legacy package
  until the separate JournaledGrain workstream supplies a tested F# replacement
  and migration path; the core release must neither regress nor falsely claim to
  replace it.
- Documentation and examples use typed contracts/operations and generated F#
  API records.
- Functional actor type is part of `GrainId.Type` so equal keys in different
  contracts never collide.

## Delivery guarantees and error model

The proposal inherits Orleans delivery semantics:

- timeouts and lost replies can leave the caller uncertain whether an operation
  executed;
- one-way calls provide even less feedback;
- ordinary persistence and transactions do not automatically deduplicate a
  command whose reply was lost;
- cancellation is cooperative;
- retries of mutating operations require an explicit idempotency/deduplication
  policy.

The protocol defines stable failures for unknown actor, unknown operation,
unsupported protocol version, schema mismatch, decode failure, and payload
limits. Application exceptions follow the configured Orleans exception
serialization policy. Domain failures should normally be typed replies such as
`Result<'T,'Error>`.

## Security and observability

- Raw envelopes and request constructors are internal.
- The generated/precompiled client derives pre-admission scheduling/one-way
  headers from its frozen registry; the silo validates operation metadata
  against its own frozen registry after admission. This is consistency checking,
  not a server-side ability to undo forged headers.
- Payload sizes/depths are limited before allocation-heavy decode.
- Authorization filters receive logical actor/operation metadata.
- Trace defaults include actor type, operation ID/name, and protocol version.
- Actor keys are added to traces only under an explicit redaction policy and
  never used as default metric labels.
- Serializer fallback cannot instantiate arbitrary unapproved runtime types.

## Implementation plan

### Phase 0: feasibility gates

Before broad API work, implement focused tests/prototypes:

1. a generated non-generic per-actor F# marker is registered in the stock
   Orleans manifest, with no concrete open generic marker discovered;
2. a custom activator returns an F# object expression not assignable to the
   marker;
3. a handwritten F# request/invokable reaches the target through normal
   `ActivationData`;
4. lifecycle, a persisted state facet, and a global filter execute;
5. a functional and normal grain coexist in a two-silo test;
6. the build emits/consumes a minimal `.g.fs` bound API with reliable IDE/build
   ordering;
7. the same tests pass on Orleans 10.1.0 and 10.2.2.

If a supported Orleans version closes the activation seam, stop and document the
minimal upstream hook/fork required. Do not silently replace `IGrainContext`.

### Phase 1: identity, contract, and transport core

- Implement stable actor/key/operation identifiers and frozen registry.
- Fix actor identity collision by using a distinct GrainType per closed actor
  contract.
- Implement `Operation<'Actor,'Argument,'Reply>` and typed handler registry.
- Implement F# reference/request/invokable/envelope/codecs.
- Implement acknowledged calls, protocol errors, cancellation, and local/remote
  deep-copy equivalence.
- Add contractless one-operation mode only after the minimal pre-compilation
  tool emits a unique private actor tag/marker for each definition.

### Phase 2: public DSL and generator

- Implement `grainContract`, keep `grain { }` for simple mode, and introduce a
  compile-proven contracted builder expression such as
  `grainFor Room.contract { }` without colliding with the current auto-open
  `grain` builder value.
- Generate bound F# API records and binders.
- Add compatibility manifest and analyzers.
- Port the chat sample and normal grain-to-grain calls.
- Ensure all sample/application projects are F#-only.

### Phase 3: core Orleans capabilities

- Ordinary/named persistence and lifecycle.
- ReadOnly, OneWay, AlwaysInterleave, Unordered, MayInterleave, Reentrant.
- Placement, directory, collection, stateless worker validation.
- Timers, reminders, explicit streams, broadcast channels, and explicit
  lifecycle-stage hooks.
- Filters, RequestContext, tracing, activation migration.

### Phase 4: advanced parity

- Transaction request families and transactional state.
- Implicit stream subscriptions and response streaming.
- Grain extensions/observers.
- Optional per-operation native fidelity generation.
- Heterogeneous rolling-upgrade matrix.

### Phase 5: compatibility removal

- Migrate documentation/examples.
- Deprecate unsafe `ask<'Result>` and legacy universal references.
- Remove `FSharpGrainImpl` and C# runtime/code-generation projects used by the
  non-event-sourced functional path. Retain the legacy Journaled/event-sourcing
  bridge until its separate migration acceptance gate passes.
- Complete package/API compatibility notes and a major-version release plan.

### Separate second-stage workstream

Design and implement JournaledGrain/log-consistency parity using the acceptance
criteria above. It may proceed after the core activation/transport architecture
is stable.

## Required tests

### Compile-time API tests

- Wrong argument type does not compile.
- Wrong reply type cannot be selected.
- An operation for Actor A cannot be called through Actor B.
- `FunctionalGrainContext<'Actor,'Key>.key` cannot be read as an unrelated key
  type.
- `RoomApi.ref` infers and returns `RoomApi` from the typed key.
- Invalid contract/policy combinations produce source-located diagnostics.

### Identity and routing

- Two actor contracts with the same string/GUID/integer/compound key create
  distinct activations and state.
- Two same-shaped contractless definitions receive distinct generated actor
  tags, marker types, GrainTypes, activations, and state.
- Only silos with a definition advertise/support its GrainType.
- Auto-discovery never advertises a concrete open universal functional marker.
- Mixed actor versions route according to configured Orleans version policy.
- Unsupported operation/version returns a deterministic protocol failure.

### Activation and lifecycle

- The actual `GrainInstance` is the F# target object, not
  `FSharpGrainImpl`/marker.
- OnActivate occurs after persistent state load.
- OnDeactivate/disposal execute on collection, shutdown, and failure paths
  supported by Orleans.
- Definition hooks registered at explicit Orleans lifecycle stages execute in
  the expected order around state setup and application activation.
- Rehydration/migration order is correct.

### Persistence

- State survives deactivation, silo restart, and activation on another silo.
- Named multiple states use the correct providers.
- ETag conflict/write failure does not produce a false successful reply.
- Read-only operation does not publish/persist a replacement state.

### Scheduling

- Serialized default, ReadOnly read/read admission, AlwaysInterleave, static
  MayInterleave, Reentrant, and OneWay are tested against stock Orleans
  behavior. `InvokeMethodOptions.Unordered` is tested only for its documented
  client-routing optimization; no ordering guarantee is asserted.
- A client/registry policy mismatch is rejected by dispatch, while tests also
  demonstrate that a forged pre-admission header cannot be retroactively undone
  by the target.
- MayInterleave(readOnly-operation) is not used as a substitute for ReadOnly.
- Stateful-invalid stateless worker combinations are rejected.

### Runtime services

- Timers are activation-local; reminders survive deactivation.
- Explicit streams and broadcast channels work across silos; implicit streams
  have provider-specific tests before release.
- Cancellation reaches target handlers and does not claim rollback.
- Global/grain-level filters and RequestContext see logical operation metadata.
- Normal grains and functional grains call each other.

### Serialization and compatibility

- Local and remote calls isolate nested mutable arrays/ref cells/objects.
- Contract schemas are compatible across supported rolling versions.
- CI compatibility verification compares the current manifest with an explicit
  released/checked-in baseline; without one it reports compatibility as
  unverified instead of passing vacuously.
- Trimming publish test avoids unrooted reflection paths where generated codecs
  are expected.
- Malformed/oversized envelopes fail safely.

### Transactions

- Create, Join, CreateOrJoin, Supported, NotAllowed, and Suppress options match
  Orleans behavior where exposed.
- Transaction context crosses functional calls.
- A transaction created by the read-only request family is read-only; joining an
  ambient read-write transaction remains read-write, while the F# operation
  policy still prevents state publication/automatic writes.
- One-way + transactional operation is rejected at build time.
- Timeout/unknown-outcome tests do not claim automatic deduplication.

## Core functional-runtime definition of done (Phases 0–3)

The first supported functional-runtime release is complete when:

1. a user can build and run the chat sample with only F# application projects;
2. no application grain class/interface implementation is authored;
3. the activation target is an F# object expression and `FSharpGrainImpl` is not
   used;
4. typed API records make wrong argument/reply/actor combinations compile-time
   errors;
5. distinct contracts sharing the same key cannot collide;
6. the Phase 1–3 feature matrix is covered by multi-silo integration tests;
7. persistence survives deactivation/restart and follows provider failure
   semantics;
8. the new transport uses correct copying, cancellation, filters, and protocol
   validation;
9. generated build artifacts are F#, deterministic, and work in IDE/CI/package
   consumption;
10. Phase 4 gaps (transactions, implicit streams, native method fidelity,
    JournaledGrain) are labeled experimental/deferred until their own acceptance
    suites pass.

Completing Phase 4 removes the corresponding deferred labels only after its
transaction, streaming, extension, fidelity, and rolling-upgrade suites pass.
The separate JournaledGrain workstream retains its own acceptance gate.

## Hard boundaries and implementation warnings

1. **No C# is feasible; no CLR type is not.** The hidden marker and compiled
   object expression remain CLR types.
2. **Do not instantiate a delegate as the target.** A direct `Func` or
   `FSharpFunc` is technically dispatchable but loses `IGrainBase` lifecycle,
   reminder/filter interfaces, and normal target contract mapping. The object
   expression is the compatibility membrane.
3. **Do not replace Orleans activation context.** That becomes a runtime rewrite.
4. **Do not advertise one open universal marker everywhere.** Register closed
   installed actor types.
5. **Do not expose raw client flags.** The trusted generated request family
   chooses scheduling/one-way headers from the client registry before target
   admission. The silo registry validates consistency after admission but
   cannot undo a forged header; client and silo are a trusted protocol pair.
6. **Do not infer wire identity from CLR names.**
7. **Do not treat F# outer shape as deep immutability.**
8. **Do not equate ReadOnly with isolation or OneWay with reliable delivery.**
9. **Do not claim all Orleans features through one Dispatch without naming the
   MethodInfo/attribute gap.**
10. **Pin and test Orleans versions.** Manual reference/request APIs are public
    seams but are close to the transport.

## Primary implementation references

- Orleans activator contract:
  [`IGrainActivator`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Runtime/Activation/IGrainActivator.cs)
- Default marker/class-map activation requirement:
  [`ActivationDataActivatorProvider`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Runtime/Activation/ActivationDataActivatorProvider.cs)
- Grain-type component configuration/custom activator hook:
  [`IGrainContextActivator.cs`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Runtime/Activation/IGrainContextActivator.cs)
- Activation instance, lifecycle, scheduling, and migration:
  [`ActivationData`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Runtime/Catalog/ActivationData.cs)
- Manifest class/interface inputs:
  [`GrainTypeOptions`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Core/Configuration/GrainTypeOptions.cs)
- Stable CLR type → GrainType provider seam:
  [`IGrainTypeProvider`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Core.Abstractions/Manifest/IGrainTypeProvider.cs)
- Stable interface ID provider seam:
  [`IGrainInterfaceTypeProvider`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Core.Abstractions/IDs/GrainInterfaceType.cs)
- Interface version/default-grain-type properties:
  [`IGrainInterfacePropertiesProvider`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Core.Abstractions/Manifest/GrainInterfaceProperties.cs)
- Grain base lifecycle surface:
  [`IGrainBase`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Core.Abstractions/Core/IGrainBase.cs)
- Reference/request options and cancellation base:
  [`GrainReference`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Core.Abstractions/Runtime/GrainReference.cs)
- Invokable cancellation, logical naming, and default response timeout:
  [`IInvokable`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Serialization/Invocation/IInvokable.cs)
- Invocation option definitions:
  [`InvokeMethodOptions`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Core.Abstractions/CodeGeneration/InvokeMethodOptions.cs)
- Target assignment/incoming invocation path:
  [`InsideRuntimeClient`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Runtime/Core/InsideRuntimeClient.cs)
- Generated invokable target-cast behavior used as a design reference:
  [`InvokableGenerator`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.CodeGenerator/InvokableGenerator.cs)
- Persistence setup/factory:
  [`PersistentStateStorageFactory`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Runtime/Facet/Persistent/PersistentStateStorageFactory.cs)
- Journaled-grain base:
  [`JournaledGrain`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.EventSourcing/JournaledGrain.cs)
- Log-view-adaptor setup and derived CLR type-name use:
  [`LogConsistentGrain`](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.EventSourcing/LogConsistency/LogConsistentGrain.cs)
- Orleans request scheduling:
  [official scheduling documentation](https://learn.microsoft.com/en-us/dotnet/orleans/grains/request-scheduling)
- Orleans delivery guarantees:
  [official delivery-guarantee documentation](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/messaging-delivery-guarantees)
- Orleans code generation:
  [official code-generation documentation](https://github.com/dotnet/orleans/blob/main/docs/site/src/content/docs/grains/code-generation.md)
- Current F# event-sourcing API:
  [`docs/event-sourcing.md`](../../docs/event-sourcing.md)

## Review questions for the implementation PR

The reviewer should explicitly answer:

1. Is any `FSharpGrainImpl` or C# grain class still instantiated on the new path?
2. Is the actual target compatible with lifecycle, filters, reminders, and
   cancellation?
3. Can two contracts with the same key collide?
4. Can the caller choose a wrong reply type?
5. Are pre-admission operation flags sourced from the trusted generated client
   registry, and does review avoid claiming that target validation can undo
   them?
6. Is persistent state loaded/committed at the correct lifecycle points?
7. Does local invocation preserve the same deep-copy isolation as remote
   invocation?
8. Are logical operation names visible without high-cardinality/PII metric
   labels?
9. Are native MethodInfo/attribute limitations documented or covered by
   generated F# fidelity artifacts?
10. Are all supported Orleans versions exercised in integration tests?
11. Are JournaledGrain/log-consistency claims kept separate until their dedicated
    suite passes?
