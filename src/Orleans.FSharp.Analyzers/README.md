# Orleans.FSharp.Analyzers

An opt-in [FSharp.Analyzers.SDK](https://github.com/ionide/FSharp.Analyzers.SDK) CLI plugin for Orleans.FSharp.

## What it does

OF0001 flags unsuppressed `async { }` expressions in the files you select for analysis. Prefer `task { }` in grain handlers; `[<AllowAsync>]` marks intentional Async interop. This is a syntax-based style rule, not proof that a binding executes inside a grain.

## Setup

Add the package to your project, then keep its normal library assets so the public `AllowAsync` attribute is available. Pin your selected package version instead of floating it in production:

```xml
<ItemGroup>
  <PackageReference Include="Orleans.FSharp.Analyzers" Version="*"
                    PrivateAssets="all" GeneratePathProperty="true" />
</ItemGroup>
```

Run the matching CLI host against the package's **directory**, not the DLL file:

```bash
dotnet tool install --global fsharp-analyzers --version 0.37.2
dotnet restore MyGrains.fsproj
analyzer_package="$(dotnet msbuild MyGrains.fsproj -nologo -getProperty:PkgOrleans_FSharp_Analyzers)"
fsharp-analyzers --project MyGrains.fsproj --analyzers-path "$analyzer_package/lib/net8.0"
```

This package exports a `CliAnalyzer`, not an `EditorAnalyzer` or Roslyn analyzer. Installing it alone does **not** run OF0001 during `dotnet build` or in an editor. Invoke the CLI explicitly in CI. The ordinary `lib/net8.0` package layout is intentional and follows the F# SDK's library-plugin model.

## Requirements

- .NET 8+ host (the analyzer assembly targets `net8.0`)
- `fsharp-analyzers` 0.37.2 (matching the package's FSharp.Analyzers.SDK dependency)

## Documentation

Full docs and examples: <https://github.com/Neftedollar/orleans-fsharp>

## License

MIT
