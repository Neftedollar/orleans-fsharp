# Functional Grain Runtime

**The short path through the current Orleans.FSharp authoring model.**

Use this page for the mental model, then follow one focused guide. The exhaustive surface remains
in [Functional Runtime Reference](functional-grains.md).

## The four pieces

1. An API record describes the calls a client can make.
2. A contract fixes actor identity, key encoding, version, and call policies.
3. A definition attaches state, handlers, and activation behavior.
4. A typed reference binds the contract to an Orleans `IGrainFactory` and domain key.

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

        handle (_.increment) (fun _ state () ->
            task {
                let next = state + 1
                return next, next
            })

        handleQuery (_.value) (fun _ state () -> task { return state })
    }
```

At the call site, `CounterApi.ref factory "orders"` returns the record itself. Calling
`api.increment ()` uses Orleans routing and activation; it is not an in-process function call.

## Read next by concern

| Concern | Focused guide |
|---|---|
| Actor identity, keys, operation shapes, and versions | [Contracts and Keys](functional-grains/contracts.md) |
| Ephemeral state, persistence, lifecycle, timers, and reminders | [State and Lifecycle](functional-grains/state-and-lifecycle.md) |
| Handler semantics, observers, streams, and streaming replies | [Calls and Delivery](functional-grains/delivery-and-streaming.md) |
| Stateless workers, placement, migration, and transactions | [Placement and Transactions](functional-grains/placement-and-transactions.md) |
| Events as the durable source of truth | [Event Sourcing](event-sourcing.md) |
| Every builder operation and edge case | [Complete Reference](functional-grains.md) |

## Which example is canonical?

[Getting Started](getting-started.md) and [Testing](testing.md) share the Counter above.
[Chat Room](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/chat-room) is the
canonical multi-feature application and includes a C# caller.
[Feature Tour](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/feature-tour)
exercises the broader Orleans surface against a live silo.

## What stays Orleans-native

Clustering, placement directors, storage providers, stream providers, reminders, transactions,
log-consistency providers, activation migration, and Dashboard remain Orleans facilities.
Orleans.FSharp supplies a typed F# authoring and transport layer; it does not replace the runtime.

For current framework versions and additions which are not wrapped yet, see
[Orleans Compatibility](compatibility.md).
