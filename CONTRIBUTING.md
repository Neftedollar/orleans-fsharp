# Contributing to Orleans.FSharp

Thank you for your interest in contributing to Orleans.FSharp! This project brings idiomatic F# to Microsoft Orleans, and every contribution -- whether it's a bug fix, new test, documentation improvement, or feature -- makes the library better for the entire F# community.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An editor with F# support (VS Code + Ionide, Rider, or Visual Studio)

### Build

```bash
dotnet build
```

The solution uses `TreatWarningsAsErrors`, so the build must complete with **zero warnings**.

### Run Tests

```bash
dotnet test
```

All 1500+ tests must pass before submitting a pull request.

## Code Style

Orleans.FSharp follows idiomatic F# conventions:

- **Use `task {}` exclusively** -- never use `async {}` in production code. The Orleans runtime is built on `Task<T>`, and `task {}` avoids unnecessary allocations from `Async.StartAsTask`.
- **XML documentation comments are required** on all public API members. Every public function, type, and module should have a `<summary>` doc comment.
- **Discriminated unions over class hierarchies** -- model state and commands as DUs wherever possible.
- **Pure functions preferred** -- keep side effects at the edges; grain handlers should be as pure as practical.
- **No mutable state** outside of Orleans-managed grain state.
- **`TreatWarningsAsErrors` is enabled** -- the CI build will reject any warnings.

## Pull Request Process

1. **Fork** the repository and create a feature branch from `main`:
   ```bash
   git checkout -b my-feature main
   ```

2. **Make your changes** following the code style guidelines above.

3. **Add tests** for any new functionality. We use expecto-style tests and aim for comprehensive coverage.

4. **Ensure everything passes:**
   ```bash
   dotnet build   # 0 warnings
   dotnet test    # all tests green
   ```

5. **Push** your branch and open a pull request against `main`.

6. **Describe your changes** clearly in the PR description. Explain *what* changed and *why*.

7. A maintainer will review your PR. We aim to respond within a few days.

## Areas Where Help Is Welcome

Not sure where to start? Here are areas where contributions are especially valuable:

- **More tests** -- edge cases, error paths, concurrency scenarios. We have 800+ tests but there's always room for more.
- **Documentation improvements** -- clearer examples, better explanations, fixing typos.
- **Persistence provider integrations** -- additional storage providers, clustering providers, or reminder services.
- **Samples** -- real-world example applications that demonstrate Orleans.FSharp patterns.
- **Performance** -- benchmarks, optimizations, reducing allocations.
- **Analyzer rules** -- new compile-time checks for the `Orleans.FSharp.Analyzers` package.

## First-Time Contributors

If this is your first contribution to an open-source project, welcome! Here are some tips:

1. Look for issues labeled [`good first issue`](https://github.com/Neftedollar/orleans-fsharp/labels/good%20first%20issue) -- these are specifically chosen to be approachable for newcomers.
2. Don't hesitate to ask questions in an issue or discussion thread. We're happy to help.
3. Small PRs are easier to review and more likely to be merged quickly. Start with something focused.
4. If you're unsure whether an idea fits the project, open an issue first to discuss it.

## Reporting Bugs

Use the [Bug Report](https://github.com/Neftedollar/orleans-fsharp/issues/new?template=bug_report.yml) issue template. Include:

- Orleans.FSharp version
- .NET version
- Minimal reproduction steps
- Expected vs actual behavior

## Releasing

Versioning is driven by [MinVer](https://github.com/adamralph/minver) from git tags. Maintainers cut a release like this:

1. Make sure `main` is green and `CHANGELOG.md` has a dated section for the new version.
2. Tag the commit on `main`: `git tag v<X.Y.Z>` (or `v<X.Y.Z>-alpha.N` for prereleases).
3. Push the tag: `git push origin v<X.Y.Z>`.
4. CI builds, packs, and publishes to NuGet via OIDC trusted publisher on tag push.

Do **not** edit `Directory.Build.props` to bump the version — MinVer derives the package version from the tag. Stable tags (`v2.0.0`) produce stable packages; pre-release tags (`v2.0.0-alpha.1`) produce pre-release packages.

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating, you agree to uphold a welcoming, inclusive, and harassment-free environment for everyone.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
