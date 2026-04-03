---
title: Serialization
description: Three serialization modes — F# Binary (recommended), JSON fallback, and Orleans Native for C# interop
---

# Serialization

Orleans.FSharp offers three serialization modes. Choose based on your needs — you can switch at any time.

## Quick Comparison

| Mode | CE Keyword | Speed | C# Project Needed? | Attributes? | Best For |
|------|-----------|-------|-------------------|-------------|----------|
| **F# Binary** | `useFSharpBinarySerialization` | Fast | No | None | Pure F# clusters (recommended) |
| **JSON** | `useJsonFallbackSerialization` | Good | No | None | Prototyping, schema flexibility |
| **Orleans Native** | *(default)* | Fastest | Yes (CodeGen) | `[<GenerateSerializer>]` + `[<Id>]` | Mixed F#/C# clusters |

## Mode 1: F# Binary (Recommended)

Binary serialization using FSharp.Reflection — fast, compact, zero boilerplate.

```fsharp
// Your types — plain F#, no attributes
type OrderState =
    | Created of orderId: string
    | Paid of amount: decimal
    | Shipped of trackingNo: string
    | Delivered
    | Cancelled of reason: string

type OrderCommand = Place of string | Confirm | Ship of string | Cancel of string | GetStatus

// Your grain — clean
let orderGrain = grain {
    defaultState (Created "")
    handle (fun state cmd -> task { ... })
    persist "Default"
}

// Enable in silo config
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    useFSharpBinarySerialization  // ← this is all you need
}
```

**How it works:** The `FSharpBinaryCodecProvider` inspects F# types at runtime via `FSharp.Reflection`, builds binary reader/writer functions, and **caches them per type** in a `ConcurrentDictionary`. First access pays the reflection cost (~1ms); subsequent calls are a dictionary lookup (~20ns).

**Supported types:**
- Discriminated unions (any nesting depth)
- Records
- Options and ValueOptions
- Lists, arrays, sets, maps
- Tuples
- All .NET primitives (int, string, float, decimal, Guid, DateTime, TimeSpan, etc.)
- Byte arrays
- Any nested combination of the above

**When to use:** Pure F# Orleans clusters. This is the recommended mode for new projects.

## Mode 2: JSON Fallback

JSON serialization via FSharp.SystemTextJson — human-readable, flexible schema evolution.

```fsharp
// Same clean types — no attributes
type CounterState = { Count: int }
type CounterCommand = Increment | Decrement | GetValue

let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    useJsonFallbackSerialization
}
```

**Pros:**
- Human-readable payload (useful for debugging)
- Name-based schema evolution (add/remove fields by name, not ordinal)
- Broad ecosystem compatibility

**Cons:**
- ~2-5x slower than binary modes
- Larger payload size (text vs binary)
- `float Infinity`, `NaN` not supported (IEEE 754 limitation of JSON)
- `option option` — `Some None` serializes as `null`, deserializes as `None` (known limitation)

**When to use:** Prototyping, debugging, or when you need flexible schema evolution.

## Mode 3: Orleans Native

Orleans built-in source-generated serialization — maximum performance, required for C# interop.

```fsharp
// Types need Orleans attributes
[<GenerateSerializer>]
type CounterState =
    | [<Id(0u)>] Zero
    | [<Id(1u)>] Count of int

[<GenerateSerializer>]
type CounterCommand =
    | [<Id(0u)>] Increment
    | [<Id(1u)>] Decrement
    | [<Id(2u)>] GetValue

// No serialization keyword needed — it's the default
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}
```

**Requirements:**
- `[<GenerateSerializer>]` attribute on every type crossing grain boundaries
- `[<Id(n)>]` attribute on every DU case and record field (ordinal position)
- A **C# CodeGen project** (`Orleans.FSharp.CodeGen`) that references your F# types — Orleans Roslyn source generators only work on C# projects
- A C# grain class per grain definition (inherits `Grain`, delegates to F# handler)

**Why so much boilerplate?** Orleans uses Roslyn source generators to produce optimized binary serializers at compile time. Roslyn does not support F# — hence the C# bridge project.

### When You MUST Use Orleans Native

**Mixed F#/C# clusters.** If your Orleans cluster has both F# silos (using Orleans.FSharp) and C# silos (using standard Orleans), they need to agree on serialization format. Orleans Native is the common format both understand.

```
F# Silo ←→ C# Silo     → Orleans Native (both understand [GenerateSerializer])
F# Silo ←→ F# Silo     → F# Binary (recommended) or JSON
F# Silo only            → F# Binary (recommended)
```

**Migrating from C# to F#.** If you're gradually moving C# grains to F#, start with Orleans Native for compatibility. Once all silos are F#, switch to F# Binary.

**Korat pattern (C# core + F# new grains).** Existing C# grains keep Orleans Native serialization. New F# grains can use F# Binary — they have separate state types that don't cross the C#/F# boundary.

### Setting Up CodeGen (Orleans Native only)

1. Create a C# class library project:

```bash
dotnet new classlib -lang C# -n MyApp.CodeGen
dotnet add MyApp.CodeGen package Microsoft.Orleans.Sdk
dotnet add MyApp.CodeGen reference ../MyApp.Grains/MyApp.Grains.fsproj
```

2. Add the assembly attribute:

```csharp
// AssemblyAttributes.cs
using Orleans;
[assembly: GenerateCodeForDeclaringAssembly(typeof(MyApp.Grains.SomeType))]
```

3. For each F# grain, create a C# grain class:

```csharp
// CounterGrainImpl.cs
[GenerateSerializer]
public class CounterGrainImpl : Grain, ICounterGrain
{
    private readonly GrainDefinition<CounterState, CounterCommand> _def;
    // ... constructor, HandleMessage delegation to F# handler
}
```

4. Reference the CodeGen project from your Silo project.

## Mixing Modes

You can use multiple modes in the same silo. Orleans resolves serializers in priority order:

1. Orleans Native (types with `[GenerateSerializer]`) — highest priority
2. F# Binary / JSON (fallback for types without attributes)

This means you can use Orleans Native for shared C#/F# types and F# Binary for F#-only types:

```fsharp
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    useFSharpBinarySerialization  // fallback for F#-only types
    // Orleans Native types still work via [GenerateSerializer]
}
```

## Schema Evolution

| Scenario | JSON | F# Binary | Orleans Native |
|----------|------|-----------|---------------|
| Add DU case at end | Works | Works | Works (with new `[Id]`) |
| Remove DU case | Old data with removed case fails | Same | Same |
| Add record field | **Fails** (FSharp.SystemTextJson strict) | **Fails** (ordinal-based) | **Fails** (ordinal-based) |
| Rename DU case | Fails (name-based) | Works (ordinal-based) | Works (ordinal-based) |

For schema migrations across versions, use the [StateMigration](advanced.md) module.

## Performance

Measured over 10,000 roundtrips of a typical DU with 5 cases:

| Mode | Time | Payload Size | Relative Speed |
|------|------|-------------|----------------|
| Orleans Native | ~1ms | Smallest | 1x (baseline) |
| F# Binary | ~2ms | Small | ~2x |
| JSON | ~5ms | Large (text) | ~5x |

All modes are fast enough for real-world Orleans usage. Grain call network latency (~100-500μs) dominates serialization time.

## Next Steps

- [Getting Started](getting-started.md) — build your first grain
- [Grain Definition](grain-definition.md) — all 31 CE keywords
- [Advanced](advanced.md) — state migration for schema evolution
- [Testing](testing.md) — FsCheck property tests for serialization roundtrips
