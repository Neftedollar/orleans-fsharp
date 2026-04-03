# Orleans.FSharp.CodeGen

C# bridge project that enables Orleans Roslyn source generators for F# grain definitions.

## Why this package exists

Orleans uses C# Roslyn source generators to produce serializers and grain method dispatchers. These generators do not run on F# projects. This package is a thin C# project that references your F# grain interfaces and definitions, allowing the Orleans SDK to generate the required code.

**This project contains no runtime logic** -- only assembly-level attributes and project references.

## How to use

1. Add this package (or a project reference) to your solution.
2. Reference your F# grains project and `Orleans.FSharp` from this C# project.
3. Add an `AssemblyAttributes.cs` file with the appropriate Orleans generate-code attributes:

```csharp
using Orleans;

[assembly: GenerateCodeForDeclaringAssembly]
```

4. The Orleans SDK source generator runs during the C# build and emits serializers for all referenced F# types.

## Project references

This package references:
- `Orleans.FSharp` -- core grain definitions
- `Orleans.FSharp.EventSourcing` -- event-sourced grain definitions
- Your F# sample/application project containing grain interfaces

## Requirements

- .NET 10+
- `Microsoft.Orleans.Sdk` (included)

## License

MIT
