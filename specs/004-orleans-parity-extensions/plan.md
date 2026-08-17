# Implementation Plan — Spec 004 Phase A

**Spec (authority):** [spec.md](./spec.md) — items 4, 8, 9.
**Branch:** `feat/004-parity-phase-a` (based on the spec branch until its PR merges).
**Discipline:** spec-003 rules carried forward: implementer per task + controller
(Fable) personal review; sequential dispatch (no parallel implementers — the
003 git-index races); price-first design probes before any promised surface;
parity/mapping visible in files, not asserted in reports; push after each
accepted task; both-Orleans-versions matrix per task; earliest-stage
validation; C#-consumable surfaces.

## Tasks

| # | Task | Spec item | Model |
|---|---|---|---|
| A1 | First-class placement (`statelessWorker` / `placement`) + lifecycle-stage hooks (`onLifecycle`) + `Scripting.startOnPorts` functional hosting | 4, 8 | sonnet |
| A2 | C# facade (`forCSharp`) + "Calling from C#" docs | 9 | opus after controller design probe |

A2 gate: the controller probes the facade mechanism (DispatchProxy vs typed
helper vs source-gen) with a compiler check BEFORE the brief promises a
surface — the curried-episode rule, institutionalized.
