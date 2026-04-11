# Contributing to Orleans.FSharp

Thank you for contributing! This project brings idiomatic F# to Microsoft Orleans, and every contribution — bug fix, test, doc improvement, or feature — makes the library better for the entire F# community.

## Quick Start

```bash
dotnet build    # zero warnings required
dotnet test     # 1,542 tests must pass
```

That's it. If both succeed, you're ready to work on the codebase.

## How to Contribute

### 1. Documentation Fixes (Easiest, Most Valuable)

Every doc file in `docs/` has room for improvement. Better examples, clearer explanations, fixed typos — these help **every developer** who uses the library.

```bash
# Pick any docs file, improve it, open a PR
# No tests needed for doc-only changes
```

### 2. Bug Fixes

Found a bug? Here's the fastest path to a fix:

1. **Open an issue** with a minimal reproduction
2. **Fork** the repo and create a branch
3. **Write a failing test** that reproduces the bug
4. **Fix the code** until the test passes
5. **Open a PR** with the test + fix

```bash
git checkout -b fix/issue-number main
# ... make changes ...
dotnet build && dotnet test
git push origin fix/issue-number
```

### 3. New Features

For new features, please **open an issue first** to discuss the design. This saves everyone time — we can validate the approach before you invest effort.

## Code Style

| Rule | Why |
|---|---|
| Use `task {}` exclusively | Orleans is `Task`-based; `async {}` adds unnecessary allocations |
| XML docs on all public API | Developers need IntelliSense that actually explains things |
| Discriminated unions over classes | F# idiomatic modeling — pattern matching, exhaustiveness, immutability |
| Pure functions in handlers | Easier to test, reason about, and compose |
| No mutable state outside Orleans-managed state | The runtime handles persistence and concurrency |
| `TreatWarningsAsErrors` | Catches mistakes before they reach CI |

## Pull Request Checklist

- [ ] `dotnet build` passes with zero warnings
- [ ] `dotnet test` — all 1,542 tests green
- [ ] New code has XML documentation comments
- [ ] New functionality has tests (unit or integration)
- [ ] PR description explains **what** changed and **why**

## Where Help Is Most Welcome

| Area | What we need |
|---|---|
| **Tests** | Error paths, concurrency scenarios, FsCheck properties |
| **Docs** | Clearer examples, "why this matters" explanations |
| **Samples** | Real-world applications demonstrating patterns |
| **Benchmarks** | Performance tracking, regression detection |
| **Analyzers** | New compile-time checks for common mistakes |
| **Integrations** | Additional storage/cluster/reminder providers |

## First-Time Contributors

Welcome! Here's how to get started:

1. Look for issues labeled [`good first issue`](https://github.com/Neftedollar/orleans-fsharp/labels/good%20first%20issue)
2. Small, focused PRs are easier to review and merge quickly
3. If you're unsure whether an idea fits, open an issue first — we're happy to discuss
4. Ask questions in the issue thread — no question is too basic

## Reporting Bugs

[Open an issue](https://github.com/Neftedollar/orleans-fsharp/issues/new) and include:

- Orleans.FSharp version and .NET version
- Minimal reproduction steps
- Expected vs actual behavior

A good bug report gets a fix faster. The more we can reproduce locally, the quicker we can help.

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). We expect a welcoming, inclusive, and harassment-free environment for everyone.

## License

Contributions are licensed under [MIT](LICENSE).
