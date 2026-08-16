# CQRS Pattern with Orleans F#

Command-Query Responsibility Segregation separates read and write operations into
distinct grain types, enabling independent scaling and optimized data models.

> **Note.** The functional grain runtime below (`grainContract` / `grainFor`) is the current
> authoring model for this pattern. The original `grain { }` CE version is kept, unchanged, under
> [Classic model (deprecated)](#classic-model-deprecated) below -- that CE now carries
> `[<Obsolete>]` (warning, not error) but still compiles and runs as described there.

## Architecture

```
Client
  |
  +---> OrderCommandApi (write side, mutating handlers)
  |
  +---> OrderQueryApi   (read side, readOnly queries)  <--- projected by the write side
```

Command/query separation maps directly onto two independent `grainContract` / `grainFor` pairs:
the command side exposes mutating operations, the query side exposes `readOnly` operations over
its own denormalized state -- no shared code path, no shared storage, exactly the isolation CQRS
asks for.

## Command Grain (Write Side)

The command grain owns the source of truth and handles all mutations. The pure decision function
is unchanged from the classic model -- only the glue that wires it to Orleans changes.

```fsharp
open System
open System.Threading.Tasks
open Orleans.FSharp

type OrderCommand =
    | PlaceOrder of items: string list
    | CancelOrder of reason: string
    | ShipOrder

type OrderState =
    | Empty
    | Placed of items: string list
    | Shipped of items: string list
    | Cancelled of reason: string

module Domain =
    let handleCommand (state: OrderState) (cmd: OrderCommand) : Result<OrderState, string> =
        match state, cmd with
        | Empty, PlaceOrder items -> Ok(Placed items)
        | Placed items, ShipOrder -> Ok(Shipped items)
        | Placed _, CancelOrder reason -> Ok(Cancelled reason)
        | _, PlaceOrder _ -> Error "Order already exists"
        | _, ShipOrder -> Error "Order not in placed state"
        | _, CancelOrder _ -> Error "Order cannot be cancelled in current state"

type OrderCommandActor = private OrderCommandActor of unit

[<NoEquality; NoComparison>]
type OrderCommandApi =
    { place: string list -> Task<Result<OrderState, string>>
      cancel: string -> Task<Result<OrderState, string>>
      ship: unit -> Task<Result<OrderState, string>> }

[<RequireQualifiedAccess>]
module OrderCommandApi =
    let contract =
        grainContract<OrderCommandActor, string, OrderCommandApi> () {
            grainType "cqrs.order-command"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract
```

Each command DU case becomes its own typed operation (`place` / `cancel` / `ship`) instead of one
handler pattern-matching on a message union -- every operation gets its own precise argument and
reply type, and each one calls straight into the same `Domain.handleCommand` the classic model
used, unchanged.

## Query Grain (Read Side)

The query grain maintains a denormalized view optimized for reads. Both of its read operations
are declared `readOnly` in the contract -- Orleans schedules them to interleave with each other
and discards whatever state replacement the handler returns, exactly matching the read-only,
never-mutating role a query side is supposed to play. `project` (how the write side pushes
updates -- see "Projecting Events" below) is the one mutating operation on this grain.

```fsharp
type OrderSummary =
    { orderId: string
      itemCount: int
      status: string
      lastUpdated: DateTimeOffset }

type ProjectedOrder = { items: string list; status: string }

/// The query grain's own persisted shape -- richer than `OrderSummary`, which is only ever a
/// reply, never stored.
type OrderReadModel =
    { orderId: string
      items: string list
      status: string
      lastUpdated: DateTimeOffset }

type OrderQueryActor = private OrderQueryActor of unit

[<NoEquality; NoComparison>]
type OrderQueryApi =
    { project: ProjectedOrder -> Task<unit>
      summary: unit -> Task<OrderSummary>
      items: unit -> Task<string list> }

[<RequireQualifiedAccess>]
module OrderQueryApi =
    let contract =
        grainContract<OrderQueryActor, string, OrderQueryApi> () {
            grainType "cqrs.order-query"
            version 1
            stringKey

            oneWay (_.project)
            readOnly (_.summary)
            readOnly (_.items)
        }

    let ref = FunctionalGrain.ref contract

module OrderQueryDefinition =
    let orderQuery =
        grainFor OrderQueryApi.contract {
            initialState (fun orderId ->
                { orderId = orderId
                  items = []
                  status = "unknown"
                  lastUpdated = DateTimeOffset.MinValue })

            handle
                (_.project)
                (fun context state projected ->
                    task {
                        return
                            { state with
                                items = projected.items
                                status = projected.status
                                lastUpdated = context.utcNow },
                            ()
                    })

            handle
                (_.summary)
                (fun _context state () ->
                    task {
                        return
                            state,
                            { orderId = state.orderId
                              itemCount = List.length state.items
                              status = state.status
                              lastUpdated = state.lastUpdated }
                    })

            handle (_.items) (fun _context state () -> task { return state, state.items })
        }
```

`initialState` (key-aware) seeds `orderId` from the grain's own key -- a small improvement over
the classic query grain's `state { OrderId = ""; ... }`, which had no way to know which order it
was looking at until told.

## Projecting Events

The write side pushes the read side's update directly, over an ordinary grain-to-grain call --
no stream, no separate observer grain to keep in sync:

```fsharp
module Projection =
    let projectionOf (state: OrderState) : ProjectedOrder =
        match state with
        | Empty -> { items = []; status = "empty" }
        | Placed items -> { items = items; status = "placed" }
        | Shipped items -> { items = items; status = "shipped" }
        | Cancelled reason -> { items = []; status = $"cancelled: {reason}" }

    /// Runs the pure decision function, then pushes the result to this order's query-side grain
    /// over a `oneWay` call -- the command's own success does not wait for the read model to
    /// catch up, which is exactly the eventual-consistency contract CQRS asks for.
    let applyAndProject
        (context: FunctionalGrainContext<OrderCommandActor, string>)
        (state: OrderState)
        (cmd: OrderCommand)
        =
        task {
            match Domain.handleCommand state cmd with
            | Ok next ->
                let query = OrderQueryApi.ref context.grainFactory context.key
                do! query.project (projectionOf next)
                return next, Ok next
            | Error e -> return state, Error e
        }

module OrderCommandDefinition =
    open Projection

    let orderCommand =
        grainFor OrderCommandApi.contract {
            defaultState (fun () -> Empty)

            handle (_.place) (fun context state items -> applyAndProject context state (PlaceOrder items))
            handle (_.cancel) (fun context state reason -> applyAndProject context state (CancelOrder reason))
            handle (_.ship) (fun context state () -> applyAndProject context state ShipOrder)
        }
```

Both grains share the same domain key (the order ID), so `OrderQueryApi.ref context.grainFactory
context.key` from inside the command grain's handler always addresses the matching query grain --
`context.grainFactory` is the same grain-to-grain mechanic the saga pattern uses for its own
inter-grain calls (see [../saga-pattern](../saga-pattern/README.md)).

Because `project` is declared `oneWay`, the command's own `Task` completes once the push has
entered the local send path -- **before** the query grain has necessarily even started applying
it (see "Delivery semantics" in [docs/functional-grains.md](../../docs/functional-grains.md)). A
caller that reads the query side immediately after a successful command may briefly see the
previous projection; this is CQRS's normal eventual-consistency window, not a bug, and is exactly
why the classic write-up below called this a "background... subscriber" updating the read model
rather than a synchronous step.

A functional handler can also reach a **named** Orleans stream provider straight from
`context.services`, the same way it reaches named persistent state from `context.persistentState`:
`context.services.GetRequiredKeyedService<Orleans.Streams.IStreamProvider>("StreamProvider")`
resolves correctly from inside a running functional handler (verified by running one), if you
want stream-based fan-out to several projections instead of one direct call. A single `oneWay`
push is the simpler mechanism for one read model and is what's shown above.

## When to Use

- High read-to-write ratio (many more queries than commands)
- Read and write models have different shapes
- Need to scale reads independently of writes
- Complex domain logic on the write side

## Classic model (deprecated)

This is the original write-up, kept unchanged. It is written against the `grain { }` CE, which
now carries `[<Obsolete>]` (warning, not error) -- it still compiles and runs as described. The
pattern it demonstrates is the same one presented on the functional runtime above; only the
authoring model differs.

### Architecture

```
Client
  |
  +---> ICommandGrain (write side) ---> IPersistentState<T>
  |
  +---> IQueryGrain   (read side)  ---> denormalized read model
```

### Write Grain (Command Side)

The command grain owns the source of truth and handles all mutations.

```fsharp
open Orleans.FSharp

type OrderCommand =
    | PlaceOrder of items: string list
    | CancelOrder of reason: string
    | ShipOrder

type OrderState =
    | Empty
    | Placed of items: string list
    | Shipped of items: string list
    | Cancelled of reason: string

let handleCommand (state: OrderState) (cmd: OrderCommand) : Result<OrderState, string> =
    match state, cmd with
    | Empty, PlaceOrder items ->
        Ok (Placed items)
    | Placed items, ShipOrder ->
        Ok (Shipped items)
    | Placed _, CancelOrder reason ->
        Ok (Cancelled reason)
    | _, PlaceOrder _ ->
        Error "Order already exists"
    | _, ShipOrder ->
        Error "Order not in placed state"
    | _, CancelOrder _ ->
        Error "Order cannot be cancelled in current state"

// In the grain builder:
let orderGrain = grain {
    name "OrderCommand"
    state Empty
    handle (fun ctx state cmd ->
        task {
            match handleCommand state cmd with
            | Ok newState -> return Ok newState
            | Error e -> return Error e
        })
}
```

### Read Grain (Query Side)

The query grain maintains a denormalized view optimized for reads.

```fsharp
type OrderSummary = {
    OrderId: string
    ItemCount: int
    Status: string
    LastUpdated: System.DateTimeOffset
}

type OrderQuery =
    | GetSummary
    | GetItems

// A separate grain for read queries, potentially backed by
// a different storage provider optimized for reads.
let orderQueryGrain = grain {
    name "OrderQuery"
    state { OrderId = ""; ItemCount = 0; Status = "unknown"; LastUpdated = System.DateTimeOffset.MinValue }
    handle (fun ctx state query ->
        task {
            return Ok state
        })
}
```

### Projecting Events

The write grain publishes events to an Orleans stream.
A background grain or observer subscribes and updates the read model.

```fsharp
open Orleans.FSharp.Streaming

// In the command grain handler, after successful state transition:
let publishEvent (ctx: GrainContext) (event: OrderEvent) =
    task {
        let provider = ctx.StreamProvider "OrderEvents"
        let stream = Stream.getStream<OrderEvent> provider "orders" (ctx.Key)
        do! Stream.publish stream event
    }
```

### When to Use

- High read-to-write ratio (many more queries than commands)
- Read and write models have different shapes
- Need to scale reads independently of writes
- Complex domain logic on the write side
