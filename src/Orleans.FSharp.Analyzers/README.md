# Orleans.FSharp.Analyzers

Roslyn-style F# analyzers for [Orleans.FSharp](https://github.com/Neftedollar/orleans-fsharp) — compile-time feedback for idiomatic grain code.

## What it does

Grain handlers must return `Task<_>`, but it's easy to reach for `async { }` out of F# habit. This analyzer flags `async { }` used where Orleans expects a `task { }`-based handler, so the mistake surfaces at edit time instead of as a runtime surprise.

## Setup

Register the analyzer with the [F# Analyzers SDK](https://github.com/ionide/FSharp.Analyzers.SDK) (Ionide / `fsharp-analyzers` CLI). Add to your `.fsproj`:

```xml
<ItemGroup>
  <PackageReference Include="Orleans.FSharp.Analyzers" Version="3.*">
    <IncludeAssets>analyzers</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

Then run via the `fsharp-analyzers` tool or your editor's analyzer integration.

## Requirements

- .NET 8+ host (the analyzer assembly targets `net8.0`)
- `FSharp.Analyzers.SDK`-compatible runner

## Documentation

Full docs and examples: <https://github.com/Neftedollar/orleans-fsharp>

## License

MIT
