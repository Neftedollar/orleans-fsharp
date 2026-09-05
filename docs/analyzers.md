# Orleans.FSharp Analyzers

An opt-in F# CLI analyzer for task-based grain code.

## Installation

```bash
dotnet add package Orleans.FSharp.Analyzers
```

Keep the package's normal library assets: they expose the public `AllowAsync` suppression
attribute. Set `PrivateAssets="all"` and `GeneratePathProperty="true"` on its
`PackageReference`, and pin the package version you selected. Do not restrict
`IncludeAssets` to `analyzers`: this F# SDK plugin is distributed under `lib/net8.0`.

The supported host is `fsharp-analyzers` 0.37.2. This package currently exports a
`CliAnalyzer`, not an `EditorAnalyzer` or a Roslyn compiler analyzer. Package installation
alone does **not** run OF0001 during `dotnet build` or in an editor; run the CLI explicitly.

---

<a id="of0001"></a>

## OF0001 — Use `task { }` instead of `async { }`

### Description

OF0001 is an opt-in style warning for unsuppressed `async { }` expressions in the selected
files. It scans syntax; it does not infer whether a binding is a grain handler. Orleans grain
handlers return `Task<_>`, so `task { }` is the direct fit. Intentional Async interop is valid
when its Task boundary and cancellation are handled explicitly; suppress that binding below.

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

Apply this rule to the files where you want the task-based style convention enforced.

---

## Running analyzers from the CLI

Install the analyzer CLI tool:

```bash
dotnet tool install --global fsharp-analyzers --version 0.37.2
```

Run against your project:

```bash
dotnet restore MyGrains.fsproj
analyzer_package="$(dotnet msbuild MyGrains.fsproj -nologo -getProperty:PkgOrleans_FSharp_Analyzers)"
fsharp-analyzers --project MyGrains.fsproj --analyzers-path "$analyzer_package/lib/net8.0"
```

---

The path is a directory containing the packed plugin. Keep the CLI and analyzer SDK versions
aligned; arbitrary editor/runner versions are not certified by the package smoke test. See the
[F# SDK packaging model](https://ionide.io/FSharp.Analyzers.SDK/content/Getting%20Started%20Writing.html#Packaging-and-Distribution).

## Analyzer list

| Code | Severity | Description |
|------|----------|-------------|
| [OF0001](#of0001) | Warning | `async { }` should be `task { }` |

---

## See also

- [Getting Started](getting-started.md) — quick introduction to Orleans.FSharp
- [Legacy archive](legacy/index.md) — unsupported migration reference for earlier authoring models
- [Advanced](advanced.md) — transactions, OpenTelemetry, shutdown, migration
