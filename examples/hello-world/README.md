# Hello World

Minimal Orleans.FSharp example: defines a counter grain with the functional grain runtime
(`grainContract` + `grainFor`), starts a localhost silo, increments counters directly and through
`GrainBatch`, and runs a typed `StateMigration` chain. `CounterGrain.fs` also keeps the original
`grain {}` computation-expression version as deprecated reference -- see `Program.fs` for why it
cannot run standalone and
[docs/functional-grains.md](../../docs/functional-grains.md) for the full migration guide.

## How to run

```bash
dotnet run --project src/Silo
```

## Expected output

```
--- Hello World: Counter Grain (Functional Grain Runtime) ---
Increment #1 -> count = 1
Increment #2 -> count = 2
Increment #3 -> count = 3
Increment #4 -> count = 4
Increment #5 -> count = 5
Final count: 5
GrainBatch increments: [1; 1; 1] (aggregate = 3)
State migration v1 -> v2: count = 5, label = migrated counter
Done. Shutting down...
```

## Key concepts

- **`grainContract` / `grainFor`** the functional grain runtime's contract + definition pair (this
  example's live path)
- **`FunctionalGrain.ref`** typed grain reference whose record fields are callable operations
- **`GrainBatch.map` / `GrainBatch.aggregate`** concurrent fan-out over a dynamic collection of
  grain references, preserving input order before aggregation
- **`StateMigration.tryApplyMigrations`** validation plus a typed, pure state-schema upgrade chain
- **`siloConfig {}`** computation expression for silo configuration
- **`useJsonFallbackSerialization`** enables clean F# types without `[GenerateSerializer]` attributes
- **`grain {}`** (deprecated) the original computation expression, kept in `CounterGrain.fs` as
  reference -- it needs a C#-generated proxy per grain interface and cannot resolve standalone in
  an F#-only project; the functional runtime's proxies are pre-generated, so it needs no such bridge

## Why the shutdown helper is documented rather than run here

`Scripting.startOnPorts`, `Scripting.getGrain`, and `Scripting.shutdown` are public helpers for an
interactive `.fsx` session. A standalone executable already owns an `IHost`, as this example does,
so starting a second fixed-port host solely to call `Scripting.shutdown` would be artificial and
would obscure the normal `host.StartAsync()` / `host.StopAsync()` lifecycle. The genuinely owning
example is the interactive recipe in
[Advanced: interactive scripting](../../docs/advanced.md#interactive-scripting), which starts a
functional definition with `FunctionalScripting.startOnPorts` and always finishes with
`do! Scripting.shutdown silo`.

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
