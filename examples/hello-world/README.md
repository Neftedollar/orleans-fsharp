# Hello World

Minimal Orleans.FSharp example: defines a counter grain with the `grain {}` computation expression, starts a localhost silo, increments the counter 5 times, and prints results.

## How to run

```bash
dotnet run --project src/Silo
```

## Expected output

```
--- Hello World: Counter Grain ---
Increment #1 -> count = 1
Increment #2 -> count = 2
Increment #3 -> count = 3
Increment #4 -> count = 4
Increment #5 -> count = 5
Final count: 5
Done. Shutting down...
```

## Key concepts

- **`grain {}`** computation expression for declarative grain behavior
- **`siloConfig {}`** computation expression for silo configuration
- **`useJsonFallbackSerialization`** enables clean F# types without `[GenerateSerializer]` attributes
- **`GrainRef`** type-safe grain references with `invoke`
- **CodeGen C# project** bridges F# grain definitions to the Orleans source generator

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
