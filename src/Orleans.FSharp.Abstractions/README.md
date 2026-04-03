# Orleans.FSharp.Abstractions

C# shim that enables Orleans proxy generation for all F# grains — add this to your silo project and get a zero-boilerplate F# actor system.

## Why this exists

Orleans Roslyn source generators only run on C# projects. F# assemblies are invisible to them. Without proxy classes, calling a grain from a client throws at runtime.

This package contains **only** three `IFSharpGrain` interfaces in a C# project. Because the project has `Microsoft.Orleans.Sdk`, source generators run and produce the proxy classes (`Proxy_IFSharpGrain` etc.) in this assembly. Any silo that references this package (directly or transitively via `Orleans.FSharp.Runtime`) can call all F# grains — with no per-grain C# code required.

## Interfaces

| Interface | Key type | F# handle type |
|-----------|----------|----------------|
| `IFSharpGrain` | `string` | `FSharpGrainHandle<'S,'M>` |
| `IFSharpGrainWithGuidKey` | `Guid` | `FSharpGrainGuidHandle<'S,'M>` |
| `IFSharpGrainWithIntKey` | `int64` | `FSharpGrainIntHandle<'S,'M>` |

## Usage

In your silo project (`.fsproj`):

```xml
<PackageReference Include="Orleans.FSharp.Abstractions" Version="*" />
```

In your client code (F#):

```fsharp
open Orleans.FSharp

// Get a typed handle — no per-grain interface needed
let counter = FSharpGrain.ref<CounterState, CounterCommand> factory "counter-1"

// Call the grain — fully typed, no box/unbox
let! state = counter |> FSharpGrain.send Increment
```

## Dependency chain

```
Orleans.FSharp.Abstractions (C#)   ← proxy classes generated HERE
        ↑                    ↑
Orleans.FSharp (F#)     Orleans.FSharp.CodeGen (optional, legacy per-grain stubs)
        ↑
Orleans.FSharp.Runtime (F#)        ← references Abstractions transitively
        ↑
Your silo project (F#)             ← gets proxy classes for free
```

No circular dependencies. No code to write.

## Key design decision: no `IRemindable`

`IFSharpGrain` does **not** inherit `IRemindable`. The `FSharpGrain<'S,'M>` class in `Orleans.FSharp.Runtime` implements `IRemindable` directly. This avoids pulling `Microsoft.Orleans.Reminders` source generators into the Abstractions project, which would create the cross-assembly proxy access problem this package is designed to solve.
