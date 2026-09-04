# Orleans.FSharp.Abstractions

Pre-generated Orleans transport proxies used by the functional F# runtime.

## Why this package exists

Orleans' Roslyn source generators run in C# projects, not F# projects. This small C# assembly owns
the fixed transport interfaces and generated proxies that `grainContract` / `grainFor` use behind
typed F# API records. Applications therefore do not need to create a C# bridge or a grain interface
for each functional actor.

The package is normally pulled in transitively by `Orleans.FSharp`; application projects should
reference `Orleans.FSharp` and `Orleans.FSharp.Runtime` instead of adding this package directly.
Application code binds a `GrainContract<'Actor,'Key,'Api>` with `FunctionalGrain.ref`; it does
not call this assembly directly.

## Dependency chain

```text
Orleans.FSharp.Abstractions (C#; generated fixed transport proxies)
        ↑
Orleans.FSharp (functional contracts, definitions, typed references)
        ↑
Orleans.FSharp.Runtime (silo hosting)
        ↑
Application
```

## Legacy archive

The assembly temporarily retains old transport interfaces so existing applications can migrate.
They are unsupported and documented only in the
[Legacy API reference](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/legacy/api-reference.md).

## License

MIT
