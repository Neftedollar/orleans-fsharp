# Orleans.FSharp.EventSourcing.Marten

Marten + PostgreSQL backing store for `Orleans.FSharp.EventSourcing` grains.

## What it does

Wires Marten as a custom storage provider for F# event-sourced grains. Events go to Marten's append-only event store backed by PostgreSQL; snapshots and projections are available through the Marten document session model.

## Quick example

```fsharp
open Orleans.Hosting
open Orleans.FSharp.EventSourcing.Marten

let configureSilo (siloBuilder: ISiloBuilder) =
    siloBuilder.UseMartenEventSourcing(connectionString = "Host=localhost;Database=orleans;Username=postgres;Password=postgres")
```

## Requires

- A reachable PostgreSQL instance (Marten 8+ schema is created automatically on first run)
- `Orleans.FSharp.EventSourcing` for the F# event-sourced grain CE

See the [project README](https://github.com/Neftedollar/orleans-fsharp) for the full Orleans.FSharp story.
