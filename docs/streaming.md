# Streaming

**Guide to Orleans streaming with F#-idiomatic APIs.**

> **Current API.** This guide uses functional definitions for subscriptions. Examples for the original authoring model are retained in [Legacy Streaming](legacy/streaming.md).

## What you'll learn

- How to publish events to streams
- How to subscribe item-by-item, in batches, with server-side filtering, or as pull-based TaskSeq
- How terminal error/completion callbacks follow the selected Orleans provider
- How to use broadcast channels for fan-out
- How to rewind and resume stream consumption
- Implicit stream and broadcast subscriptions (`onStream` / `onBroadcast`)

## Overview

Orleans.FSharp wraps Orleans streams with typed `StreamRef<'T>` references and functional APIs in the `Stream` module. Broadcast channels get their own `BroadcastChannel` module.

---

## Setup

Configure a stream provider in your silo:

```fsharp
open Orleans.FSharp.Runtime

let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    addMemoryStreams "StreamProvider"
}
```

---

## Publishing

Get a stream reference and publish events:

```fsharp
open Orleans.FSharp.Streaming

let streamProvider = client.GetStreamProvider("StreamProvider")

let stream = Stream.getStream<OrderEvent> streamProvider "orders" "us-east"

do! Stream.publish stream (OrderPlaced { OrderId = "123"; Total = 99.99m })
do! Stream.publish stream (OrderShipped { OrderId = "123"; TrackingNumber = "ABC" })

// One provider-native publish batch. Providers may split or coalesce delivery batches.
do! Stream.publishBatch stream pendingEvents
```

`Stream.getStream` is a purely local operation -- it creates a reference without contacting the silo.

`Stream.publishBatchFrom stream token events` supplies an explicit starting sequence token when
the provider supports it. `Stream.complete stream` and `Stream.fail stream error` forward directly
to Orleans' producer terminal methods. Provider behavior is deliberately preserved: Orleans
10.3.1's persistent stream producer throws `NotImplementedException` for both terminal methods,
while a provider which emits them reaches the consumer callbacks below.

---

## Subscribing (Push-based)

Subscribe with a callback handler:

```fsharp
let! subscription =
    Stream.subscribe stream (fun event ->
        task {
            printfn "Received: %A" event
        })

// Later, unsubscribe
do! Stream.unsubscribe subscription
```

The subscription is durable and persists beyond grain deactivation.

`Stream.subscribeWithToken` is the same subscribe with a handler that also receives each event's
sequence token — the cursor you need to checkpoint; see
[Rewinding / Resuming](#rewinding--resuming).

Use first-class terminal callbacks when error and completion are part of the consumer protocol:

```fsharp
let handlers =
    StreamHandlers.create (fun event -> processEvent event)
    |> StreamHandlers.withError (fun error -> recordFailure error)
    |> StreamHandlers.withCompletion (fun () -> markComplete ())

let! subscription = Stream.subscribeHandlers stream handlers
```

`StreamHandlers.withToken` constructs the same callback set with an item handler of
`'T -> StreamSequenceToken option -> Task<unit>`.

For provider-native batch delivery:

```fsharp
let batchHandlers =
    StreamBatchHandlers.create (fun batch ->
        batch
        |> Array.map _.Item
        |> processBatch)
    |> StreamBatchHandlers.withError recordFailure
    |> StreamBatchHandlers.withCompletion markComplete

let! subscription = Stream.subscribeBatch stream batchHandlers
```

Each `StreamBatchItem<'T>` contains `Item` and an optional provider `Token`. Ordering inside a
delivered batch is preserved; do not assume one publish batch maps to exactly one callback batch.

Server-side filtering passes opaque filter data to Orleans. The named provider must have an
`IStreamFilter` registered:

```fsharp
let! subscription = Stream.subscribeFiltered stream "tenant:42" handlers
```

The F# wrapper does not run a client-side predicate and does not reinterpret the string.

---

## Consuming as TaskSeq (Pull-based)

Convert a stream to a `TaskSeq<'T>` for pull-based consumption with backpressure:

```fsharp
open FSharp.Control
open Orleans.FSharp.Streaming

let consume stream =
    task {
        let events = Stream.asTaskSeq stream

        // Process events as they arrive.
        for event in events do
            do! processEvent event
    }
```

Internally, `asTaskSeq` uses a bounded `Channel` with capacity 1000 and `BoundedChannelFullMode.Wait` for backpressure when the consumer falls behind.

---

## Rewinding / Resuming

Subscribe from a specific sequence token to resume processing from a checkpoint:

```fsharp
let! subscription =
    Stream.subscribeFrom stream savedToken (fun event ->
        task {
            processEvent event
        })
```

This works on any rewindable stream provider — Orleans' in-memory streams and Event Hubs both
are; a non-rewindable provider rejects the token.

### Getting the cursor to save

`subscribeWithToken` is the subscribe whose handler receives each event's own sequence token —
that token *is* the checkpoint:

```fsharp
let mutable checkpoint : StreamSequenceToken option = None

let! subscription =
    Stream.subscribeWithToken stream (fun event token ->
        task {
            processEvent event
            checkpoint <- token   // save it wherever you keep your position
        })
```

The token is `Some` on a rewindable provider and `None` on one that supplies no cursor — the same
reading as `context.streamSequenceToken` on the [functional grain runtime](functional-grains.md).
Save it *after* the event is processed: the token belongs to the event you just handled.

To resume and keep checkpointing, use `subscribeFromWithToken`, which is `subscribeFrom` with the
cursor-carrying handler:

```fsharp
let! resumed =
    Stream.subscribeFromWithToken stream savedToken (fun event token ->
        task {
            processEvent event
            checkpoint <- token
        })
```

Two behaviours are worth knowing before you rely on this, both measured against Orleans' memory
streams in `tests/Orleans.FSharp.Integration/StreamingIntegrationTests.fs`:

- **A new subscription's rewind is inclusive.** The event that produced `savedToken` is delivered again, so a
  handler that resumes from its last processed event will see that event twice. Checkpoint what
  you have completed and make the handler idempotent, or save the token before processing if
  at-most-once is what you want.
- **The backlog arrives with the next delivery cycle, not immediately.** A resumed subscription
  that then sits idle received nothing at all for 30 seconds; a single further publish to the
  stream flushed the whole backlog from the checkpoint plus the new event. On a live stream this
  is invisible; in a test, publish rather than sleep.

`Stream.getSequenceToken` is **deprecated** (`[<Obsolete>]` — a warning, not an error) and still
returns `None`. It was never a lookup: `StreamSubscriptionHandle` carries no cursor, so there was
nothing for it to return. Use `subscribeWithToken` / `subscribeFromWithToken`, or — on the
functional grain runtime — read `context.streamSequenceToken` inside an `onStream` hook.

---

## Resuming Subscriptions After Reactivation

After a grain reactivates, existing durable subscriptions need new handlers:

```fsharp
do! Stream.resumeAll stream (fun event ->
    task {
        processEvent event
    })
```

Use `resume` / `resumeFrom` for one subscription with `StreamHandlers`, `resumeBatch` /
`resumeBatchFrom` for batch callbacks, and `resumeAllHandlers` / `resumeAllBatch` for every durable
subscription. The corresponding new-subscription functions are `subscribeFromHandlers`,
`subscribeFromFiltered`, and `subscribeBatchFrom`.

There is one deliberate Orleans distinction: `subscribeFrom*` creates a new subscription and the
tested memory provider includes the checkpoint event, while `resumeFrom` / `resumeBatchFrom`
reattach a handle which already acknowledged that event and continue after it. Keep that
new-subscription versus existing-handle distinction in migration tests for your chosen provider.

---

## Listing Subscriptions

Get all active subscriptions for a stream:

```fsharp
let! subscriptions = Stream.getSubscriptions stream

for sub in subscriptions do
    printfn "Active subscription"
```

---

## Broadcast Channels

Broadcast channels deliver messages to ALL subscriber grains (fan-out), unlike streams which target individual consumers.

### Setup

```fsharp
let config = siloConfig {
    useLocalhostClustering
    addBroadcastChannel "Notifications"
}
```

### Publishing

```fsharp
open Orleans.FSharp.BroadcastChannel

let provider = client.ServiceProvider.GetRequiredService<IBroadcastChannelProvider>()
let channel = BroadcastChannel.getChannel<string> provider "alerts" "global"

do! BroadcastChannel.publish channel "System maintenance at midnight"
```

### Consuming

A broadcast consumer is a functional definition operation — see
[implicit subscriptions](#implicit-subscriptions) below.

---

## Implicit subscriptions

An **implicit** subscription inverts the usual order: instead of a grain subscribing to a stream,
a grain *type* declares a namespace, and publishing to `StreamId.Create(namespace, key)` activates
the grain whose identity encodes `key` — creating it if it does not exist — and delivers the item.

### On the functional grain runtime (`grainContract` / `grainFor`)

Two definition operations, `onStream` and `onBroadcast`:

```fsharp
let inboxDefinition =
    grainFor InboxApi.contract {
        defaultState (fun () -> { mail = [] })

        // provider name, stream namespace, hook
        onStream "StreamProvider" "chat.messages" (fun context state (item: Message) ->
            task { return { state with mail = state.mail @ [ item ] } })

        // the same shape over a broadcast-channel provider
        onBroadcast "BroadcastProvider" "chat.control" (fun context state (item: Control) ->
            task { return state })

        handle (_.read) (fun _ state () -> task { return state, state.mail })
    }
```

The hook's item type is inferred from the lambda, so it usually needs an annotation
(`(item: Message)`). Nothing else is required: no attribute, no class grain, no code generation.
The runtime publishes the manifest binding Orleans' `[ImplicitStreamSubscription]` /
`[ImplicitChannelSubscription]` publishes, and the activation accepts the delivery through
Orleans' own `IStreamSubscriptionObserver` / `IOnBroadcastChannelSubscribed` seams.

### Publishing to an implicitly subscribed grain

Orleans routes an implicit delivery to `GrainId.Create(grainType, streamId.Key)` — the stream key
bytes **verbatim**. The stream key must therefore be the grain key *in the contract's own Orleans
encoding*, and `StreamId.Create`'s own overloads do not always produce it. Use the contract:

```fsharp
let streamId = FunctionalGrain.streamId InboxApi.contract "chat.messages" inboxKey
do! provider.GetStream<Message>(streamId).OnNextAsync message

// broadcast channels have the same helper
let channelId = FunctionalGrain.channelId InboxApi.contract "chat.control" inboxKey
do! provider.GetChannelWriter<Control>(channelId).Publish control
```

`stringKey` and `guidKey` happen to agree with `StreamId.Create(ns, key)`, but **`int64Key` does
not**: `StreamId.Create(ns, 42L)` writes decimal `"42"` while Orleans'
`GrainIdKeyExtensions.CreateIntegerKey` — which the codec uses, because that is what an
`IGrainWithIntegerKey` identity really is — writes hexadecimal `"2A"`. A publish built the naive
way silently lands on a *different* grain (the one whose key reads as `0x42` = 66). The compound
codecs have no `StreamId.Create` overload at all. `FunctionalGrain.streamId` asks the contract, so
it cannot drift.

**Rules, all of them checked rather than assumed:**

| Rule | Where it is enforced |
|---|---|
| Provider and namespace must be non-blank | definition sealing |
| One hook per `(provider, namespace)` pair, per transport | definition sealing |
| `statelessWorker` cannot be combined with `onStream` / `onBroadcast` | definition sealing |
| The named provider must be registered on the silo | silo startup validation |

**Delivery semantics** follow the `onTimer` rules exactly:

- a delivery is an ordinary **non-reentrant** grain call (Orleans' `IStreamConsumerExtension`
  delivery methods carry no `[AlwaysInterleave]`), so it takes a turn like any other call;
- **whole-state replacement**: the hook receives the current state and returns the replacement,
  which is published in memory **only when the hook returns successfully**;
- the runtime issues **no storage call** of its own — write explicitly through
  `context.persistentState` if you want durability;
- `context.cancellationToken` is `CancellationToken.None` (the Orleans delivery path supplies
  none);
- `context.streamSequenceToken` is `Some` for an `onStream` delivery on a rewindable provider
  (Orleans' memory streams are rewindable) and `None` otherwise — always `None` for
  `onBroadcast`, which has no cursor. The runtime never rewinds with it: a fresh activation
  resumes at the subscription's current position. Use it to checkpoint or de-duplicate.

**A throwing `onStream` hook.** The exception travels back to Orleans' pulling agent, which
**redelivers the same item** with backoff for up to
`StreamPullingAgentOptions.MaxEventDeliveryTime` (one minute by default) and then moves on. An
implicit subscription is never faulted by a delivery failure — Orleans'
`PersistentStreamPullingAgent.ErrorProtocol` excludes implicit subscriptions from subscription
faulting explicitly — so the next item still arrives. Delivery is therefore **at-least-once**: a
hook that is not idempotent should de-duplicate.

**A throwing `onBroadcast` hook** is not retried — a broadcast publish is a direct fan-out grain
call, not a queued one. Where the failure shows up depends on
`BroadcastChannelOptions.FireAndForgetDelivery`, which Orleans defaults to **`true`**: in that
default mode `BroadcastChannelWriter` logs it at `Error` and the publisher's `Publish` still
completes; with `FireAndForgetDelivery = false` the publisher's `Publish` faults with an
`AggregateException` carrying it.

**A broadcast item of the wrong type behaves the same way**, and that is deliberate. Orleans checks
the runtime type inside the consumer extension and routes a mismatch into the subscription's
*error* callback as an `InvalidCastException` naming both types — never into the hook. This
runtime faults that callback, so the mismatch surfaces on exactly the path a throwing hook takes
(logged in the default mode, thrown to the publisher in the awaited one). Completing it quietly
would let Orleans report the item as delivered while no hook ever saw it. The hook is not entered,
no state is published, and the subscription stays healthy — the next correctly-typed publish is
delivered normally.

**One caveat worth knowing.** Orleans' implicit-subscription binding names a *namespace*, not a
provider. If a silo runs two stream providers and an item is published to a declared namespace on
a provider the definition does not name, Orleans still routes it to this grain type; the runtime
matches on `(provider, namespace)`, logs a warning, and leaves the item undelivered.

Implicit `onStream` batch delivery is not exposed: that definition hook receives one item at a
time. Explicit `Stream.subscribeBatch` is the separate provider-native batch API.


## Stream Providers

### Event Hubs

```fsharp
open Orleans.FSharp.StreamProviders

let configFn = StreamProviders.addEventHubStreams "EventHub" connStr "my-hub"
```

### Azure Queue

```fsharp
let configFn = StreamProviders.addAzureQueueStreams "AzureQueue" connStr
```

### Redis Streams (experimental)

```fsharp
let configFn = StreamProviders.addRedisStreams "Redis" "localhost:6379"
```

> **Experimental.** `addRedisStreams` requires a prerelease `Microsoft.Orleans.Streaming.Redis`
> package (`-alpha` / `-preview`) at runtime — there is no stable 10.x release yet. The helper
> resolves the provider by reflection, so an absent package yields a clear "install the package"
> error rather than a build break.

Apply these to the `ISiloBuilder` directly or via `addCustomStorage` in the silo config.

---

## Complete functional examples

- [Functional Grain Runtime](functional-grains.md#implicit-subscriptions-onstream-and-onbroadcast)
- [Feature tour source](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/feature-tour)
- [Legacy Streaming](legacy/streaming.md)

## Next steps

- [Legacy API](legacy/index.md) -- maintenance documentation for earlier authoring models
- [Silo Configuration](silo-configuration.md) -- configure stream providers
- [Event Sourcing](event-sourcing.md) -- CQRS pattern with event streams
