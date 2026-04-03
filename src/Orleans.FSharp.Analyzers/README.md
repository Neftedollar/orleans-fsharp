# Orleans.FSharp.Analyzers

F# code analyzer that detects `async {}` computation expression usage and recommends `task {}` instead.

## Why

Orleans is `Task`-native. Using `async {}` in grain code introduces unnecessary overhead from `Async.StartAsTask` conversions and can cause deadlocks on the grain's single-threaded scheduler. This analyzer catches `async {}` usage at edit-time so you can fix it before it reaches production.

## Diagnostic

| Code | Severity | Message |
|------|----------|---------|
| **OF0001** | Warning | Use `task {}` instead of `async {}` for Orleans compatibility. Orleans is Task-native; async introduces overhead and potential deadlocks. |

## Opt-out

Apply `[<AllowAsync>]` to a method or property to suppress the diagnostic for that binding:

```fsharp
open Orleans.FSharp.Analyzers

[<AllowAsync>]
let legacyOperation () = async {
    // OF0001 suppressed here
    return! someAsyncLib ()
}
```

## Installation

This analyzer is distributed via `FSharp.Analyzers.SDK`. To enable it:

1. Install `Orleans.FSharp.Analyzers` as a NuGet reference.
2. Configure your editor or build to load analyzers from the SDK path.

Refer to the [FSharp.Analyzers.SDK docs](https://ionide.io/FSharp.Analyzers.SDK/) for editor integration details.

## How it works

The analyzer walks the untyped F# AST (`ParsedInput`) looking for `SynExpr.App` nodes where the function is `SynExpr.Ident("async")` applied to a `SynExpr.ComputationExpr`. Bindings decorated with `[<AllowAsync>]` or `[<AllowAsyncAttribute>]` are skipped.

## License

MIT
