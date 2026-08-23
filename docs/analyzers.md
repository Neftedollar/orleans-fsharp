# Orleans.FSharp Analyzers

**Compile-time feedback for idiomatic Orleans grain code.**

## Installation

```bash
dotnet add package Orleans.FSharp.Analyzers
```

> **Note**: F# analyzers surface warnings in editors that support Ionide (VS Code + Ionide extension, JetBrains Rider). They also run via the `fsharp-analyzers` CLI tool.

---

<a id="of0001"></a>

## OF0001 — Use `task { }` instead of `async { }`

### Description

`async { }` computation expressions are **banned in Orleans.FSharp grain code**. Orleans is built on .NET `Task`-based concurrency. Using `async { }` requires an unnecessary `Async.AwaitTask` / `Async.StartAsTask` conversion at every grain boundary, adds overhead, and is incompatible with Orleans's `CancellationToken` propagation model.

Use `task { }` instead — the computation expression FSharp.Core has shipped since F# 6 (`IcedTasks` adds cancellable and resumable variants on top of it). It compiles directly to a `Task<'T>` and is fully compatible with Orleans grain handlers.

### Example (warning)

```fsharp
type CounterState = { count: int }

// ⚠️ OF0001: Use task { } instead of async { }
let handler (state: CounterState) () =
    async {                     // warning here
        let next = { count = state.count + 1 }
        return next, next.count
    }
```

### Fix

```fsharp
type CounterState = { count: int }

// ✅ Correct
let handler (state: CounterState) () =
    task {
        let next = { count = state.count + 1 }
        return next, next.count
    }
```

### Suppression with `[<AllowAsync>]`

When `async { }` is genuinely required — for example, interoperating with a library that returns `Async<'T>` — suppress OF0001 on the specific binding:

```fsharp
open System.Net.Http
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer

[<AllowAsync>]
let fetchFromLegacyApi (url: string) : Async<string> =
    async {
        use client = new HttpClient()
        return! client.GetStringAsync(url) |> Async.AwaitTask
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
| [OF0001](#of0001) | Warning | `async { }` should be `task { }` |

---

## See also

- [Getting Started](getting-started.md) — quick introduction to Orleans.FSharp
- [Legacy API](legacy/index.md) — maintenance documentation for earlier authoring models
- [Advanced](advanced.md) — transactions, OpenTelemetry, shutdown, migration
