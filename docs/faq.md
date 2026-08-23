# Frequently Asked Questions

## What is Orleans.FSharp?

Orleans.FSharp is a functional-first F# authoring layer over Microsoft Orleans. A grain exposes a plain F# API record, a `grainContract` defines its identity and policies, and `grainFor` or `journaledGrainFor` supplies behavior.

## Do I need a C# proxy or CodeGen project?

No. The functional runtime uses precompiled Orleans proxies from `Orleans.FSharp.Abstractions`. Your application writes the actor brand, API record, contract, and definition in F#.

## How do I create a grain reference?

```fsharp
let api = FunctionalGrain.ref CounterApi.contract grainFactory "counter-1"
let! count = api.increment ()
```

The returned value has the exact API-record type.

## Which Orleans versions are supported?

The current package range starts at Orleans 10.1.0 and is tested against both 10.1.0 and 10.2.2. A NuGet lower bound means older 10.0.x versions are not selected.

## Does event sourcing support snapshots?

Yes. `journaledGrainFor` supports Orleans log-consistency providers and typed custom storage. Snapshot policy can be configured globally and overridden per grain definition; the definition-level policy wins.

## Do streams, broadcasts, timers, and reminders work on journaled grains?

Yes. Journaled definitions support `onStream`, `onBroadcast`, `onTimer`, and `onReminder` with the same functional hooks as ordinary definitions. Event state changes still go through raised and confirmed events.

## Can C# call a functional grain?

Yes. Build a typed facade with `FunctionalGrainFacade.create`. C# receives normal task-returning methods and can consume streaming replies with `await foreach`.

## How do F# actors appear in Orleans Dashboard?

The Dashboard activation table displays the functional manifest type, for example `FunctionalGrainMarker<MyApp+CounterActor>`. The actor brand is visible; API-record fields are transported through the shared functional dispatch operation. See [Dashboard](dashboard.md) for a runnable example and the exact current display.

## How do I start a project?

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyApp
```

Then follow [Getting Started](getting-started.md).

## Where is documentation for existing applications on the original API?

It is retained under [Legacy API](legacy/index.md), separate from current guides.

## Is it production-ready?

The library includes unit and live Orleans integration suites, dual-version CI coverage, wire-contract validation, persistence, transactions, event sourcing, streaming, reminders, timers, and production hosting helpers. As with any distributed runtime, validate storage providers, deployment topology, observability, and upgrade behavior against your own workload.

## Where is the source?

[github.com/Neftedollar/orleans-fsharp](https://github.com/Neftedollar/orleans-fsharp)
