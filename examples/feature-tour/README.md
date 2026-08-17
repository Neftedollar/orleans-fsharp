# Feature Tour

One runnable app that takes every Orleans feature people ask about and puts it on the
**functional grain runtime** (`grainContract` + `grainFor`), section by section, against a live
silo. Where a feature works, it runs here. Where it doesn't, this README says exactly why and
what the failure looks like.

Nothing in the tour is asserted from documentation: every row of the status matrix below is
backed by a line the app prints when you run it.

## How to run

```bash
dotnet run --project src/FeatureTour
```

Takes about a minute (one section waits for a real Orleans reminder to fire, another deploys a
two-silo cluster). Logging is pinned to `Error` so the transcript stays readable — genuine
failures still print.

Every section ends in a `->` verdict line, and **each verdict is computed from what that section
observed**, never hardcoded. If any section stops holding, the app lists it and exits non-zero,
so this file is a check and not just a demo.

## Status matrix

Status means:

- **supported** — the functional runtime does this directly; nothing but the API you already use.
- **composed** — it works, but you assemble it from stock Orleans parts the contract does not
  expose; the recipe is in this repo, in the section named.
- **wall** — it does not work today, and the reason is named.

| # | Feature | Status | Why, and where to look |
|---|---|---|---|
| 1 | Persistence: `stateFrom` + extra `usePersistentState` holders | **supported** | `Persistence.fs`, tour §1. One primary holder whose loaded value *is* the handler's `state`, plus a second, differently-typed holder on a different provider. Every read, write and clear is explicit — returning new state from a handler publishes it in memory only. `RecordExists` is `false`, then `true` after the writes, still `true` after `deactivateOnIdle` and a fresh activation, `false` again after `ClearStateAsync`. |
| 2 | Timers and reminders | **supported** | `Scheduling.fs`, tour §2. `onTimer` at 200 ms ticks visibly; `onReminder "heartbeat"` is registered in Orleans' real reminder table (the tour reads it back through `IReminderRegistry`) and **fires during the run**. The one-minute floor is `ReminderOptions.MinimumReminderPeriod` and it constrains the *period*, not the *due time* — which is why a 3-second due time with a 1-minute period is both legal and demonstrable. |
| 3 | Grain call filters | **supported** | `CallFilters.fs`, tour §3. A stock `IIncomingGrainCallFilter` reads `IFunctionalRequestMetadata` (grain type, contract version, operation id, `readOnly`/`oneWay`/`alwaysInterleave` flags, payload size) and rejects one designated operation; the rejection surfaces to the caller before the handler runs. |
| 4 | Request context | **supported** | `RequestContextTour.fs`, tour §4. The client sets a correlation id with `RequestCtx.set`; the handler reads it with `context.tryGetRequestContext`; the handler then adds its own value with `context.setRequestContext` and it flows onward into a grain-to-grain call. |
| 5 | Cancellation | **supported** | `Cancellation.fs`, tour §5. `rawRef.callCancellable` cancelled mid-flight: the caller gets `OperationCanceledException`, and the target independently observes the trip on `context.cancellationToken`. Cooperative only — it rolls nothing back. |
| 6 | Contract versioning | **supported** | `VersioningTour.fs`, tour §6. Two contracts over one `grainType` at versions 1 and 2. Matching succeeds; the mismatch is refused with `grain type 'tour.versioned' hosts contract version 1 but received version 2`. Matching is `=`, never `>=`: a version bump is a breaking wire change for every caller. |
| 7 | Streams | **supported** | `Streams.fs`, tour §7. The producer is an ordinary handler: there is no `context.streamProvider`, because Orleans exposes `GetStreamProvider` only on `Grain`/`IGrainBase`/`IClusterClient` — so it resolves the named `IStreamProvider` as a **keyed service** off `context.services`. All three consumer arms receive every event: an external `IClusterClient` over the gateway, a functional grain subscribing from its own `onActivate`, and a subscription taken outside any grain context. |
| 8 | Observers (classic path) | **composed** | `ObserverTour.fs` + `src/TourInterop`, tour §8. `Observer.createRef` / `subscribe` / `notify` / `unsubscribe` all work from a functional grain — **provided the observer interface is declared in a C#-compiled assembly**. That requirement is Orleans': its proxy source generators are Roslyn generators and never run over F#, so an F#-declared `IGrainObserver` has no proxy and `CreateObjectReference` fails on it. Identical for the `grain { }` CE and for class grains. **This row is what the tour runs.** The library now also ships *functional observers*, which need no application C# at all — see the next row. |
| 8b | Observers (functional) | **supported**, not exercised here | Not in this app, and deliberately: the tour's rule is that every row is backed by a line it prints, and retro-fitting §8 would replace the evidence for row 8 rather than add to it. The capability is proved elsewhere, end to end on a real cluster — `tests/Orleans.FSharp.Integration/FunctionalPushIntegrationTests.fs` (delivery, unsubscribe, a throwing observer, manager expiry, two brands, and the handle's wire form) and `examples/chat-room`, whose transcript shows `[push]` lines arriving between the calls and stopping after `unsubscribe`. `observerContract` + `FunctionalObserver.create` / `notify` + `FunctionalObserverManager`; no observer interface and no code generation in application code. |
| 9 | Broadcast channels | **composed** | `Broadcast.fs` + `src/TourInterop`, tour §9. The **producer** is a plain functional handler (`BroadcastChannel.publish` over the keyed `IBroadcastChannelProvider`). The **consumer** is always a class grain — but it does **not** have to be C#. See ["An F#-only broadcast consumer"](#an-f-only-broadcast-consumer) below for the two things it needs. |
| 10 | Heterogeneous cluster | **supported** | `Heterogeneous.fs`, tour §11. A two-silo cluster where `tour.regional` is registered only on the non-primary silo. Every call to it lands on that silo, while `tour.everywhere` spreads across both. Driven from an external client that installs the transport with `AddFunctionalGrainClient`. |
| 11 | Implicit **stream** subscriptions for a functional grain | **wall** | Not in the app; the failure is reproduced and quoted in ["Implicit stream subscriptions"](#implicit-stream-subscriptions-the-exact-wall) below. Short version: the functional manifest publishes no stream-binding properties, and even when you add them by hand the activation has no stream consumer to hand the item to, so Orleans drops it. Closing this is a runtime feature, not a composition. |
| 12 | Stateless workers and flexible placement | **supported** | `Placement.fs`, tour §10. Spec 004 item 4: `statelessWorker maxLocalWorkers` and `placement strategy` are first-class `grainFor` definition operations — `WorkerDefinition` declares `statelessWorker 4` directly, and the registry's own properties provider publishes the manifest properties (`placement-strategy`, `max-local-instances`, `remove-idle-workers`, `unordered`), verified identical to a live `StatelessWorkerAttribute` by a property-key exactness test. Measured: 8 concurrent 400 ms calls to one grain id finish in ~0.8 s across 4 activations, against 1 activation and ~3.2 s without it. Composing placement by hand through an application `IGrainPropertiesProvider` (`FunctionalPlacementProvider`, kept in the same file) remains possible for placement needs the closed operation set does not cover. |
| 13 | Distributed ACID transactions | **out of scope** | Out of spec 003 by design. The classic path is on the KEEP list and is demonstrated end to end in [`examples/bank-transactions`](../bank-transactions) — two accounts, one atomic transfer. Nothing about the functional runtime removes it; it simply is not part of this authoring model yet. |
| 14 | `IAsyncEnumerable<'T>` replies | **wall** | An API record field is exactly `'Arg -> Task<'Reply>`, and the transport carries one serialized reply payload per request. Streaming replies would be a new transport feature (a chunking protocol with its own back-pressure and cancellation semantics), not sugar over the existing one. Today: use Orleans streams (row 7) for push, or a `readOnly` paged query for pull. |

## Walls and hazards found while building this

These are not hypotheticals. Each was hit while writing the tour, and each is reproduced by code.

### F# tuples of FSharp.Core generics did not cross the transport — FIXED

**This was a live bug found while writing this tour; it is fixed now.** An argument or reply that
was a *tuple* whose elements are generic types from FSharp.Core (`option`, `list`, `Map`, …)
failed at deserialization with one of two diagnostics:

```text
FSharpBinaryCodec: type 'Microsoft.FSharp.Collections.FSharpList`1[[System.String, System.Private.CoreLib, ...]]'
not found. Ensure the type is in a loaded assembly.

FSharpBinaryCodec: the payload declares type 'Microsoft.FSharp.Core.FSharpOption`1[[System.String, ...]]',
which is not assignable to the expected type 'System.Tuple`2[[...FSharpOption`1...],[...FSharpOption`1...]]'.
```

Orleans owns `System.Tuple`, so a tuple payload never reaches the F# codec whole: Orleans' own
`TupleCodec` decomposes it and hands each element to the F# codec *individually*, as its own
field carrying only that element's `FullName`. Both halves of top-level payload handling were
scoped to the declared type alone — the declaration table did not contain the elements (and
`Type.GetType` cannot resolve a generic whose outer type lives in FSharp.Core), and the
expected-payload-type guard compared each element against the whole tuple.

The fix declares a payload type's constituents with it and admits them in the guard. Tuples of
`option`, `list`, `Map`, `Set`, `Result`, arrays, records and nested tuples now round-trip in both
argument and reply position; the regression suite is
`tests/Orleans.FSharp.Tests/FunctionalTupleCodecTests.fs`.

`RequestContextTour.fs` still returns a two-field `ContextView` record rather than a tuple — a
record was always the clearer shape for a named pair, and it is no longer a workaround.

### `ClearStateAsync` leaves an F# record with null fields

After `ClearStateAsync()`, Orleans re-seeds the facet with an **uninitialized** instance of the
stored type. For an F# record that means every reference field comes back `null` — and `null` is
not a legal value of an F# `list`. The tour prints the observation:

```text
hazard observed on clear: cleared facet State is a record whose 'events' list field is null
```

**Re-seed the holder explicitly after every `ClearStateAsync`** (`Persistence.fs` does) — the
initializer is not re-run, and the runtime deliberately does not re-initialize for you, because
only the application knows what "empty" means.

The *diagnostic* for forgetting has since been fixed. It used to die far from the cause, as
`ArgumentNullException: Value cannot be null. (Parameter 'source')` raised inside whichever
collection loop first touched the field. It now names the field, the record, and this cause:

```text
FSharpBinaryCodec: field 'events' of the record '…TourState' is null, but its declared type
'…FSharpList`1[…]' has no null value. The usual cause is a persistent state that was cleared and
not re-initialized: after ClearStateAsync the holder's State is a fresh uninitialized instance,
so assign a freshly initialized state before the next write.
```

### An application filter cannot type-test the functional request

The library's own integration fixture writes `context.Request :? FunctionalRequest`. Application
code cannot: `FunctionalRequest` is `internal` to `Orleans.FSharp.Abstractions`. The supported
surface is `IFunctionalRequestMetadata`, published as **argument 0** of every functional request,
so the type test goes on the argument rather than on the request:

```fsharp
if request.GetArgumentCount() > 0 then
    match request.GetArgument 0 with
    | :? IFunctionalRequestMetadata as metadata -> ...
    | _ -> ()   // not a functional call — Orleans' own system grains use this pipeline too
```

### `IReminderRegistry` lives in `Orleans.Timers`

Not `Orleans.Runtime`. The reminder-retirement snippet in
[`docs/functional-grains.md`](../../docs/functional-grains.md) omits the `open Orleans.Timers`
its own code needs.

### Assemblies reached only through F# must be pre-loaded

Orleans takes its application-part snapshot inside `UseOrleans`, before your configuration runs,
and its generators never run over F# — so an assembly reached only through an F# reference is
invisible to that snapshot and its grain classes simply do not exist as far as the silo is
concerned.

Building a `siloConfig { }` value now pre-loads every Orleans assembly `Orleans.FSharp.Runtime`
references (the set is derived from its own references, not hand-written), so reminders,
streaming and broadcast channels are covered for you. What is **not** covered is an assembly of
your own: this example's `TourInterop` is reached only from F#, and without the explicit touch in
`preloadTourAssemblies` its broadcast-channel consumer grain is missing from the cluster with no
error anywhere. Any third-party provider you reach only through F# needs the same.

## An F#-only broadcast consumer

Orleans' `[ImplicitChannelSubscription]` + `IOnBroadcastChannelSubscribed` pair needs a *class*
grain, but not a C# one. `Broadcast.fs` ships both arms, and both receive every publish. The F#
one needs exactly two things beyond the attribute and the interface:

1. **It must also implement `IGrainWithStringKey`.** That interface is declared and
   code-generated inside Orleans itself, so it costs no C# of yours — and without it Orleans
   routes the publish and then fails to activate:

   ```text
   InvalidOperationException: Unable to find an IGrainContextActivatorProvider for grain type fsharpbroadcastconsumer
   ```

2. **Its class must be added to `GrainTypeOptions.Classes` by hand**, because an F# assembly
   carries none of the `[ApplicationPart]` / `[TypeManifestProvider]` attributes Orleans'
   generators would have emitted, so nothing otherwise tells the silo the class exists.

## Implicit stream subscriptions: the exact wall

Reproduced outside this app, because the outcome is a wall and shipping it would only add a
section that prints a failure. What happens, step by step:

1. `ImplicitStreamSubscriptionAttribute` implements `IGrainBindingsProviderAttribute`, and
   Orleans collects bindings **only** from attributes on the grain class. Unlike placement
   (row 12) there is no public `IGrainBindingsProvider` service to register instead, and the
   grain class is the library's own marker — so there is no supported way to declare the binding.
2. Forcing it anyway — writing the attribute's own binding output into the manifest through an
   `IGrainPropertiesProvider` (`binding.0.type = stream`,
   `binding.0.pattern = namespace:<ns>`) — gets *further* than expected. Orleans resolves the
   functional grain type as an implicit subscriber and **activates it on publish**.
3. And then drops the event:

   ```text
   warn: Orleans.Streams.StreamConsumerExtension
     [GrainId probe.sink/k1, ...] got an item for subscription 0ad857f7-..., but I don't have
     any subscriber for that stream. Dropping on the floor.
   ```

The missing half is inside the activation: `FunctionalGrainMarker<'Actor>` and the functional
activation base implement `IFunctionalGrainTarget` and `IRemindable` and nothing else — no
`IStreamSubscriptionObserver`, and the definition has no way to declare a stream handler for the
runtime to route to. Closing this means the library publishing the binding properties itself
*and* accepting the subscription — a runtime feature.

**What works today instead:** an explicit `SubscribeAsync` from `onActivate`, which is tour §7
arm (b) and is fully supported. The only thing you give up is activation-on-first-event.

## Layout

```
src/FeatureTour/            the F# app — one module per feature, plus the driver
  Tour.fs                   console formatting and a polling helper
  Persistence.fs            §1
  Scheduling.fs             §2
  CallFilters.fs            §3
  RequestContextTour.fs     §4
  Cancellation.fs           §5
  VersioningTour.fs         §6
  Streams.fs                §7
  ObserverTour.fs           §8
  Broadcast.fs              §9
  Placement.fs              §10
  Heterogeneous.fs          §11
  Program.fs                the driver: silo configuration and every section
src/TourInterop/            the C# half: the observer interface and one broadcast consumer grain
```

`src/TourInterop` exists for one reason: it carries `Microsoft.Orleans.Sdk`, so Orleans' Roslyn
generators run over it. Everything that genuinely needs generated code — the observer proxy —
lives there. Nothing else does.

## Documentation

- [Functional Grain Runtime](../../docs/functional-grains.md) — the full authoring model
- [Streaming](../../docs/streaming.md), [Advanced](../../docs/advanced.md)
- [`examples/bank-transactions`](../bank-transactions) — ACID transactions (matrix row 13)
