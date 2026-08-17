# Chat Room

Real-time chat room. The live demo runs a full room (join/leave membership, message posting with
membership validation, a message-history query, a typing indicator) through the functional grain
runtime (`grainContract` + `grainFor` + explicit `stateFrom` persistence). `ChatGrain.fs` keeps the
original `grain {}` + `FSharpObserverManager` push-notification version (with auto-expiring
subscriber lifecycles) as deprecated reference -- see `Program.fs` for why it cannot run standalone
and [docs/functional-grains.md](../../docs/functional-grains.md) for the full migration guide.

**Push notification is live, through functional observers.** The room pushes every message and
every join/leave to its subscribers, and the subscriber is an ordinary F# handler record --
`RoomObserverApi` in `ChatGrainFunctional.fs` -- with no observer interface and no code generation
anywhere in this project. The run transcript below shows the `[push]` lines arriving while the
calls are being made, and shows them stopping after `unsubscribe`.

**The CLASSIC observer path is still walled here, and the wall is worth keeping on record**,
because it is exactly what functional observers route around. Observers are on the KEEP list (not
deprecated) and are grain-model agnostic in principle, so `ChatGrainFunctional.fs` was first
written *with* `subscribe`/`unsubscribe` holding `FSharpObserverManager<IChatObserver>` in state,
then actually run to see what happens. `Observer.createRef<IChatObserver>` -- the client-side call
that turns a local object into an Orleans-addressable reference, before either grain model is even
involved -- throws immediately:

```
System.InvalidOperationException: Unable to find an IGrainReferenceActivatorProvider for grain type sys.client
```

`IChatObserver` is declared in F# (`ChatTypes.fs`) and this example has no C# CodeGen project to
generate its reference-activator/proxy, so the observer reference itself cannot be constructed --
independent of which grain it would be passed to. `Orleans.FSharp.BroadcastChannel` (also KEEP-list)
was checked as a second candidate before writing any code for it: `docs/streaming.md` states that
broadcast-channel *consumers* are grains implementing `IOnBroadcastChannelSubscribed` with
`[ImplicitChannelSubscription]`, "handled by the C# CodeGen" -- the identical wall, one hop later.
Neither is a functional-runtime gap; both are this example's total absence of a C# CodeGen project.

**What closes it.** A functional observer needs no application interface at all: the single
C#-declared interface lives inside `Orleans.FSharp.Abstractions`, where Orleans' proxy generator
has already run over it, and every application observer of every brand rides on that one interface.
So `RoomObserverApi` is just a record, `FunctionalObserver.create` returns a serializable typed
handle, and the handle is an ordinary operation argument. `history` stays in the demo as what it
always should have been -- an ordinary `readOnly` paged query, not a substitute for push. The old
grain (classic observer push, fully intact) remains the reference for the deprecated model.

## How to run

```bash
dotnet run --project src/Silo
```

## Expected output

```
--- Chat Room (Functional Grain Runtime) ---
Push notification is LIVE below, through functional observers: no observer
interface and no code generation in this project. The CLASSIC observer path is
still walled here -- see ChatGrainFunctional.fs's header for that wall.

--- Chat Room: 2 members joined ---

Alice: Hey everyone! -> Ok 1
Bob: Hi Alice, how's it going? -> Ok 2
Charlie (not a member): Can I join in? -> Error NotAMember
Alice (empty message) -> Error EmptyMessage

Bob left. Bob: Anyone still here? -> Error NotAMember
Members remaining: 1

--- History (an ordinary readOnly paged query) ---
  [21:51:12] Bob: Hi Alice, how's it going?
  [21:51:11] Alice: Hey everyone!

Done. Shutting down...
```

(Timestamps will differ per run.)

## Key concepts

- **`grainContract` / `grainFor`** the functional grain runtime's contract + definition pair (this
  example's live path): `join` / `leave` / `say` / `history` / `typing` / `memberCount` /
  `subscribe` / `unsubscribe`
- **`say: string * string -> Task<Result<int, ChatError>>`** membership + non-empty validation
  returning a typed error instead of throwing
- **`readOnly (_.history)` / `readOnly (_.memberCount)`** query operations that never block on the
  write path and interleave with other read-only calls
- **`oneWay (_.typing)` + `alwaysInterleave (_.typing)`** a fire-and-forget indicator that never
  waits for the target and always interleaves
- **`typing: (string * bool) -> Task<unit>`** a multi-input operation: one argument always, with
  the inputs grouped in a tuple -- called as `room.typing ("Bob", true)`
- **`observerContract` + `FunctionalObserver.create` + `FunctionalObserverManager`** live push to a
  client-hosted handler record, with a liveness window and no code generation
- **`defaultState` + `usePersistentState` + explicit `WriteStateAsync`** the handler's state is the
  live per-activation shape (durable data *plus* the subscriber set), while every membership and
  message change is persisted to the `"Default"` memory storage provider through a named holder.
  The subscriber set is deliberately outside the persisted record: it holds live object references,
  and the F# codec refuses a state type carrying one rather than writing something that only looks
  restorable
- **`FSharpObserverManager<T>`** (deprecated-reference path, `ChatGrain.fs`) the classic observer
  manager, kept as the reference implementation for the deprecated model -- see the note above for
  why it cannot run in this project and what replaces it
- **`useJsonFallbackSerialization`** enables clean F# types without serialization attributes

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
