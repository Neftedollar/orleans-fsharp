# Developer Guide

This guide provides technical deep-dives into Orleans.FSharp's architecture, development patterns, and contribution workflows. It's intended for developers who want to understand how the library works internally or contribute to the codebase.

---

## 📚 Table of Contents

- [Architecture Deep Dive](#architecture-deep-dive)
- [How Computation Expressions Work](#how-computation-expressions-work)
- [Adding New CE Keywords](#adding-new-ce-keywords)
- [Understanding the Universal Grain Pattern](#understanding-the-universal-grain-pattern)
- [Testing Strategy](#testing-strategy)
- [Serialization Architecture](#serialization-architecture)
- [Release Process](#release-process)
- [Performance Considerations](#performance-considerations)
- [Common Contribution Patterns](#common-contribution-patterns)

---

## Architecture Deep Dive

### Core Design Principles

Orleans.FSharp is built around these principles:

1. **Functional Core, Imperative Shell**: Grain handlers are pure functions. Orleans manages the imperative shell (persistence, lifecycle, clustering).

2. **Type Safety Over Runtime Checks**: Use F#'s type system to prevent invalid states at compile time, not runtime validation.

3. **Composability**: Every CE keyword is composable. You can combine `handle`, `persist`, `onActivate`, etc. in any order.

4. **Zero Abstraction Penalty**: The CE layer adds negligible overhead vs raw Orleans API calls. Performance benchmarks confirm this.

### Module Dependencies

```
Orleans.FSharp (core library)
│
├── Dependencies:
│   ├── Orleans.FSharp.Abstractions (transitive)
│   ├── FsToolkit.ErrorHandling (taskResult { } CE)
│   ├── FSharp.Control.TaskSeq (streaming)
│   ├── IcedTasks (cold tasks, cancellable tasks)
│   ├── Polly (resilience patterns)
│   └── Microsoft.Orleans.* (v10.1.*)
│
└── Provides:
    ├── grain { } CE
    ├── FSharpGrain module (universal grain pattern)
    ├── Streaming, Reminders, Timers, Observers
    ├── Serialization (F# Binary, JSON, Native)
    └── GrainRef, GrainState, GrainContext modules

Orleans.FSharp.Runtime
│
├── Dependencies:
│   ├── Orleans.FSharp (core)
│   ├── Microsoft.Orleans.Server
│   ├── Serilog.Extensions.Logging
│   └── Microsoft.Extensions.*
│
└── Provides:
    ├── siloConfig { } CE
    ├── clientConfig { } CE
    ├── Serilog integration
    └── GrainDiscovery

Orleans.FSharp.Abstractions (C# project)
│
├── Dependencies:
│   ├── Microsoft.Orleans.Sdk
│   └── Microsoft.Orleans.EventSourcing
│
└── Provides:
    ├── IFSharpGrain (universal interface)
    ├── IFSharpGrainWithGuidKey
    ├── IFSharpGrainWithIntKey
    └── Orleans proxy generation
```

### Key Types

```fsharp
// Grain definition result type
type GrainDefinition<'S, 'M> = {
    DefaultState: 'S
    Handler: 'S -> 'M -> Task<'S * obj>
    // ... other handlers, hooks, etc.
}

// Universal grain interface (in Abstractions, C#)
public interface IFSharpGrain : IGrainWithStringKey
{
    Task<GrainDispatchResult> Handle(string commandType, object payload);
}

// F# dispatch result
type GrainDispatchResult = {
    State: obj option
    Result: obj option
}
```

---

## How Computation Expressions Work

### The `grain { }` Builder

The `grain { }` CE is a `GrainBuilder<'S, 'M>` that accumulates configuration into a `GrainDefinition<'S, 'M>` record:

```fsharp
type GrainBuilder<'S, 'M>() =
    let mutable defaultState = Unchecked.defaultof<'S>
    let mutable handler = Unchecked.defaultof<'S -> 'M -> Task<'S * obj>>
    let mutable persistProvider = None
    // ... other mutable slots

    member _.Yield(()) = ()

    [<CustomOperation("defaultState")>]
    member _.DefaultState(state: 'S) =
        defaultState <- state
        ()

    [<CustomOperation("handle")>]
    member _.Handle(handlerFunc: 'S -> 'M -> Task<'S * obj>) =
        handler <- handlerFunc
        ()

    [<CustomOperation("persist")>]
    member _.Persist(providerName: string) =
        persistProvider <- Some providerName
        ()

    // ... more keywords

    member _.Run() = {
        DefaultState = defaultState
        Handler = handler
        // ... build complete definition
    }
```

### Execution Flow

When you define a grain:

```fsharp
let counter = grain {
    defaultState { Count = 0 }
    handle handlerFunc
    persist "Default"
}
```

The CE executes in order:
1. `Yield()` initializes builder
2. `defaultState` sets initial state
3. `handle` stores handler function
4. `persist` stores provider name
5. `Run()` returns complete `GrainDefinition`

This definition is then registered with Orleans via `AddFSharpGrain`:

```fsharp
siloBuilder.Services.AddFSharpGrain<CounterState, CounterCommand>(counter)
```

Which:
1. Creates `FSharpGrainImpl<CounterState, CounterCommand>` class
2. Registers it with Orleans grain activator
3. Sets up message routing via `UniversalGrainHandlerRegistry`

---

## Adding New CE Keywords

### Step 1: Identify the Need

New keywords should solve a real developer pain point. Check:
- GitHub issues requesting the feature
- Common Orleans patterns not yet covered
- Feedback from community surveys

### Step 2: Design the API

Keywords should:
- Be **composable** with existing keywords
- Follow **naming conventions** (lowercase camelCase)
- Have **clear semantics** (one keyword = one concern)
- Be **discoverable** (name should hint at purpose)

Example: Adding `handleState`

```fsharp
// Problem: handle requires manual boxing
handle (fun state cmd -> task {
    let newState = { Count = state.Count + 1 }
    return newState, box newState  // manual box
})

// Solution: handleState returns state directly
handleState (fun state cmd -> task {
    return { Count = state.Count + 1 }  // no box needed
})
```

### Step 3: Implement the Keyword

Add to `GrainBuilder.fs`:

```fsharp
[<CustomOperation("handleState")>]
member _.HandleState(handlerFunc: 'S -> 'M -> Task<'S>) =
    // Wrap to match handle signature
    let wrapped state msg = task {
        let! newState = handlerFunc state msg
        return newState, box newState  // auto-box
    }
    handler <- wrapped
    ()
```

### Step 4: Add Tests

```fsharp
test "handleState should auto-box state in result" {
    let grainDef = grain {
        defaultState { Count = 0 }
        handleState (fun state _ -> task {
            return { Count = state.Count + 1 }
        })
    }
    
    let! newState, result = grainDef.Handler initialState Increment
    newState.Count |> should equal 1
    result :?> int |> should equal 1  // auto-boxed
}
```

### Step 5: Update Documentation

- Add to `QUICK-REFERENCE.md`
- Add to `docs/grain-definition.md`
- Add example to README if significant
- Update CHANGELOG.md

---

## Understanding the Universal Grain Pattern

### Traditional Orleans Pattern

```csharp
// 1. Define interface (per grain!)
public interface ICounterGrain : IGrainWithStringKey
{
    Task<int> Increment();
    Task<int> GetCount();
}

// 2. Implement grain (C# class)
public class CounterGrain : Grain, ICounterGrain
{
    public Task<int> Increment() { ... }
    public Task<int> GetCount() { ... }
}

// 3. CodeGen project generates proxies
```

### Orleans.FSharp Universal Pattern

```fsharp
// 1. Define grain (pure F#, no interfaces)
let counter = grain {
    defaultState { Count = 0 }
    handle (fun state cmd -> task {
        match cmd with
        | Increment -> return { Count = state.Count + 1 }, box (state.Count + 1)
    })
}

// 2. Register once at silo startup
siloBuilder.Services.AddFSharpGrain<CounterState, CounterCommand>(counter)

// 3. Call from anywhere
let handle = FSharpGrain.ref<CounterState, CounterCommand> factory "counter-1"
let! state = handle |> FSharpGrain.send Increment
```

### How It Works

1. **Abstractions Project** (C#):
   - Defines `IFSharpGrain`, `IFSharpGrainWithGuidKey`, `IFSharpGrainWithIntKey`
   - Orleans SDK generates proxy classes for these interfaces
   - Proxies are public and discoverable by Orleans runtime

2. **FSharpGrainImpl** (C#, in Abstractions):
   - Concrete grain class implementing `IFSharpGrain`
   - Delegates to F# handler via `UniversalGrainHandlerRegistry`
   - One implementation covers all F# grains

3. **UniversalGrainHandlerRegistry** (F#):
   - Maps DU type names to handler functions
   - Routes messages to correct grain definitions
   - Handles serialization/deserialization

4. **AddFSharpGrain** (F#, in Runtime):
   - Registers grain definition in DI container
   - Sets up message routing
   - Auto-registers `FSharpBinaryCodec` for serialization

### Benefits

- **No per-grain C# stubs**: One universal interface for all grains
- **Pure F#**: Grains defined with computation expressions, not classes
- **Type-safe**: DU commands prevent invalid messages at compile time
- **Composable**: Keywords compose in any order
- **Testable**: Handlers are pure functions, easy to unit test

---

## Testing Strategy

### Test Pyramid

```
        ╱╲
       ╱  ╲         Integration (200 tests)
      ╱────╲        with TestCluster
     ╱      ╲
    ╱────────╲       Unit (1200 tests)
   ╱  Property ╲     with FsCheck
  ╱____________╲
 ╱              ╲    Edge cases, error paths
╱________________╲
```

### Unit Tests

Test individual modules in isolation:

```fsharp
[<Fact>]
let ``should increment counter when Increment command sent`` () = task {
    let grainDef = counterGrain
    let! newState, result = grainDef.Handler { Count = 0 } Increment
    newState.Count |> should equal 1
    result :?> int |> should equal 1
}
```

### Property Tests (FsCheck)

Test invariants across all inputs:

```fsharp
[<Property>]
let ``applyMigrations should be idempotent`` (migrations: Migration list) (state: obj) =
    let result1 = StateMigration.applyMigrations migrations 1 state
    let result2 = StateMigration.applyMigrations migrations 1 result1
    result1 = result2
```

### Integration Tests

Test with real Orleans TestCluster:

```fsharp
[<Fact>]
let ``grain should persist state to storage`` () = task {
    use! cluster = TestHarness.createTestCluster config
    let grain = cluster.GetGrain<IFSharpGrain> "test-1"
    
    let! result = grain.Handle("Increment", box ())
    let grain2 = cluster.GetGrain<IFSharpGrain> "test-1"
    let! state = grain2.Handle("GetCount", box ())
    
    state.Result |> should equal (box 1)
}
```

### Running Tests

```bash
# All tests
dotnet test

# Unit tests only
dotnet test tests/Orleans.FSharp.Tests

# Integration tests only
dotnet test tests/Orleans.FSharp.Integration

# Specific test
dotnet test tests/Orleans.FSharp.Tests --filter "FullyQualifiedName~GrainBuilderTests"

# With verbose output
dotnet test tests/Orleans.FSharp.Tests --logger "console;verbosity=detailed"
```

---

## Serialization Architecture

### Three Modes

1. **F# Binary** (default, fastest):
   - Uses `FSharpBinaryCodec` (auto-registered)
   - Native Orleans serialization
   - Zero allocations for records and DUs

2. **JSON** (interoperable):
   - Uses `FSharp.SystemTextJson`
   - Human-readable, debuggable
   - Slight performance penalty

3. **Orleans Native** (fallback):
   - Orleans default serialization
   - Works for all types
   - May require `[<GenerateSerializer>]` attributes

### How FSharpBinaryCodec Works

```fsharp
// Auto-registered when using AddFSharpGrain
FSharpBinaryCodecRegistration.addToSerializerBuilder serializerBuilder

// Registers F#-specific codecs for:
// - Records
// - Discriminated unions
// - Tuples
// - Options
// - Lists, Arrays, Maps, Sets
```

### Adding Custom Serializers

```fsharp
let config = siloConfig {
    configureServices (fun services ->
        services.AddSerializer(fun builder ->
            builder.AddProvider<MyCustomCodecProvider>() |> ignore
        )
    )
}
```

---

## Release Process

### Version Numbering

We use [Semantic Versioning](https://semver.org/):

- **MAJOR**: Breaking changes
- **MINOR**: New features (backward compatible)
- **PATCH**: Bug fixes (backward compatible)

### Release Steps

Versioning is automated by [MinVer](https://github.com/adamralph/minver), which derives the package version from the latest `v*` git tag. There is no version field to edit in `Directory.Build.props`.

1. **Update CHANGELOG.md**:
   - Move items from `[Unreleased]` to a new version section
   - Add date: `## [2.1.0] - 2026-04-15`
   - Update the comparison links at the bottom

2. **Build and test on `main`**:
   ```bash
   dotnet build --configuration Release
   dotnet test
   ```

3. **Tag and push**:
   ```bash
   git tag v2.1.0           # use v2.1.0-alpha.1 for prereleases
   git push origin v2.1.0
   ```

4. **CI publishes to NuGet**:
   - GitHub Actions detects the `v*` tag
   - MinVer reads the tag and stamps the package version
   - Packages are published to NuGet.org via trusted publisher (OIDC)
   - Pre-release tags (`-alpha.N`, `-rc.N`) produce pre-release packages

### NuGet Packages

Published packages:
- `Orleans.FSharp`
- `Orleans.FSharp.Runtime`
- `Orleans.FSharp.Abstractions`
- `Orleans.FSharp.EventSourcing`
- `Orleans.FSharp.EventSourcing.Marten`
- `Orleans.FSharp.Testing`
- `Orleans.FSharp.Analyzers`
- `Orleans.FSharp.Templates`

---

## Performance Considerations

### Zero Abstraction Penalty

Orleans.FSharp adds **negligible overhead** vs raw Orleans:

- **CE builder**: One-time allocation at grain definition (not per-call)
- **Handler dispatch**: Single dictionary lookup in `UniversalGrainHandlerRegistry`
- **Boxing**: Only for `handle` (required by universal interface); avoided with `handleState` and `handleTyped`

### Benchmarking

Run benchmarks:

```bash
cd benchmarks
dotnet run --configuration Release
```

Key metrics:
- **Latency**: Time per grain call
- **Throughput**: Calls per second
- **Allocations**: Bytes per call

### Optimization Tips

1. **Use `handleState` or `handleTyped`** instead of `handle` to avoid manual boxing
2. **Prefer records for state** (faster serialization than DUs)
3. **Use F# Binary serialization** for performance-critical paths
4. **Allow read-heavy message types to interleave** with `interleaveMessage typeof<Query>` to lift the one-message-at-a-time bottleneck
5. **For `[StatelessWorker]` / `[Reentrant]` high-throughput grains**, use the per-grain `Orleans.FSharp.CodeGen` path (the universal pattern cannot carry per-grain attributes)

---

## Common Contribution Patterns

### Adding a Persistence Provider

1. Create new project: `Orleans.FSharp.Persistence.MyProvider`
2. Implement provider interface:
   ```fsharp
   type MyProvider(config: MyConfig) =
       interface IGrainStorage with
           member _.ReadAsync(...) = ...
           member _.WriteAsync(...) = ...
           member _.ClearAsync(...) = ...
   ```
3. Add `siloConfig { }` keyword:
   ```fsharp
   [<CustomOperation("addMyStorage")>]
   member _.AddMyStorage(name: string, config: MyConfig) = ...
   ```
4. Write tests with real provider
5. Document in `docs/` and update README

### Adding an Analyzer Rule

1. Open `Orleans.FSharp.Analyzers` project
2. Create new analyzer:
   ```fsharp
   type MyAnalyzer() =
       interface IAnalyzer with
           member _.Analyze(ast: UntypedParse) =
               // Walk AST, return diagnostics
               [ ... ]
   ```
3. Add tests in `AnalyzerTests.fs`
4. Document in `docs/analyzers.md`

### Adding a CE Keyword

See [Adding New CE Keywords](#adding-new-ce-keywords) section above.

---

## Troubleshooting

### Build Fails with Warnings

```
error FSXXXX: Warning as error: ...
```

Fix the warning (usually a missing XML doc or unused variable). We don't suppress warnings except in specific cases.

### Tests Fail with "Grain Not Found"

Check:
1. Grain is registered via `AddFSharpGrain`
2. Key type matches (string vs GUID vs int)
3. `Orleans.FSharp.Abstractions` is referenced in test project

### Serialization Errors

Check:
1. Type is serializable (record, DU, tuple, primitive)
2. No circular references in state
3. `FSharpBinaryCodec` is registered (auto-done by `AddFSharpGrain`)

### Orleans Source Generator Not Running

Check:
1. `Orleans.FSharp.Abstractions` is a C# project (generators only run on C#)
2. Project references are correct
3. Clean and rebuild: `dotnet clean && dotnet build`

---

## Resources

- [F# for Fun and Profit](https://fsharpforfunandprofit.com/) - Learn F#
- [Microsoft Orleans Docs](https://learn.microsoft.com/dotnet/orleans/) - Orleans internals
- [F# Foundation](https://fsharp.org/) - F# community and standards
- [Conventional Commits](https://www.conventionalcommits.org/) - Commit message convention

---

**Questions?** Open an issue or discussion on [GitHub](https://github.com/Neftedollar/orleans-fsharp).
