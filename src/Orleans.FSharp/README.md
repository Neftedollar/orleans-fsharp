# Orleans.FSharp

Idiomatic, functional F# actors on [Microsoft Orleans](https://learn.microsoft.com/dotnet/orleans/).
The current authoring model uses typed API records, `grainContract`, `grainFor`, and
`FunctionalGrain.ref`; it needs no application-owned C# grain interface or proxy project.

## Quick example

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      value: unit -> Task<int> }

[<RequireQualifiedAccess>]
module CounterApi =
    let contract =
        grainContract<CounterActor, string, CounterApi> {
            grainType "counter"
            version 1
            stringKey
            readOnly (_.value)
        }

    let ref = FunctionalGrain.ref contract

let counterDefinition =
    grainFor CounterApi.contract {
        defaultState (fun () -> 0)

        handle (_.increment) (fun _ count () ->
            task {
                let next = count + 1
                return next, next
            })

        handleQuery (_.value) (fun _ count () -> task { return count })
    }

let callCounter (grainFactory: Orleans.IGrainFactory) =
    task {
        let counter = CounterApi.ref grainFactory "visits"
        let! next = counter.increment ()
        let! current = counter.value ()
        return next, current
    }
```

Register `counterDefinition` with `ISiloBuilder.AddFunctionalGrain`; callers use
`callCounter`'s typed API-record binding.

## Current functional surface

- `grainContract<'Actor,'Key,'Api> { }` declares stable identity, key encoding, versioning, and
  delivery policy.
- `grainFor contract { }` binds immutable state transitions, persistence, lifecycle hooks,
  timers, reminders, streams, placement, and transactions.
- `journaledGrainFor contract { }` binds a pure event fold and supports Orleans log-consistency
  providers, typed custom storage, and snapshot policies.
- `FunctionalGrain.ref` returns the typed API record; `FunctionalGrain.rawRef` additionally exposes
  cancellable and server-streaming calls.
- `FunctionalObserver` provides codegen-free typed push callbacks.

See the [functional runtime guide](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/functional-grains.md),
[event-sourcing guide](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/event-sourcing.md),
and [API reference](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/api-reference.md).

## Related packages

| Package | Purpose |
|---|---|
| `Orleans.FSharp.Runtime` | Silo/client configuration and functional-definition hosting |
| `Orleans.FSharp.Abstractions` | Pre-generated transport proxies, pulled in transitively |
| `Orleans.FSharp.Testing` | TestingHost, web-host, FsCheck, and log-capture helpers |
| `Orleans.FSharp.Analyzers` | F# analyzer that reports `async { }` where `task { }` is expected |

## Legacy archive

The original authoring model is unsupported and is not part of the 5.x release line. Its
source-only compatibility projects, examples, and migration mapping remain in the repository's
[Legacy archive](https://github.com/Neftedollar/orleans-fsharp/tree/main/docs/legacy) to help
existing applications migrate.

## License

MIT
