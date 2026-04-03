# Streaming

**Guide to Orleans streaming with F#-idiomatic APIs.**

## What you'll learn

- How to publish events to streams
- How to subscribe with callbacks or pull-based TaskSeq
- How to use broadcast channels for fan-out
- How to rewind and resume stream consumption
- Implicit stream subscriptions on grains

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
            // Save the token for future recovery
        })
```

This is only supported by rewindable stream providers (e.g., Event Hubs).

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

Broadcast channel consumers are grains that implement `IOnBroadcastChannelSubscribed` with the `[ImplicitChannelSubscription]` attribute. This is handled by the C# CodeGen.

---

## Implicit Stream Subscriptions

Use `implicitStreamSubscription` in the `grain { }` CE to auto-subscribe a grain to a stream namespace:

```fsharp
let orderProcessor =
    grain {
        defaultState { ProcessedCount = 0 }
        handle myHandler
        persist "Default"

        implicitStreamSubscription "OrderEvents" (fun state event ->
            task {
                let orderEvent = event :?> OrderEvent
                return { state with ProcessedCount = state.ProcessedCount + 1 }
            })
    }
```

The grain is automatically subscribed when activated. Each grain ID receives events from the stream with the matching key.

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

- [Grain Definition](grain-definition.md) -- `implicitStreamSubscription` and other grain features
- [Silo Configuration](silo-configuration.md) -- configure stream providers
- [Event Sourcing](event-sourcing.md) -- CQRS pattern with event streams
