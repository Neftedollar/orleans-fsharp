# Orleans.FSharp.CodeGen — Legacy compatibility package

This package is the per-grain C# Roslyn source-generation bridge for applications that still use
the original Orleans.FSharp authoring model. New functional grains use typed API records and the
fixed proxies from `Orleans.FSharp.Abstractions`; they do not use this package.

## Legacy API

Existing applications may keep this bridge while migrating. It contains no runtime logic: a C#
project references the F# grain assembly, applies Orleans generation attributes, and lets
`Microsoft.Orleans.Sdk` emit serializers and dispatchers.

```csharp
using Orleans;

[assembly: GenerateCodeForDeclaringAssembly]
```

See the [Legacy API](https://github.com/Neftedollar/orleans-fsharp/tree/main/docs/legacy) and
[migration guide](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/legacy/migration.md).

## Requirements

- .NET 10+
- `Microsoft.Orleans.Sdk`

## License

MIT
