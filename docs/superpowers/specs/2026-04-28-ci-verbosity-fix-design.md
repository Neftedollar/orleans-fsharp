# Design: Restore green CI on PRs #6 and #7

**Date**: 2026-04-28
**Status**: approved (brainstorming → ready for writing-plans)
**Author**: Roman Melnikov

## Background

Two recently opened PRs deprecate non-functional concurrency CE keywords on `GrainDefinition`:

- **#6** — class-level: `reentrant`, `statelessWorker`, `maxActivations`, `mayInterleave`
- **#7** — per-method: `interleave`, `oneWay`, `readOnly`

Both PRs fail the `build-and-test` GitHub Actions job at the `dotnet build --verbosity quiet` step. The job's `dotnet test` step never executes (skipped). Both PRs are marked `mergeable=true` by GitHub but cannot be merged because CI is red.

Local builds on macOS succeed, including with the exact `dotnet build --verbosity quiet` command CI uses.

## Problem

CI invokes `dotnet build --verbosity quiet`. Inside the build, `Orleans.FSharp.CodeGen.csproj` runs the F# code generator via an MSBuild `<Exec>` task at line 42. The generator process exits with code 1, but `--verbosity quiet` suppresses its stdout/stderr, so the actual error message never reaches the CI log. We see only the MSBuild wrapper message:

```
error MSB3073: ... dotnet run --project Generator ... exited with code 1.
```

Without the generator's actual error output, we cannot identify the root cause. Hypotheses include the em-dash `—` in `[<Obsolete>]` strings interacting badly with Ubuntu's default locale, path-case differences, or other Linux-specific runtime behavior — but each of these is a guess.

## Decision

Diagnostic-first approach in a separate, scoped CI-fix PR. Three PRs total, sequenced.

## Sequence

```
main ──┬─→ PR-A (fix/ci-verbosity-normal) ──→ merge ──┐
       │                                              │
       └─→ #6, #7 unchanged ──── rebase on new main ──┴─→ CI shows real error
                                                          ↓
                                                       fix root cause in #6/#7
                                                          ↓
                                                       merge #6, #7
```

## PR-A — CI verbosity fix

**Branch**: `fix/ci-verbosity-normal` off main
**Files changed**: `.github/workflows/ci.yml` only

**Edits**:
- `dotnet build --verbosity quiet` → `dotnet build --verbosity normal`
- `dotnet test tests/Orleans.FSharp.Tests --verbosity quiet` → `dotnet test tests/Orleans.FSharp.Tests --verbosity normal`

**Why `normal` and not `minimal`**: `minimal` does not include stdout from MSBuild `<Exec>` tasks. Our generator runs via `<Exec>` in `src/Orleans.FSharp.CodeGen/Orleans.FSharp.CodeGen.csproj:42`. `normal` is the dotnet default and forwards `<Exec>` stdout/stderr.

**Commit message**: `fix(ci): use --verbosity normal so MSBuild Exec stdout is visible`

**PR body** references #6 and #7 as blocked debugging cases that motivate the change. Notes that this is a permanent quality improvement, not a temporary diagnostic.

**Acceptance**: PR-A's own CI run shows `build-and-test: success` (it should — verbosity bump alone introduces no compilation change). Then merge to main.

## Phase 2 — Rebase #6 and #7

After PR-A is merged:

```
git checkout feat/deprecate-class-level-concurrency
git rebase main
git push --force-with-lease

git checkout feat/deprecate-per-method-concurrency
git rebase main
git push --force-with-lease
```

CI re-runs on each PR. The build still fails (we have not changed the deprecation code yet), but now the generator's stderr is in the log.

## Phase 3 — Diagnose and fix

Read the actual generator error from CI. Form a hypothesis based on real evidence (not guesswork). Three most likely candidates, in priority order:

1. **em-dash `—` in `[<Obsolete>]` strings** — replace with ASCII `--` in both PRs (single-character change in the deprecation messages added by #6 and #7)
2. **Path/case sensitivity** in `Discovery.fs` or generator template — patch as appropriate
3. **Other** — react to actual evidence

Push the fix as a follow-up commit to #6 and #7. Re-run CI.

## Phase 4 — Verify and merge

- CI on #6 must show `build-and-test: success`
- The `dotnet test` step must execute (not skipped) and report a non-zero passing count
- Same for #7
- Only after both PRs are green are they merge-ready

## Out of scope

Deliberately not addressed in this cycle:

- Replacement of `"Tracking issue: TBD"` placeholder text in the `[<Obsolete>]` messages on #6 and #7. Will be done when a real GitHub issue is filed for the longer-term architectural fix (per-grain stub generator for regular F# grains).
- Cleanup of sample grains in `src/Orleans.FSharp.Sample/` that demonstrate the non-functional CE keywords. They are flagged via `#nowarn "44"` in #6; rewriting them as a teaching tool for working alternatives is a separate refactor PR.
- Pre-existing `Static Analysis (Semgrep)` and `Secrets Detection` job failures on main. These are infrastructure issues (broken external action SHA reference) unrelated to the deprecation work.

## Risks

- **Phase 3 hypothesis is wrong twice in a row**. Mitigation: each diagnostic CI run after Phase 1 has full generator output, so we are no longer guessing — we are reading the error message. If the first fix attempt does not match, the next CI run will tell us why directly.
- **Rebase conflicts on #6 / #7 after PR-A merges**. Unlikely — PR-A only touches `.github/workflows/ci.yml`, which neither #6 nor #7 modifies. If a conflict arises, it is mechanical to resolve.

## Success criteria

1. PR-A merged into main with green `build-and-test`.
2. After rebase, both #6 and #7 surface the generator's actual error message in their CI logs.
3. After fix, both #6 and #7 show `build-and-test: success` with `dotnet test` running and reporting passing tests.
4. The `--verbosity normal` change persists on main as a permanent diagnostic improvement.
