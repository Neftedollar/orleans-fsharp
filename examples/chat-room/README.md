# Chat Room

Real-time chat room. The live demo runs a full room (join/leave membership, message posting with
membership validation, a message-history query, a typing indicator) through the functional grain
runtime (`grainContract` + `grainFor` + explicit `stateFrom` persistence). `ChatGrain.fs` keeps the
original `grain {}` + `FSharpObserverManager` push-notification version (with auto-expiring
subscriber lifecycles) as deprecated reference -- see `Program.fs` for why it cannot run standalone
and [docs/functional-grains.md](../../docs/functional-grains.md) for the full migration guide.

**Push notification (observers) stays on the classic path, and here's why.** Observers are on the
KEEP list (not deprecated) and are grain-model agnostic in principle, so `ChatGrainFunctional.fs`
was first written *with* `subscribe`/`unsubscribe` holding `FSharpObserverManager<IChatObserver>`
in state, then actually run to see what happens. `Observer.createRef<IChatObserver>` -- the
client-side call that turns a local object into an Orleans-addressable reference, before either
grain model is even involved -- throws immediately:

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

The live demo's fallback is honest, not silent: `history` is a `readOnly` poll a client calls to see
new messages, replacing push. The old grain (observer-based push, fully intact) remains the
reference implementation for pub/sub.

## How to run

```bash
dotnet run --project src/Silo
```

## Expected output

```
--- Chat Room (Functional Grain Runtime) ---
Note: push notification (observers) hit a C#-codegen wall in this example --
see ChatGrainFunctional.fs's header. history() below is the honest fallback:
clients poll for new messages instead of receiving a push.

--- Chat Room: 2 members joined ---

Alice: Hey everyone! -> Ok 1
Bob: Hi Alice, how's it going? -> Ok 2
Charlie (not a member): Can I join in? -> Error NotAMember
Alice (empty message) -> Error EmptyMessage

Bob left. Bob: Anyone still here? -> Error NotAMember
Members remaining: 1

--- History (poll-based fallback for push notification) ---
  [21:51:12] Bob: Hi Alice, how's it going?
  [21:51:11] Alice: Hey everyone!

Done. Shutting down...
```

(Timestamps will differ per run.)

## Key concepts

- **`grainContract` / `grainFor`** the functional grain runtime's contract + definition pair (this
  example's live path): `join` / `leave` / `say` / `history` / `typing` / `memberCount`
- **`say: string * string -> Task<Result<int, ChatError>>`** membership + non-empty validation
  returning a typed error instead of throwing
- **`readOnly (_.history)` / `readOnly (_.memberCount)`** query operations that never block on the
  write path and interleave with other read-only calls
- **`oneWay (_.typing)` + `alwaysInterleave (_.typing)`** a fire-and-forget indicator that never
  waits for the target and always interleaves
- **`typing: (string * bool) -> Task<unit>`** a multi-input operation: one argument always, with
  the inputs grouped in a tuple -- called as `room.typing ("Bob", true)`
- **`stateFrom` + `PersistentState.create` + explicit `WriteStateAsync`** every membership/message
  change is persisted to the `"Default"` memory storage provider
- **`FSharpObserverManager<T>`** (deprecated-reference path, `ChatGrain.fs`) manages observer
  subscriptions with auto-expiry -- still the reference implementation for push notification here;
  see the note above for why the functional twin polls instead
- **`useJsonFallbackSerialization`** enables clean F# types without serialization attributes

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
