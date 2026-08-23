---
title: "Legacy: Streaming Examples"
description: "Streaming examples for the original grain authoring model."
---

# Legacy Streaming Examples

**Streaming examples for the original Orleans.FSharp grain authoring model.**

> The streaming modules remain current. This page only isolates the old grain-definition examples which previously appeared in the current guide.
> New actors use the functional `grainContract` / `grainFor` subscription operations.

### On the classic `grain { }` / CodeGen path

Implicit subscriptions there are a per-grain Orleans attribute. The universal grain pattern shares
a single `FSharpGrainImpl` class, so it cannot carry a per-grain
`[ImplicitStreamSubscription("namespace")]`. Define the grain via `Orleans.FSharp.CodeGen` and
annotate the generated C# class. For explicit subscriptions from any grain, use `Stream.subscribe`
(shown above), which works with the universal pattern.

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

## Current guide

See [Streaming](/orleans-fsharp/streaming/) for functional `onStream` and `onBroadcast` subscriptions.
