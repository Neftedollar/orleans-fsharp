# CQRS Pattern with Orleans F#

Command-Query Responsibility Segregation separates read and write operations into
distinct grain types, enabling independent scaling and optimized data models.

## Architecture

```
Client
  |
  +---> ICommandGrain (write side) ---> IPersistentState<T>
  |
  +---> IQueryGrain   (read side)  ---> denormalized read model
```

## Write Grain (Command Side)

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

## Read Grain (Query Side)

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

## Projecting Events

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

## When to Use

- High read-to-write ratio (many more queries than commands)
- Read and write models have different shapes
- Need to scale reads independently of writes
- Complex domain logic on the write side
