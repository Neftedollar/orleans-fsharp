# Orleans.FSharp.CodeGen — Legacy source archive

This non-packable source project is the archived per-grain C# Roslyn bridge from the original
Orleans.FSharp authoring model. It is retained only to build migration examples and tests. New
functional grains use typed API records and fixed proxies from `Orleans.FSharp.Abstractions`.

## Legacy API

Existing applications can study or build this bridge while migrating. It contains no runtime logic: a C#
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
