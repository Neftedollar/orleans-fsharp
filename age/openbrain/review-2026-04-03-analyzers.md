# Code Review — Analyzers, GrainMock, and FSharpGrainRef

**Date:** 2026-04-04
**Reviewer:** AI review pass (claude-sonnet-4-6)
**Files reviewed:**

1. `src/Orleans.FSharp.Analyzers/AsyncUsageAnalyzer.fs`
2. `tests/Orleans.FSharp.Tests/AnalyzerTests.fs`
3. `docs/analyzers.md`
4. `src/Orleans.FSharp/FSharpGrainRef.fs` (ask/askGuid/askInt at the bottom)
5. `tests/Orleans.FSharp.Tests/GrainMockTests.fs` (withFSharpGrain tests at the bottom)

---

## 1. AsyncUsageAnalyzer.fs

### [CRITICAL] The analyzer itself uses `async { }` — OF0001 fires on its own entry point

**File:** `AsyncUsageAnalyzer.fs`, line 273–290

```fsharp
let asyncUsageAnalyzer (ctx: CliContext) : Async<Message list> =
    async {
        let asyncRanges = AstWalker.collectAsyncRanges ctx.ParseFileResults.ParseTree
        return asyncRanges |> List.map (fun range -> ...)
    }
```

The `FSharp.Analyzers.SDK` requires the analyzer function to return `Async<Message list>`, so `async { }` is technically mandated here by the SDK's public API. The body is also trivial (no `Task` involved). This is exactly the scenario that `[<AllowAsync>]` was designed for, yet the attribute is absent from the function. When the analyzer runs against the `Orleans.FSharp.Analyzers` project itself, OF0001 will fire on line 274 — the ironically named "async usage analyzer" will warn about its own entry point.

**Recommendation:** Add `[<AllowAsync>]` directly above the `asyncUsageAnalyzer` binding, or at minimum add an inline comment explaining why `async { }` is required here. Without it, self-analysis of the project emits a spurious warning that undermines trust in the tool.

---

### [HIGH] `SynExpr.LetOrUse` field count mismatch — pattern may not compile with FCS 43.12

**File:** `AsyncUsageAnalyzer.fs`, line 93

```fsharp
| SynExpr.LetOrUse(_, _, _, _, bindings, body, _, _) ->
```

The comment on line 91 says this has 8 fields: `isRecursive, isUse, isFromSource, isBang, bindings, body, range, trivia`. However the actual FCS 43.12 (shipped with F# 9 / .NET 9) definition of `SynExpr.LetOrUse` has **7** fields: `(isRecursive, isUse, bindings, body, range, trivia)` — the `isFromSource` and `isBang` fields shown in the comment are not part of `LetOrUse` (they belong to `SynBinding` and `SynExpr.LetOrUseBang` respectively). An 8-wildcard pattern on a 6 or 7-field DU case will cause a compile error. This likely means the file currently fails to build, or the field count comment is wrong and the pattern was written for a private/unreleased FCS version. Either way it needs verification against the exact FCS package version actually referenced.

**Recommendation:** Verify the exact field count of `SynExpr.LetOrUse` against the installed `FSharp.Analyzers.SDK` package's FCS transitive dependency and fix the pattern to match.

---

### [HIGH] `SynExpr.LetOrUseBang` is missing from the walker — common async pattern not detected

**File:** `AsyncUsageAnalyzer.fs` — not present in the match

`let!` bindings inside a computation expression desugar to `SynExpr.LetOrUseBang`. When a user writes:

```fsharp
let fetchData () = async {
    let! x = someTask ()
    return x
}
```

The `LetOrUseBang` node wraps both the right-hand side and the continuation body. The current walker **does not recurse into** `SynExpr.LetOrUseBang`, so `async { }` blocks nested inside a `let!` continuation are invisible to it. This is a correctness gap.

The `walkExpr` function has a catch-all `| _ -> ()` at line 179, which silently swallows `LetOrUseBang` and any other unhandled cases. In FCS 43.12 the signature is roughly `SynExpr.LetOrUseBang(spBind, isUse, isFromSource, pat, rhsExpr, andBangBindings, body, range, trivia)`.

**Recommendation:** Add a `SynExpr.LetOrUseBang` arm that walks `rhsExpr` and `body`.

---

### [MEDIUM] `SynExpr.App` — primary detection pattern misses `async<qualifier> { }` style

**File:** `AsyncUsageAnalyzer.fs`, lines 73–75

```fsharp
| SynExpr.App(_, _, SynExpr.Ident id, SynExpr.ComputationExpr(_, body, _), _)
    when id.idText = "async" ->
```

This pattern requires the `async` keyword to be a bare `SynExpr.Ident`. It will miss:

- Qualified forms: `Microsoft.FSharp.Control.async { }` — parses as a `SynExpr.LongIdentSet` or `SynExpr.DotGet` chain, not `SynExpr.Ident`.
- `async` bound to a local alias: `let myAsync = async in myAsync { ... }` — would be missed.

In practice unqualified `async` is by far the most common form in grain code, so this is a low-priority miss, but the qualifier form is a real gap.

**Recommendation:** Add a secondary arm that checks `SynExpr.App(_, _, SynExpr.LongIdent(_, lid, ...), SynExpr.ComputationExpr ...)` where the last ident in `lid` is `"async"`. Document the known limitation regarding aliased forms.

---

### [MEDIUM] `SynExpr.For` — loop variable and start/end expressions are not walked

**File:** `AsyncUsageAnalyzer.fs`, line 150

```fsharp
| SynExpr.For(_, _, _, _, _, _, _, body, _) ->
    walkExpr suppress body
```

The comment says this has 9 fields in FCS 43.12. Only `body` (8th field) is walked. The "from" and "to" expressions (fields 5 and 6) are not walked. If someone writes:

```fsharp
for i in (async { return 0 } |> Async.RunSynchronously) .. 10 do ...
```

the `async` in the range expression is missed. This is an unusual pattern but the walker is otherwise exhaustive about sub-expressions. Consistency is valuable.

**Recommendation:** Walk from-expression and to-expression in addition to body. Check the exact field layout for `SynExpr.For` against FCS docs.

---

### [MEDIUM] `SynExpr.ForEach` — `enumerable` expression not walked

**File:** `AsyncUsageAnalyzer.fs`, line 154

```fsharp
| SynExpr.ForEach(_, _, _, _, _, _, body, _) ->
    walkExpr suppress body
```

The `ForEach` node has a field for the enumerable expression (the thing after `in`). It is not walked. Same concern as `For` — unusual but inconsistent.

---

### [MEDIUM] `SynExpr.ObjExpr` — only `members` are walked, not `argOptions` or `bindings`

**File:** `AsyncUsageAnalyzer.fs`, line 163

```fsharp
| SynExpr.ObjExpr(_, _, _, _, members, _, _, _) ->
    for md in members do walkMemberDef md
```

The ObjExpr node also has a `bindings` field (field 3) and an `extraImpls` field. Interface members on the object expression (`interface IFoo with ...`) sit in `extraImpls`, not in `members`. An `async { }` inside an explicit interface implementation on an object expression will be missed.

---

### [MEDIUM] `SynMemberDefn.AutoProperty` — no XML doc comment on `walkMemberDef`

**File:** `AsyncUsageAnalyzer.fs`, lines 198–209

`walkMemberDef` is an `and` binding in the `internal` module. It has no doc comment. While it is internal, the module-level doc on `AstWalker` says it is `internal` specifically so tests can access it — making good docs valuable.

---

### [LOW] `SynExpr.Record` — `SynExprRecordField` pattern uses 5 wildcards but may be wrong

**File:** `AsyncUsageAnalyzer.fs`, line 168

```fsharp
for SynExprRecordField(_, _, value, _, _) in fields do
```

`SynExprRecordField` in FCS 43.12 has 4 fields: `(fieldName, equalsRange, expr, blockSeparator)`. The 5-field destructure will be a compile error if the FCS version in use has only 4 fields. As with the `LetOrUse` issue this is a "verify against the actual package" item.

---

### [LOW] `SynExpr.YieldOrReturn` and `SynExpr.YieldOrReturnFrom` — 4-field patterns

**File:** `AsyncUsageAnalyzer.fs`, lines 172–178

Both are documented as having 4 fields (flags, expr, range, trivia). This is consistent with recent FCS. Low risk, but worth cross-checking.

---

### [LOW] `SynTypeDefn` pattern binding uses named fields — good, but `extraMembers` is version-sensitive

**File:** `AsyncUsageAnalyzer.fs`, line 213

```fsharp
let walkTypeDefn (SynTypeDefn(typeRepr = repr; members = extraMembers)) : unit =
```

`SynTypeDefn` gained the `members` field in a later FCS version. If this compiles cleanly on the targeted FCS it is fine; just confirm that `members` in `SynTypeDefn` means "extra member definitions after the type body" (augmentation members), which is the intent here.

---

### [LOW] `NestedModule` pattern in `walkModuleDecl` — field count comment absent

**File:** `AsyncUsageAnalyzer.fs`, line 228

```fsharp
| SynModuleDecl.NestedModule(_, _, decls, _, _, _) ->
```

No comment on which field is `decls`. This is fine for experienced FCS readers but adds to maintenance burden. A brief label comment would help.

---

### [LOW] `AllowAsyncAttribute` targets `Method` and `Property` but not `Function`

**File:** `AsyncUsageAnalyzer.fs`, line 30

```fsharp
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property, AllowMultiple = false)>]
```

F# module-level `let` bindings compile to static methods, so `AttributeTargets.Method` does cover them. However, in F# source `[<AllowAsync>]` above a `let` binding feels odd if the attribute's documented targets are `Method` and `Property`. `AttributeTargets.All` or adding a note in the XML doc ("module-level `let` bindings compile as `Method`") would reduce confusion for users.

---

## 2. AnalyzerTests.fs

### [HIGH] `parseSource` uses `Async.RunSynchronously` — banned by project constitution

**File:** `AnalyzerTests.fs`, line 35

```fsharp
let parseResult =
    checker.ParseFile("dummy.fs", sourceText, parsingOptions)
    |> Async.RunSynchronously
parseResult.ParseTree
```

The `FSharpChecker.ParseFile` returns `Async<FSharpParseFileResults>`. The test helper converts it with `Async.RunSynchronously`. The project constitution bans `async { }` in production code, and CLAUDE.md says `Async.RunSynchronously` in tests should use `[<AllowAsync>]`. The function itself is not an `async { }` block, so OF0001 will not fire — but the use of `Async.RunSynchronously` is exactly the kind of brittle synchronous blocking the policy exists to discourage.

More importantly: the test file **does not** apply `[<AllowAsync>]` to `parseSource`, yet the policy in `docs/analyzers.md` ("Unit tests calling `Async.RunSynchronously` (use `[<AllowAsync>]` on the test helper)") says it should. This is an inconsistency in the documentation's own example.

**Recommendation:** Either rewrite `parseSource` using `task { }` (requires `Async.StartAsTask` or an F# Task interop) or add `[<AllowAsync>]` above the binding, consistent with the documented guidance.

---

### [MEDIUM] Test isolation — all tests share a single `FSharpChecker.Create()` call per invocation

**File:** `AnalyzerTests.fs`, line 26

Each call to `asyncCount` creates a new `FSharpChecker`. `FSharpChecker` is a heavyweight object backed by a background compilation service. Creating one per test call (and especially one per FsCheck sample) is correct for isolation but incurs significant startup overhead. For the current small suite this is fine, but under FsCheck with default 100 samples this creates 200+ checkers.

This is not a correctness bug, but it does make the test suite slower than it needs to be. A module-level `let checker = FSharpChecker.Create()` shared across tests would be safe because `ParseFile` is thread-safe.

**Recommendation:** Extract `FSharpChecker` to a module-level value. Mark as LOW priority until test run times become an issue.

---

### [MEDIUM] Missing test: `async { }` nested inside `ObjExpr` interface implementation

**File:** `AnalyzerTests.fs` — not present

The walker's `ObjExpr` arm does not traverse `extraImpls` (interface implementations). There is no test verifying that:

```fsharp
let obj = { new IDisposable with member _.Dispose() = async { return () } |> ignore }
```

is detected. Because there is no test, the gap in the walker (issue MEDIUM above) is unverified.

---

### [MEDIUM] Missing test: `async { }` inside `let!` binding (LetOrUseBang)

**File:** `AnalyzerTests.fs` — not present

As noted in the walker review, `SynExpr.LetOrUseBang` is not walked. There is no test for the pattern:

```fsharp
let f () = task {
    let! x = async { return 1 } |> Async.StartAsTask
    return x
}
```

This pattern is exactly the kind of mixed async/task code the analyzer is meant to catch.

---

### [MEDIUM] Missing test: nested `async { }` inside `for` / `while` body

**File:** `AnalyzerTests.fs` — not present

The walker handles `For`, `ForEach`, and `While` but there are no tests exercising these.

---

### [LOW] `FSharpChecker.GetParsingOptionsFromCommandLineArgs` deprecation risk

**File:** `AnalyzerTests.fs`, line 29

This API exists in the current FCS version but the preferred API path for parsing snippets is `FSharpChecker.GetParsingOptionsFromProjectOptions`. The current form is not broken, just worth monitoring across FCS upgrades.

---

### [LOW] No negative test for `AllowAsync` on a class member (vs. module-level binding)

**File:** `AnalyzerTests.fs` — not present

The `AllowAsync` attribute is described as applying to methods and properties. There is no test verifying that `[<AllowAsync>]` on a class `member` correctly suppresses detection:

```fsharp
type MyGrain() =
    [<AllowAsync>]
    member _.Fetch() = async { return 1 }
```

The current test suite only exercises module-level `let` bindings with the attribute.

---

## 3. docs/analyzers.md

### [LOW] Help URI in the analyzer uses a non-Anthropic GitHub username

**File:** `AsyncUsageAnalyzer.fs`, line 256, and indirectly `docs/analyzers.md`

```fsharp
let private HelpUri =
    "https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/analyzers.md#OF0001"
```

The URI embeds the username `Neftedollar`. If the project repository URL is different, or if the package is published under an organization, this URL will 404 when an IDE shows the diagnostic. Verify this is the canonical repository URL for publication.

---

### [LOW] `docs/analyzers.md` anchor `#OF0001` will not resolve correctly

**File:** `docs/analyzers.md`, line 15

The heading is `## OF0001 — Use \`task { }\` instead of \`async { }\`` which GitHub renders as the anchor `#of0001----use-task---instead-of-async--`. The URI in the analyzer code uses `#OF0001` (uppercase, no spaces). The deep link will silently fail to scroll to the section in most Markdown renderers.

**Recommendation:** Either rename the heading to simply `## OF0001` so the anchor is `#of0001`, or update the URI to use the correct rendered anchor.

---

### [LOW] Missing `fsharp-analyzers` version requirement in docs

**File:** `docs/analyzers.md`, lines 79–86

The CLI usage section does not specify a minimum required version of `fsharp-analyzers`. The tool version constrains which FCS version is used for parsing, which must match the FCS version the analyzer DLL was built against. A version mismatch causes silent failures (no diagnostics, no errors).

---

## 4. FSharpGrainRef.fs — `ask` / `askGuid` / `askInt`

### [MEDIUM] `ask`, `askGuid`, `askInt` — `InvalidCastException` on wrong `'Result` type is user-hostile

**File:** `FSharpGrainRef.fs`, lines 203–239

All three functions do:

```fsharp
let! result = handle.Grain.HandleMessage(box cmd)
return result :?> 'Result
```

The `result` is the `Result` field of `GrainDispatchResult` returned from `FSharpGrainImpl.HandleMessage` (see `IFSharpGrainInterfaces.cs` line 143). Looking at the C# impl:

```csharp
return dispatch.Result ?? (object)Unit.Default;
```

If the handler returns `None`/`null` for the result, the C# code substitutes an internal `Unit.Default` value. The F# caller will then attempt `Unit.Default :?> 'Result`, which will throw `InvalidCastException` unless `'Result` is `obj`. The XML doc at line 200 mentions `InvalidCastException`, but there is no guidance on the `Unit.Default` edge case or how to avoid it.

There is also no protection against `'Result` being a value type: if `'Result = int`, then `result :?> int` where `result` is a boxed `int` works correctly, but if `result` is `null` (which `dispatch.Result` could be for a handler that returns `null`), the downcast throws `NullReferenceException` rather than `InvalidCastException`. The distinction matters for callers writing error-handling code.

**Recommendation:** Add a note to the XML doc explaining that `ask` requires the handler to return a non-null, correctly-typed result. Consider adding an `Option`-returning variant (`tryAsk`) that returns `'Result option` and catches the downcast failure.

---

### [LOW] `ask` / `askGuid` / `askInt` — no `postGuid` / `postInt` equivalents in the XML doc summary

**File:** `FSharpGrainRef.fs`, module-level XML doc, lines 48–64

The module-level remarks block mentions `send`, `post`, and `ask` but the bulleted list under `<remarks>` does not mention `askGuid` or `askInt` variants. This is a documentation completeness gap.

---

### [LOW] `send` vs `ask` behavioral difference is not documented at the type level

**File:** `FSharpGrainRef.fs`, lines 111–115 vs 203–207

`send` returns the state by calling `result :?> 'State`, while `ask` returns a separate result type `'Result`. Both make the same `HandleMessage` call. The documentation correctly describes the difference in the module remarks, but the individual function XML docs do not cross-reference each other (`send` does not say "see also `ask`" and vice versa). Users who find `send` first may not discover `ask`.

---

## 5. GrainMockTests.fs — `withFSharpGrain` tests

### [HIGH] `withFSharpGrainInt` test has a spurious and semantically meaningless line

**File:** `GrainMockTests.fs`, line 391

```fsharp
let! _ = s1.Total |> ignore |> Task.FromResult
```

This line:
1. Takes `s1.Total : int64`
2. Calls `ignore`, returning `unit`
3. Calls `Task.FromResult<unit>`, returning `Task<unit>`
4. Awaits it with `let! _`, discarding the result

This does nothing except spin up an unnecessary `Task<unit>`. The intent was presumably just to reference `s1` to avoid an unused-variable warning, but `let _ = s1` or `s1 |> ignore` would accomplish that without allocating a Task. As written, `s1` is effectively never checked — the test only asserts `s2.Total = 15L`, which only verifies the second `AddAmount` cumulates correctly. The test **does not** verify `s1.Total = 10L`.

This is both a code smell and a missing assertion: if the first `sendInt` returns a wrong value, the test still passes.

**Recommendation:** Replace with `test <@ s1.Total = 10L @>` to cover the first call's result, and remove the spurious `Task.FromResult` line.

---

### [MEDIUM] `withFSharpGrain` mock is not thread-safe — `mutable state` with no synchronization

**File:** `GrainMock.fs`, lines 148–166 (all three withFSharpGrain variants)

The mock grain uses a mutable captured variable (`let mutable state = ...`) to track state between calls. The `FsCheck` property test at `GrainMockTests.fs` line 413 calls `FSharpGrain.post` in a `for` loop — sequentially via `GetAwaiter().GetResult()`, so it is safe in practice. However, if any caller awaits multiple sends concurrently (`Task.WhenAll [send1; send2; send3]`), the mutable state is a data race.

This is acceptable for a test double (real grains are single-threaded), but should be noted in the XML doc or with a `[<ThreadUnsafe>]`-style comment so users don't assume mock concurrency safety.

---

### [MEDIUM] No test for the `withFSharpGrain` error path: handler returns wrong type

**File:** `GrainMockTests.fs` — not present

If a caller uses `ask<'S,'C,'Result>` and the handler boxes a type other than `'Result`, the mock will throw `InvalidCastException` inside the `task { }`. There is no test asserting that this exception surfaces properly (e.g., via `AggregateException` when awaited). This validates the unhappy-path behavior.

---

### [MEDIUM] No test for `withFSharpGrain` with a definition that has no handler

**File:** `GrainMockTests.fs` — not present

`GrainMock.withFSharpGrain` checks `def.DefaultState` and raises `failwith` if absent. It also checks `def.Handler` at dispatch time. There is no test verifying the error message when `def.Handler = None` (i.e., a grain definition built with only `defaultState` and no `handle` call). The error path in `GrainMock.fs` line 158–160 is untested.

---

### [LOW] `FsCheck` property test key collisions — unlikely but possible

**File:** `GrainMockTests.fs`, line 426

```fsharp
let key = $"fscheck-ping-{n.Get}"
```

Each FsCheck sample creates a fresh `MockGrainFactory`. Keys cannot collide within a single property run. However, in the ask property at line 451:

```fsharp
let key = $"fscheck-ask-{a.Get}-{b.Get}"
```

If FsCheck generates `a=1, b=2` and `a=12, b` (where `b` is empty), the keys `"fscheck-ask-1-2"` and `"fscheck-ask-12-"` are distinct. No collision risk here either. This is a non-issue, noted for completeness.

---

### [LOW] `withFSharpGrainGuid` — `MockGetCount` case not tested in the GUID variant test

**File:** `GrainMockTests.fs`, lines 344–365

The GUID mock test only exercises `SetName`. It does not call `GetName` to confirm the state was correctly stored. Compare this with the string mock test which validates state via `MockGetCount`. Parity would be cleaner.

---

### [LOW] `withFSharpGrainInt` test — only one key (42L) is exercised

**File:** `GrainMockTests.fs`, lines 370–393

Only `42L` is tested. An FsCheck property for the int-keyed variant (similar to the string ping property) would increase confidence.

---

## Summary Table

| Severity | Count | Key items |
|----------|-------|-----------|
| CRITICAL | 1 | Analyzer warns on its own entry point; `[<AllowAsync>]` missing from `asyncUsageAnalyzer` |
| HIGH | 3 | `LetOrUse` field count mismatch (compile risk), `LetOrUseBang` not walked, spurious `Task.FromResult` in test |
| MEDIUM | 10 | Missing test coverage (LetOrUseBang, ObjExpr, loops), `Async.RunSynchronously` in test helper, `ask` null/Unit.Default edge case, mock thread-safety undocumented, missing unhappy-path tests |
| LOW | 10 | Doc anchor broken, `HelpUri` username, AllowAsync target confusion, missing cross-refs, minor field-count comments |

---

## Priority Order for Fixes

1. **CRITICAL** — Add `[<AllowAsync>]` to `asyncUsageAnalyzer` (1-line fix).
2. **HIGH** — Verify `SynExpr.LetOrUse` and `SynExprRecordField` field counts against the installed FCS version; fix patterns if mismatched.
3. **HIGH** — Add `SynExpr.LetOrUseBang` arm to `walkExpr` + corresponding test.
4. **HIGH** — Fix spurious `Task.FromResult` line in `withFSharpGrainInt` test and add the missing `s1.Total = 10L` assertion.
5. **MEDIUM** — Add tests for `ObjExpr` interface member detection, loop bodies, and `LetOrUseBang`.
6. **MEDIUM** — Fix `docs/analyzers.md` anchor `#OF0001` to match what GitHub actually renders.
7. **MEDIUM** — Add unhappy-path tests for the mock (wrong type, no handler).
8. **LOW** — Remaining documentation and minor consistency items.
