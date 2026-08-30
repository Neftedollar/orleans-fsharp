---
title: "Calls and Delivery"
description: "Handler shapes, call policies, observers, pub/sub streams, and streaming replies."
---

# Calls and Delivery

**Pick the delivery mechanism from the relationship between caller and consumer.**

| Need | Mechanism |
|---|---|
| One request and one reply | API field returning `Task<'Reply>` |
| Read-only request | `readOnly` + `handleQuery` |
| Fire-and-forget notification | `oneWay` |
| Push to one connected client object | Functional observer |
| Decoupled provider-backed pub/sub | Orleans stream or broadcast channel |
| Items produced by one call | API field returning `IAsyncEnumerable<'Item>` |

A normal handler returns state and reply; a query returns only the reply:

```fsharp
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

The contract must mark a `handleQuery` operation `readOnly`. This makes the no-state-write
intent explicit in both the API and definition.

## Concurrency policy

Orleans serializes turns per activation by default. Use `reentrant` for the whole contract or
`mayInterleave` / `alwaysInterleave` for specific requests. These policies can expose
intermediate state between awaits, so add them only around operations designed for it.

Do not add locks or `Task.Run` inside a grain. They conflict with Orleans scheduling rather than
making a functional handler safer.

## Three streaming concepts

- Functional observers are best-effort live push and must be subscribed again after a client or
  activation lifetime ends.
- Orleans streams and broadcast channels are pub/sub abstractions selected and configured by a
  provider.
- Server-streaming replies attach one producer to one call and propagate enumerator disposal back
  to that producer.

They compose, but they are not interchangeable durability guarantees.

## Details

- [Delivery semantics and reentrancy](/orleans-fsharp/functional-grains/#delivery-semantics)
- [Functional observers](/orleans-fsharp/functional-grains/#push-to-clients-functional-observers)
- [Implicit subscriptions](/orleans-fsharp/functional-grains/#implicit-subscriptions-onstream-and-onbroadcast)
- [Orleans streams](/orleans-fsharp/streaming/)
- [Server-Streaming Replies](/orleans-fsharp/streaming-replies/)
