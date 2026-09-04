# Orleans.FSharp.Testing

TestingHost, web-host, property-testing, mock-factory, and log-capture helpers for
Orleans.FSharp.

## Functional grain integration tests

Functional definitions are registered on a real Orleans `TestCluster`, then called through the
same typed API record used in production. This exercises transport, activation, serialization,
and persistence without fabricating the runtime-owned `FunctionalGrainContext`.

```fsharp
open System.Threading.Tasks
open Orleans
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi = { increment: unit -> Task<int> }

let counterContract =
    grainContract<CounterActor, string, CounterApi> {
        grainType "testing.counter"
        version 1
        stringKey
    }

let counterDefinition =
    grainFor counterContract {
        defaultState (fun () -> 0)
        handle (_.increment) (fun _ count () ->
            task {
                let next = count + 1
                return next, next
            })
    }

type CounterSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.AddFunctionalGrain(counterDefinition) |> ignore

type CounterClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore
```

`counterDefinition` and its typed reference come from the same `grainContract` / `grainFor`
module as production code. See the complete, self-contained fixture in the
[Testing guide](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/testing.md).

## Components

| Component | Purpose |
|---|---|
| `TestHarness` | A conventional in-process Orleans cluster with memory providers and log capture |
| `WebTestHarness` | Combined TestCluster and ASP.NET Core TestServer |
| `WebTestHarness.createWithFactory` | TestServer over a supplied `IGrainFactory` |
| `GrainMock.withGrain` | Register a mocked Orleans interface in `MockGrainFactory` |
| `GrainArbitrary` | TypeShape-backed FsCheck generation for F# states and command DUs |
| `FsCheckHelpers` | Command-sequence and state-machine property helpers |
| `LogCapture` | Structured in-memory `ILogger` entries for assertions |

## Log capture

```fsharp
open Microsoft.Extensions.Logging
open Orleans.FSharp.Testing

let factory = LogCapture.create ()
let logger = (factory :> ILoggerFactory).CreateLogger("Test")
logger.LogInformation("Processed {Count}", 3)

let entries = LogCapture.captureLogs factory
```

## Legacy archive

Compatibility helpers for the old authoring model remain temporarily for migration, but are
unsupported. Their archived examples live in
[Legacy Testing](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/legacy/testing.md).

## Dependencies

- `Microsoft.Orleans.TestingHost`
- `FsCheck`
- `TypeShape`
- `xunit`

## License

MIT
