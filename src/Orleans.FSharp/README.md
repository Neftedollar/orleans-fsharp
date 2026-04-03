# Orleans.FSharp

Idiomatic F# computation expressions and helpers for [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/) grain development.

## What it does

Orleans.FSharp replaces verbose C#-style grain implementations with a declarative `grain {}` computation expression. Define state, message handlers, persistence, timers, reminders, streaming, and placement strategy in a single expression.

### Modules included

`GrainState` | `GrainRef` | `Streaming` | `BroadcastChannel` | `Logging` | `Reminders` | `Timers` | `Observers` | `Filters` | `RequestCtx` | `Transactions` | `Versioning` | `Telemetry` | `Shutdown` | `StateMigration` | `Serialization` | `FSharpSerialization` | `Scripting` | `Kubernetes` | `GrainDirectory` | `GrainServices` | `GrainExtensions` | `Immutable` | `StreamProviders`

## Quick example

```fsharp
open Orleans.FSharp

type CounterMsg = Increment | GetCount

let counterGrain = grain {
    defaultState 0
    handle (fun state msg -> task {
        match msg with
        | Increment  -> return state + 1, box ()
        | GetCount   -> return state,     box state
    })
    persist "Default"
}
```

## Related packages

| Package | Purpose |
|---------|---------|
| `Orleans.FSharp.Runtime` | Silo and client hosting via `siloConfig {}` CE |
| `Orleans.FSharp.CodeGen` | C# bridge for Orleans Roslyn source generators |
| `Orleans.FSharp.Testing` | Test harness, mocks, and FsCheck integration |
| `Orleans.FSharp.EventSourcing` | Event-sourced grains via `eventSourcedGrain {}` CE |
| `Orleans.FSharp.Analyzers` | F# analyzer detecting `async {}` usage |

## Documentation

Full docs and examples: <https://github.com/Neftedollar/orleans-fsharp>

## License

MIT
