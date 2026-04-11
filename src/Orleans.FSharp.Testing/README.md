# Orleans.FSharp.Testing

Test utilities for Orleans.FSharp grains -- in-process clusters, mocks, property-based testing, and log capture.

## Components

### TestHarness

Wraps an Orleans `TestCluster` with memory storage, memory streams, and integrated log capture. Start and tear down a full in-process silo in a few lines:

```fsharp
let! harness = TestHarness.createTestCluster ()
let grain = harness.Client.GetGrain<IMyGrain>(grainId)
// ... test grain calls ...
do! harness.Cluster.StopAllSilosAsync()
```

### GrainMock

A `MockGrainFactory` that implements `IGrainFactory` with predefined grain responses. Useful for unit testing grain-to-grain interactions without a real silo:

```fsharp
let factory = MockGrainFactory()
GrainMock.withGrain<IMyDep> "key" myMockImpl factory
```

### WebTestHarness

For HTTP endpoint tests, you can run ASP.NET Core `TestServer` without Orleans `TestCluster`
and inject a mocked grain factory directly:

```fsharp
let! harness =
    WebTestHarness.createWithMockFactory
        (fun factory ->
            factory
            |> GrainMock.withFSharpGrain "company-1" companyGrainDefinition)
        (fun web ->
            web.Configure(fun app ->
                app.Run(fun ctx ->
                    task {
                        let gf = ctx.RequestServices.GetRequiredService<IGrainFactory>()
                        let handle = FSharpGrain.ref<CompanyState, CompanyCommand> gf "company-1"
                        let! state = handle |> FSharpGrain.send Increment
                        return! ctx.Response.WriteAsync(string state.Count)
                    } :> Task))
            |> ignore)
```

This keeps endpoint tests fast and deterministic while still exercising
`FSharpGrain.ref`/`send` flows.

`WebTestHarness.createWithFactory` and `createWithMockFactory` fail fast when
`configureWeb` already registers `IGrainFactory`, preventing ambiguous DI setup.

For typed query-style responses, use `FSharpGrain.ask`:

```fsharp
type CompanyState = { Count: int }
type CompanyCommand =
    | Increment
    | GetCount

let companyDef =
    grain {
        defaultState { Count = 0 }
        handleTyped (fun state cmd ->
            task {
                match cmd with
                | Increment ->
                    let next = { Count = state.Count + 1 }
                    return next, next.Count
                | GetCount ->
                    return state, state.Count
            })
    }

let! harness =
    WebTestHarness.createWithMockFactory
        (fun factory -> factory |> GrainMock.withFSharpGrain "company-42" companyDef)
        (fun web ->
            web.Configure(fun app ->
                app.Run(fun ctx ->
                    task {
                        let gf = ctx.RequestServices.GetRequiredService<IGrainFactory>()
                        let handle = FSharpGrain.ref<CompanyState, CompanyCommand> gf "company-42"
                        let! count = handle |> FSharpGrain.ask<CompanyState, CompanyCommand, int> GetCount
                        return! ctx.Response.WriteAsync(string count)
                    } :> Task))
            |> ignore)
```

Use `send` when the handler result is the state; use `ask` when the handler returns
another typed value (for example `int`, DTO, tuple).

### GrainArbitrary

TypeShape-based auto-generator of FsCheck `Arbitrary` instances for F# discriminated unions. Automatically discovers DU cases and field types to produce well-typed random grain states.

### FsCheckHelpers

- `commandSequenceArb<'Command>` -- generates non-empty command sequences
- `stateMachineProperty` -- verifies a state invariant holds after folding a command list

### LogCapture

`CapturingLogger` / `CapturingLoggerFactory` -- an in-memory `ILogger` implementation that records structured log entries (`CapturedLogEntry`) for test assertions on log level, template, properties, and exceptions.

## Dependencies

- `Microsoft.Orleans.TestingHost`
- `FsCheck 3.x`
- `TypeShape`
- `xunit`

## License

MIT
