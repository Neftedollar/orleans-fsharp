# SignalR Realtime

Orleans grains pushing real-time metrics to a browser dashboard via SignalR. A dashboard grain
generates random system metrics on a 2-second declarative timer, and a SignalR hub sends the
latest ones to a browser when it connects.

The live path is the functional grain runtime's twin (`DashboardGrainFunctional.fs`): it reuses
`DashboardGrainDef.generateMetrics` verbatim (a plain pure function, not part of the deprecated CE)
and declares the same 2-second `onTimer`, so `latestUpdate` produces the exact same randomized
`DashboardUpdate` the original did. `DashboardHub.fs` calls this twin, not the old grain.
`DashboardGrain.fs` keeps the original `grain {}` version as deprecated reference -- see
`Program.fs` / `DashboardHub.fs` for why it cannot run standalone and
[docs/functional-grains.md](../../docs/functional-grains.md) for the full migration guide.

**Two gaps were found and fixed here, verified with a real browser (Playwright), not just a build.**

1. **Standalone-hosting gap (same class as `examples/fable-fullstack`).** This is a
   `WebApplication.CreateBuilder()` app, so it calls `SiloConfig.applyToSiloBuilder` *inside* its
   own `builder.Host.UseOrleans(...)` delegate rather than through `SiloConfig.applyToHost`.
   `applyToSiloBuilder`'s own internal assembly pre-load then runs a step too late for Orleans'
   manifest-snapshot timing. Without a fix, the server crashed at startup with `Could not find an
   implementation for interface Orleans.Storage.IMemoryStorageGrain`. `Program.fs` now force-loads
   the two assemblies `applyToHost` would have, before `UseOrleans` runs.
2. **A pre-existing, unrelated JSON-casing bug in `wwwroot/index.html`**, invisible until gap 1 was
   fixed (the hub never successfully pushed real data before, so the bug never had anything to
   render incorrectly). SignalR's default Hub protocol (`System.Text.Json`) serializes with
   camelCase property names (`metrics`, `sequenceNumber`, `name`, `value`, `timestamp`), not the F#
   record's PascalCase. The JS read `update.Metrics` / `update.SequenceNumber` etc., so real updates
   silently rendered nothing (`Update #undefined`, no metric cards) even once the hub was pushing
   correctly. Fixed by reading the camelCase field names.

Confirmed with an actual browser session (not just `dotnet build`): connected, received a real
`ReceiveMetrics` push with a live-generated `DashboardUpdate`, and the four metric cards rendered
with real values -- see the commit for this example.

**A correction to this README's own older claim:** the dashboard does **not** push continuously to
connected browsers via `IHubContext<T>` from the grain -- neither the old grain nor the functional
twin ever did that (there is no `IHubContext` usage anywhere in this example). The `onTimer` /
declarative timer keeps the grain's own sequence number advancing in the background regardless of
callers, but `DashboardHub.OnConnectedAsync` only sends **one** update, at connect time. The
architecture section below is corrected to describe what the code actually does.

## How to run

```bash
dotnet run --project src/Web
```

Then open http://localhost:5000 in your browser to see one live metrics push on connect.

## Expected output (console)

```
--- SignalR Realtime: Dashboard grain activated with timer (Functional Grain Runtime) ---
Sequence number after activation tick: 1
Open http://localhost:5000 in your browser to see live metrics.
Press Ctrl+C to stop.
```

The browser will show a dark-themed dashboard with four metric cards populated once, on connect:
- **CPU** usage percentage
- **Memory** usage percentage
- **Requests per second**
- **Latency** in milliseconds

Reloading the page reconnects and shows a fresh set of values with a higher sequence number (the
background timer keeps advancing it even between page loads).

## Key concepts

- **`grainContract` / `grainFor`** the functional grain runtime's contract + definition pair (this
  example's live path): `tick` / `sequenceNumber` (`readOnly`) / `latestUpdate`
- **`onTimer`** a declarative 2-second timer, same cadence as the original, that advances the
  sequence number independent of any caller
- **`DashboardGrainDef.generateMetrics`** reused verbatim by the functional twin -- a plain pure
  function, not part of the deprecated CE, so both authoring styles generate identical metrics
- **SignalR hub** (`DashboardHub.fs`) calls the functional twin's `latestUpdate` and sends the
  result to the connecting client only -- see the architecture correction above
- **Co-hosted** Orleans silo + ASP.NET Core + SignalR in the same process
- **`grain {}`** (deprecated) the original computation expression, kept in `DashboardGrain.fs` as
  reference -- needs a C#-generated proxy per grain interface and cannot resolve standalone in an
  F#-only project
- **wwwroot/index.html** minimal HTML + JS using `@microsoft/signalr`; reads the camelCase field
  names SignalR's default JSON protocol actually sends (see the fix above)
- **`useJsonFallbackSerialization`** enables clean F# record serialization for Orleans grain calls
  (independent of, and not the same serializer as, SignalR's own Hub protocol JSON)

## Architecture

```
Browser (SignalR JS client)
    |
    | connects
    v
ASP.NET Core (SignalR Hub, DashboardHub.OnConnectedAsync)
    |
    | dashboard.latestUpdate() -- one call, on connect
    v
Functional Grain (onTimer advances SequenceNumber every 2s, independent of this call)
    |
    | returns a freshly generated DashboardUpdate
    v
Hub sends it back to Clients.Caller only (not a broadcast, and not pushed again after connect)
```

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
