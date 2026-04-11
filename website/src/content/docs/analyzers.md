---
title: "Orleans.FSharp Analyzers"
description: "Compile-time F# analyzer: OF0001 warns on async usage with AllowAsync opt-out."
---

# Orleans.FSharp Analyzers

**Compile-time feedback for idiomatic Orleans grain code.**

## Installation

```bash
dotnet add package Orleans.FSharp.Analyzers
```

> **Note**: F# analyzers surface warnings in editors that support Ionide (VS Code + Ionide extension, JetBrains Rider). They also run via the `fsharp-analyzers` CLI tool.

---

## OF0001 — Use `task { }` instead of `async { }`

### Description

`async { }` computation expressions are **banned in Orleans.FSharp grain code**. Orleans is built on .NET `Task`-based concurrency. Using `async { }` requires an unnecessary `Async.AwaitTask` / `Async.StartAsTask` conversion at every grain boundary, adds overhead, and is incompatible with Orleans's `CancellationToken` propagation model.

Use `task { }` (from `IcedTasks` or .NET 6+) instead. It compiles directly to a `Task<'T>` and is fully compatible with Orleans grain handlers.

### Example (warning)

```fsharp
// ⚠️ OF0001: Use task { } instead of async { }
let handler state cmd =
    async {                     // <-- warning here
        match cmd with
        | Increment -> return { Count = state.Count + 1 }, box (state.Count + 1)
        | GetValue  -> return state, box state.Count
    }
```

### Fix

```fsharp
// ✅ Correct
let handler state cmd =
    task {
        match cmd with
        | Increment -> return { Count = state.Count + 1 }, box (state.Count + 1)
        | GetValue  -> return state, box state.Count
    }
```

### Suppression with `[<AllowAsync>]`

When `async { }` is genuinely required — for example, interoperating with a library that returns `Async<'T>` — suppress OF0001 on the specific binding:

```fsharp
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer

[<AllowAsync>]
let fetchFromLegacyApi (url: string) : Async<string> =
    async {
        let! result = legacyHttpClient.GetAsync(url) |> Async.AwaitTask
        return! result.Content.ReadAsStringAsync() |> Async.AwaitTask
    }
```

`[<AllowAsync>]` suppresses the warning **only for the annotated binding**. Other `async { }` usages in the same module are still flagged.

### When `async { }` is acceptable

- Interop adapters that bridge `Async<'T>` APIs to `Task<'T>` callers (use `[<AllowAsync>]`)
- Script files (`.fsx`) that use the F# `Async` workflow (use `[<AllowAsync>]`)
- Unit tests calling `Async.RunSynchronously` (use `[<AllowAsync>]` on the test helper)

In all other cases, use `task { }`.

---

## Running analyzers from the CLI

Install the analyzer CLI tool:

```bash
dotnet tool install --global fsharp-analyzers
```

Run against your project:

```bash
fsharp-analyzers --project MyGrains.fsproj --analyzers-path <path-to-Orleans.FSharp.Analyzers.dll>
```

---

## Analyzer list

| Code | Severity | Description |
|------|----------|-------------|
| [OF0001](#OF0001) | Warning | `async { }` should be `task { }` |

---

## See also

- [Getting Started](getting-started.md) — quick introduction to Orleans.FSharp
- [Grain Definition](grain-definition.md) — complete `grain { }` CE reference
- [Advanced](advanced.md) — transactions, telemetry, shutdown, migration
