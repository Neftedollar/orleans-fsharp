# Orleans.FSharp.Abstractions

C# interface definitions for the `Orleans.FSharp` universal grain pattern.

## Why this exists

Orleans Roslyn source generators only work with C# projects. F# assemblies are invisible to them. This tiny package contains **only** the three `IFSharpGrain` interfaces — nothing else. By placing these interfaces in a C# project, Orleans can generate proxy code for all F# grains without requiring per-grain C# stubs.

## Interfaces

| Interface | Key type | Use when |
|-----------|----------|----------|
| `IFSharpGrain` | `string` | Default — most grains |
| `IFSharpGrainWithGuidKey` | `Guid` | Grains keyed by GUID |
| `IFSharpGrainWithIntKey` | `int` | Grains keyed by integer |

## Dependency chain

```
Orleans.FSharp.Abstractions (C#)   ← IFSharpGrain interfaces
        ↑                    ↑
Orleans.FSharp (F#)     Orleans.FSharp.CodeGen (C#)
        ↑
Orleans.FSharp.Runtime (F#)
```

No circular dependencies.

## NuGet

This package is a dependency of `Orleans.FSharp`. You do not need to reference it directly.
