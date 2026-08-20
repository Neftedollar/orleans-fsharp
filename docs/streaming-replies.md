# Server-Streaming Replies

**Guide to `'Arg -> IAsyncEnumerable<'Item>` — an API field that answers with a sequence
delivered as it is produced, instead of one reply.**

## What you'll learn

- How to declare a streaming operation and bind it with `handleStream`
- What the runtime does, and what Orleans does — because almost all of it is Orleans'
- Why a streaming handler is state-neutral, and what to do instead of publishing state
- Which contract operations compose with a streaming field and which are refused, with the reason
- Cancellation, abandoned enumerators, payload limits, and batching
- What this does **not** give you

---

## Overview

An API record field is normally `'Arg -> Task<'Reply>`. It may also be
`'Arg -> IAsyncEnumerable<'Item>`, which makes the operation **server-streaming**: the handler
produces items over time and the caller receives each one as it is produced.

```fsharp
open System.Collections.Generic
open System.Threading.Tasks
open FSharp.Control          // taskSeq
open Orleans.FSharp

type FeedActor = private FeedActor of unit

type Entry = { at: System.DateTimeOffset; text: string }

[<NoEquality; NoComparison>]
type FeedApi =
    { post: string -> Task<int>
      /// A streaming operation: the field returns IAsyncEnumerable<'Item>.
      tail: int -> IAsyncEnumerable<Entry> }

let feedContract =
    grainContract<FeedActor, string, FeedApi> {
        grainType "chat.feed"
        version 1
        stringKey
    }

let feed = FunctionalGrain.ref feedContract

let feedDefinition =
    grainFor feedContract {
        defaultState (fun () -> [])

        handle (_.post) (fun _ state (text: string) ->
            let entry = { at = System.DateTimeOffset.UtcNow; text = text }
            task { return entry :: state, List.length state + 1 })

        // `handleStream`, not `handle`. The handler returns items only — no replacement state.
        handleStream (_.tail) (fun _ state (count: int) ->
            taskSeq {
                for entry in state |> List.truncate count do
                    yield entry
            })
    }
```

Calling it is ordinary F#. `for … in` over an `IAsyncEnumerable` needs an asynchronous
computation expression, so enumerate inside `task { }` (or `taskSeq { }`) with
`FSharp.Control` opened:

```fsharp
open FSharp.Control          // enumerating an IAsyncEnumerable with `for … in`

task {
    let api = feed factory "general"

    for entry in api.tail 20 do
        printfn "%s" entry.text
}
```

and ordinary C#, because the return type is the BCL interface:

```csharp
await foreach (var entry in api.tail.Invoke(20))
{
    Console.WriteLine(entry.text);
}
```

---

## The mechanism

**Almost none of this is ours.** Orleans has supported `IAsyncEnumerable<T>` grain methods since
7.2, through a grain extension it installs on every activation, and a streaming functional
operation rides exactly that extension:

| Concern | Who does it |
|---|---|
| Starting, continuing and disposing an enumeration | Orleans, `IAsyncEnumerableGrainExtension` (`StartEnumeration` / `MoveNext` / `DisposeAsync`) |
| Keeping the open enumerator on the target | Orleans, `AsyncEnumerableGrainExtension`'s per-activation table |
| Batching items into one reply message | Orleans, `MaxBatchSize` (default 100) |
| Waiting for a slow producer without holding a message | Orleans, a long poll of `ResponseTimeout / 2` answered with a heartbeat |
| Cancelling the producer when the caller disposes | Orleans, `DisposeEnumeratorAsync` |
| Collecting an enumerator the caller abandoned | Orleans, a grain timer at `ResponseTimeout` |
| Which operation, which contract version, which types | this runtime |
| Per-item protocol token and per-item payload limit | this runtime |

The functional side is a third request shape beside the unary and transactional ones:
`FunctionalStreamRequest`, derived from Orleans' public `AsyncEnumerableRequest<T>` exactly as the
transactional request is derived from `TransactionRequest<TResult>`. Its element type is the same
fixed reply the unary path uses, so **every item carries its own protocol token and its own
payload**, and the per-item limit is the ordinary payload limit.

No code generation is involved anywhere: Orleans ships the extension's proxy, invokables and
codecs already compiled inside `Orleans.Core.Abstractions`.

---

## Scheduling: a stream does not block the activation

Every method of `IAsyncEnumerableGrainExtension` carries Orleans' own `[AlwaysInterleave]`, and
`ActivationData.RecordRunning` never makes an always-interleave message the activation's blocking
request. So while an enumeration is open — including while a `MoveNext` is long-polling a slow
producer — **an ordinary, non-reentrant call to the same activation is admitted immediately**.

This is why none of the admission policies apply to a streaming field: the scheduling is fixed by
Orleans, not by us.

---

## State: a streaming handler is state-neutral

A streaming handler receives the state **as it was when the enumeration started** and returns no
replacement:

```fsharp
handleStream (_.tail) (fun context state (count: int) -> taskSeq { ... })
//                                  ^^^^^ read-only snapshot; nothing is published
```

The reason is the scheduling above. A stream produces across many turns of the activation, and
other turns run while it is open. A whole-state replacement published when the sequence ended
would overwrite everything those turns did — silently, and more the longer the stream ran. The
snapshot is the only rule that is coherent with interleaving.

Concretely, inside a streaming handler:

- the returned value is items only — there is no `state', reply` pair;
- persistent-state facades reject the setter and every storage call, exactly as they do for a
  `readOnly` operation;
- a transactional facade is unavailable;
- a journaled definition's streaming handler raises no events.

**Write from an ordinary operation and read from the stream.** If the stream must observe changes
made while it is running, keep the changing data somewhere both can see — a persistent state read
per item, another grain, or an Orleans stream.

---

## What composes, and what is refused

Refused at contract sealing, each for a mechanism rather than a preference:

| Declaration | Why it is refused |
|---|---|
| `readOnly` | It could not have an effect. The scheduling is fixed by Orleans' `[AlwaysInterleave]`, and a streaming handler is already state-neutral. |
| `alwaysInterleave` | Same: every message of an enumeration already carries `[AlwaysInterleave]`. |
| `oneWay` | The stream *is* the reply; there is nothing left to deliver one-way. |
| `transactional` | A transaction is scoped to one call — Orleans reports the participant set back inside that call's response. A stream is many calls whose producer outlives all of them, so there is no response that could carry the participants and no boundary at which the transaction could commit. |
| `statelessWorker` (definition) | An open enumeration lives in one activation's grain extension and every `MoveNext` must reach that activation. A stateless worker routes each message to whichever local worker is free (`StatelessWorkerGrainContext.ReceiveMessageInternal`) on whichever silo the caller reached (`StatelessWorkerDirector`), so the stream would abort mid-enumeration. Use `placement PreferLocal` if the intent was to keep the work near the caller. |

The F# types already make the first four unreachable from the computation expression: they all take
an `OperationSelector`, whose range is `Task<'Reply>`. Sealing repeats the rejection because a
draft can also be built directly.

Composes unchanged:

- `operationId` and `sinceVersion` — both have streaming overloads;
- `acceptsVersions` — a streaming operation gets one protocol-token pair per admitted version,
  exactly like a unary one;
- `placement`, `collectionAge`, `stateFrom`, `usePersistentState`, timers, reminders,
  `onStream`/`onBroadcast`, the C# facade;
- `journaledGrainFor` — a journaled definition may declare streaming operations with the same
  `handleStream`.

---

## Protocol tokens

A streaming operation hashes two directions of its own — `stream-request` and `stream-item` —
instead of `request` and `reply`. That is what makes "the same operation ID at the same version
changed from unary to streaming" a rejected call rather than a silently misrouted one: under
`acceptsVersions (BackwardCompatible n)` a host answers an older caller with **that caller's**
version's tokens, so a shared direction would have matched.

The change is purely additive. Every existing token's preimage is unchanged, so nothing already
deployed computes a different digest.

---

## Cancellation, disposal, and abandoned enumerators

**Disposing the enumerator cancels the producer.** The caller's `DisposeAsync` — which
`await foreach` and F#'s `for … in` both perform — sends `DisposeAsync(requestId)` to the target.
Orleans cancels the token it handed the producer's enumerator and then disposes that enumerator,
so a handler that honours `context.cancellationToken` unblocks and its `finally` blocks run.

```fsharp
handleStream (_.follow) (fun context _ () ->
    taskSeq {
        try
            while true do
                yield! nextBatch ()
                do! Task.Delay(interval, context.cancellationToken)   // the enumeration's token
        finally
            releaseResources ()          // runs when the caller disposes
    })
```

**A caller-side token** works too, through the raw reference:

```fsharp
let reference = FunctionalGrain.rawRef feedContract factory "general"
let stream = reference.streamCancellable (_.tail) 20 cancellationToken
```

Orleans links that token with whatever token `GetAsyncEnumerator` is given, and owns the linked
source.

**A caller that simply walks away** leaves an enumerator on the target. Orleans collects it: the
extension runs a grain timer with `DueTime = Period = MessagingOptions.ResponseTimeout`, clears a
per-enumerator "seen" flag on every tick and removes any enumerator untouched since the previous
one — so it is gone after one to two `ResponseTimeout` periods. A caller that comes back afterwards
gets `EnumerationAbortedException` ("the remote target does not have a record of this enumerator").
Shorten `ResponseTimeout` to collect sooner; there is no separate knob.

---

## Payload limits, per item

`FunctionalGrainTransportOptions.MaxPayloadBytes` applies to **each item**, at both ends and
independently: the silo checks an item before it sends it, and the caller checks it before
deserializing. The diagnostics name the boundary (`silo stream item send`,
`caller stream item receive`), the grain type, the operation and the sizes — never the contents.

---

## Batching

Orleans drains up to `MaxBatchSize` **synchronously available** items into one reply message; the
default is 100. A producer that awaits between items therefore streams one item per message, and
one that does not is batched. To change it for one call:

```fsharp
api.tail 1000 |> FunctionalStream.withBatchSize 10
```

Apply it to the value the API field returned, before enumerating — the batch size is read when an
enumeration starts. Orleans' own `AsyncEnumerableExtensions.WithBatchSize` cannot be used here: it
tests the element type, and a functional stream's element type is your item type while the
underlying request's is the fixed transport reply, so it would silently do nothing.
`FunctionalStream.withBatchSize` fails loudly instead when applied to something that is not a
functional stream.

---

## Authoring a producer

The handler returns the BCL `IAsyncEnumerable<'Item>` and nothing else, so any way of producing one
works. `taskSeq { }` from `FSharp.Control.TaskSeq` is the natural F# tool and this library already
depends on that package, so using it adds nothing to your closure.

> **One caveat, and it is not about this runtime.** In `FSharp.Control.TaskSeq` 0.6.0, the
> *wrapping* combinators — `TaskSeq.map`, and `taskSeq { for x in someStream do … }` — over an
> `IAsyncEnumerable` returned by **this** runtime were measured yielding the last item twice when
> they run under an activation's task scheduler; enumerating the same stream directly is correct.
> So when one grain re-streams another grain's stream, pull the upstream enumerator yourself:
>
> ```fsharp
> handleStream (_.relay) (fun context _ (key: string) ->
>     { new IAsyncEnumerable<Entry> with
>         member _.GetAsyncEnumerator(ct) =
>             let upstream = (feed context.grainFactory key).tail 100
>             upstream.GetAsyncEnumerator ct })
> ```
>
> Producing from a `taskSeq { }` over ordinary data — a list, a range, a database cursor — is not
> affected and is what every example here does.

---

## What this does not give you

- **Client-streaming and bidirectional streaming.** The argument is still one value. Only the
  reply streams.
- **A durable or replayable stream.** An enumeration is a conversation with one activation. If the
  activation is deactivated or the silo is lost, the enumeration ends; there is no cursor to resume
  from and no at-least-once delivery. For durable fan-out use
  [Orleans streams](streaming.md) or implicit subscriptions.
- **Backpressure you control.** The caller pulls, which is backpressure — but the window is
  Orleans' (one in-flight `MoveNext`, up to `MaxBatchSize` items), not a credit scheme of yours.
- **Application call filters over the items.** `IIncomingGrainCallFilter` /
  `IOutgoingGrainCallFilter` see Orleans' extension call, not the functional operation, so a filter
  written against `IFunctionalRequestMetadata` does **not** run for a streaming operation. Put
  cross-cutting checks in the handler, or keep a unary operation as the guarded entry point.
- **State publication.** See "State" above.
- **Ordering across enumerations.** Items within one enumeration are ordered. Two enumerations of
  the same operation are independent and interleave freely.

---

## See also

- [Functional Grain Runtime](functional-grains.md) — the `grainContract` / `grainFor` model
- [Streaming](streaming.md) — Orleans streams, for durable fan-out
- [Calling from C#](calling-from-csharp.md) — `await foreach` over a facade member
- [Event Sourcing](event-sourcing.md) — journaled definitions, which also accept `handleStream`
