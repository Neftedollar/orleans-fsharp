# Hello World

Minimal Orleans.FSharp example: defines a counter grain with the functional grain runtime
(`grainContract` + `grainFor`), starts a localhost silo, increments the counter 5 times, and
prints results. `CounterGrain.fs` also keeps the original `grain {}` computation-expression
version as deprecated reference -- see `Program.fs` for why it cannot run standalone and
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
Done. Shutting down...
```

## Key concepts

- **`grainContract` / `grainFor`** the functional grain runtime's contract + definition pair (this
  example's live path)
- **`FunctionalGrain.ref`** typed grain reference whose record fields are callable operations
- **`siloConfig {}`** computation expression for silo configuration
- **`useJsonFallbackSerialization`** enables clean F# types without `[GenerateSerializer]` attributes
- **`grain {}`** (deprecated) the original computation expression, kept in `CounterGrain.fs` as
  reference -- it needs a C#-generated proxy per grain interface and cannot resolve standalone in
  an F#-only project; the functional runtime's proxies are pre-generated, so it needs no such bridge

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
