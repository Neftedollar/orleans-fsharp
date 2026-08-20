# Streaming

**Guide to Orleans streaming with F#-idiomatic APIs.**

> **Note.** The streaming APIs on this page are current. The `grain { }` CE used in the examples to
> host them now carries `[<Obsolete>]` (warning, not error); see
> [functional-grains.md](functional-grains.md) for the current grain authoring model.

## What you'll learn

- How to publish events to streams
- How to subscribe with callbacks or pull-based TaskSeq
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
```

`Stream.getStream` is a purely local operation -- it creates a reference without contacting the silo.

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

---

## Consuming as TaskSeq (Pull-based)

Convert a stream to a `TaskSeq<'T>` for pull-based consumption with backpressure:

```fsharp
open FSharp.Control

let events = Stream.asTaskSeq stream

// Process events as they arrive
for event in events do
    processEvent event
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

- **The rewind is inclusive.** The event that produced `savedToken` is delivered again, so a
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

On the **functional grain runtime** a broadcast consumer is a definition operation — see
[implicit subscriptions](#implicit-subscriptions) below. On the classic `grain { }` /
CodeGen path, a broadcast channel consumer is a class grain that implements
`IOnBroadcastChannelSubscribed` and carries `[ImplicitChannelSubscription]`.

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

Batch delivery (`IAsyncBatchObserver`) is not exposed: a hook receives one item at a time.

### On the classic `grain { }` / CodeGen path

Implicit subscriptions there are a per-grain Orleans attribute. The universal grain pattern shares
a single `FSharpGrainImpl` class, so it cannot carry a per-grain
`[ImplicitStreamSubscription("namespace")]`. Define the grain via `Orleans.FSharp.CodeGen` and
annotate the generated C# class. For explicit subscriptions from any grain, use `Stream.subscribe`
(shown above), which works with the universal pattern.

---

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

## Complete Example

```fsharp
open Orleans.FSharp.Runtime
open Orleans.FSharp.Streaming

// Configure
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    addMemoryStreams "Events"
    addBroadcastChannel "Alerts"
}

// Publish from a grain handler
let publisher =
    grain {
        defaultState ()
        handleWithContext (fun ctx state msg ->
            task {
                let streamProvider =
                    GrainContext.getService<IClusterClient> ctx
                    |> fun c -> c.GetStreamProvider("Events")
                let stream = Stream.getStream<string> streamProvider "logs" "app"
                do! Stream.publish stream $"Event: {msg}"
                return (), box ()
            })
    }

// Subscribe from client code
let streamProvider = client.GetStreamProvider("Events")
let stream = Stream.getStream<string> streamProvider "logs" "app"

let! sub = Stream.subscribe stream (fun msg ->
    task { printfn "Log: %s" msg })

// Pull-based consumption
let events = Stream.asTaskSeq stream
for event in events do
    printfn "Pulled: %s" event
```

## Next steps

- [Grain Definition](grain-definition.md) -- `interleaveMessage` and other grain features
- [Silo Configuration](silo-configuration.md) -- configure stream providers
- [Event Sourcing](event-sourcing.md) -- CQRS pattern with event streams
