---
title: "Legacy: Streaming Examples"
description: "Streaming examples for the original grain authoring model."
---

# Legacy Streaming Examples

> **Archived and unsupported.** This material is retained only to help migrate existing
> applications. There is no new Legacy release line, feature or compatibility work, or security
> fixes. New development must use the current functional API.

**Streaming examples for the original Orleans.FSharp grain authoring model.**

> The streaming modules remain current. This page only isolates the old grain-definition examples which previously appeared in the current guide.
> New actors use the functional `grainContract` / `grainFor` subscription operations.

### On the classic `grain { }` / C# class path

Implicit subscriptions there are a per-grain Orleans attribute. The universal grain pattern shares
a single `FSharpGrainImpl` class, so it cannot carry a per-grain
`[ImplicitStreamSubscription("namespace")]`. When maintaining a legacy application which already
has an application-owned concrete C# grain class, annotate that class directly. The repository's
CodeGen project is archived, non-packable source, and its retained generator does not create
ordinary grain classes. For new actors, use the functional subscription operations; for explicit
subscriptions from any grain, use `Stream.subscribe` (shown above), which works with the universal
pattern.

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
