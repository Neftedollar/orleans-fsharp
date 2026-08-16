# Implementation Plan — Spec 003: Functional Grain Runtime

**Spec (binding authority):** [spec.md](./spec.md)
**Branch:** `feat/003-functional-grain-runtime`
**Execution mode:** dynamic Workflow orchestration. Per task: one implementer
agent (model by complexity), parallel spec-compliance + quality reviewers,
scoped fix rounds (cap 3 per workflow run), then controller (Fable) acceptance
review before the next task starts. Final whole-branch review by controller.

## Global constraints

- Orleans **10.1.0 is the floor** (`Directory.Packages.props` `OrleansVersion`);
  CI matrix additionally builds/tests with `-p:OrleansVersion=10.2.2`. Every
  task's exit tests must pass on both.
- The spec's **normative `.fsi` sketch fixes public names and shapes** — no
  deviation without a recorded ruling in the ledger.
- Known 10.1.0→10.2.2 drift (verified 2026-08-16 by tag-tree diff):
  `StateStorageBridge` on 10.2.2 resolves internal `StorageInstruments` from
  `grainContext.ActivationServices` — persistent-facet tests require real silo
  DI (TestingHost), not hand-faked `IGrainContext`.
  `RedisStorageOptions.CreateMultiplexer` changed signature between the two
  versions — never customize it in test code.
- Phase 0 proof code lives in `tests/Orleans.FSharp.SeamProof/` and is promoted
  into production tests or deleted before merge (spec requirement).
- Solution file: `Orleans.FSharp.slnx`. Central package management; add new
  package versions to `Directory.Packages.props` pinned to `$(OrleansVersion)`
  where applicable.
- Commits: imperative messages, **no Co-Authored-By trailers**.
- Pre-existing public APIs stay source- and behavior-compatible; their tests
  keep passing (spec completion criteria).

## Tasks

| # | Task | Spec section | Implementer model |
|---|---|---|---|
| 1 | Phase 0: Orleans seam proof (11 architecture gates) | Implementation plan → Phase 0 | opus |
| 2 | Phase 1: public types, contracts, CE builders, key codecs | Phase 1 + Normative public API | opus |
| 3 | Phase 2: bound records, fixed serialization, protocol tokens | Phase 2 + Fixed transport | opus |
| 4 | Phase 3: Orleans reference, manifest, activation, dispatch | Phase 3 + Orleans hosting integration | opus |
| 5 | Phase 4: state, persistence facets, lifecycle ordering | Phase 4 + Persistence and activation lifecycle | opus |
| 6 | Phase 5: collection age, reminders, timers, context | Phase 5 | sonnet |
| 7 | Phase 6: runnable sample, docs, compatibility suite | Phase 6 | sonnet |
| 8 | Deprecation pass: survey superseded old APIs → `[<Obsolete>]` with replacement pointers; add new-API examples beside the old deprecated ones | user goal (not in spec) | sonnet (survey list approved by controller first) |

Task 8 scope rule: only APIs actually superseded by the functional runtime get
`[<Obsolete>]`; each attribute message names the concrete replacement
(`FunctionalGrain.ref` / `grainContract` / `grainFor` path). The survey output
is a list reviewed by the controller before any attribute lands.

## Exit criteria

Each task's exit condition is the corresponding **Exit:** block in the spec's
implementation plan, plus the relevant groups in the spec's Required tests
section. Task 8: solution builds warning-clean except intended `FS0044`
obsolete warnings in old-API tests, which are suppressed locally with pragma.
