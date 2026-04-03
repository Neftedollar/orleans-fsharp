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
