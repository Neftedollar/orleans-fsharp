# Saga Pattern with Orleans F#

Distributed transactions across multiple grains using an orchestrator grain
that coordinates compensating actions on failure.

> **Note.** The functional grain runtime below (`grainContract` / `grainFor`) is the current
> authoring model for this pattern. The original `grain { }` CE version is kept, unchanged, under
> [Classic model (deprecated)](#classic-model-deprecated) below -- that CE now carries
> `[<Obsolete>]` (warning, not error) but still compiles and runs as described there.

## Architecture

```
SagaApi orchestrator grain
  |
  +---> Step 1: InventoryApi grain
  |       (compensate: release)
  |
  +---> Step 2: PaymentApi grain
          (compensate: refund)
```

Unlike the classic write-up, `InventoryApi` and `PaymentApi` below are real, minimal functional
grains rather than illustrative, never-defined interfaces -- so the whole saga, including its
compensation path, is verified end to end rather than merely sketched.

## Saga State

```fsharp
open System.Threading.Tasks
open Orleans.FSharp

type SagaStatus =
    | NotStarted
    | Completed
    | Failed of error: string

/// A persisted state's stored type must be concrete: Orleans activates it with
/// `RuntimeHelpers.GetUninitializedObject`, which cannot construct an abstract class -- and an
/// F# union with two or more cases where at least one carries data (like `SagaStatus` above)
/// compiles to exactly that. Wrapping it in a record (always a single concrete class) is the fix
/// -- see "Persistence and Recovery" below for where this is used.
type SagaProgress = { status: SagaStatus }

type OrderSaga =
    { orderId: string
      items: string list
      amount: decimal }

type SagaActor = private SagaActor of unit

[<NoEquality; NoComparison>]
type SagaApi =
    { start: OrderSaga -> Task<SagaStatus>
      status: unit -> Task<SagaStatus> }

[<RequireQualifiedAccess>]
module SagaApi =
    let contract =
        grainContract<SagaActor, string, SagaApi> () {
            grainType "saga.orchestrator"
            version 1
            stringKey

            readOnly (_.status)
        }

    let ref = FunctionalGrain.ref contract
```

The classic `SagaStatus` also had `InProgress`/`Compensating` cases for a visible-progress
design, but its own code (below, unchanged) never actually constructed either one -- both models
run a saga to completion inside one handler invocation, so an intermediate status was never
externally observable in either. This version keeps only the statuses it actually produces;
adding visible intermediate progress (persist after each step, reintroduce the cases) is a
straightforward extension, not a capability gap.

## Supporting Grains (Inventory, Payment)

Two small functional grains stand in for the classic write-up's undefined `IInventoryGrain` /
`IPaymentGrain`. `charge` rejects orders over 500 so the failure/compensation path below has
something real to trigger:

```fsharp
type InventoryActor = private InventoryActor of unit

[<NoEquality; NoComparison>]
type InventoryApi =
    { reserve: string list -> Task<Result<unit, string>>
      release: string list -> Task<unit>
      held: unit -> Task<string list> }

[<RequireQualifiedAccess>]
module InventoryApi =
    let contract =
        grainContract<InventoryActor, string, InventoryApi> () {
            grainType "saga.inventory"
            version 1
            stringKey

            readOnly (_.held)
        }

    let ref = FunctionalGrain.ref contract

module InventoryDefinition =
    let inventory =
        grainFor InventoryApi.contract {
            defaultState (fun () -> Set.empty<string>)

            handle
                (_.reserve)
                (fun _context state items ->
                    task {
                        if items |> List.contains "out-of-stock" then
                            return state, Error "insufficient inventory"
                        else
                            return Set.union state (Set.ofList items), Ok()
                    })

            handle (_.release) (fun _context state items -> task { return Set.difference state (Set.ofList items), () })

            handle (_.held) (fun _context state () -> task { return state, state |> Set.toList |> List.sort })
        }

type PaymentActor = private PaymentActor of unit

[<NoEquality; NoComparison>]
type PaymentApi =
    { charge: decimal -> Task<Result<unit, string>>
      refund: decimal -> Task<unit> }

[<RequireQualifiedAccess>]
module PaymentApi =
    let contract =
        grainContract<PaymentActor, string, PaymentApi> () {
            grainType "saga.payment"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

module PaymentDefinition =
    let payment =
        grainFor PaymentApi.contract {
            defaultState (fun () -> 0m)

            handle
                (_.charge)
                (fun _context state amount ->
                    task {
                        if amount > 500m then
                            return state, Error "payment declined"
                        else
                            return state + amount, Ok()
                    })

            handle (_.refund) (fun _context state amount -> task { return state - amount, () })
        }
```

## Orchestrator Logic

```fsharp
/// One compensable step. Identical shape to the classic sample's `SagaStep` -- only where the
/// closures come from changes: they close over `context.grainFactory` instead of a bare
/// `IGrainFactory` parameter, because they are built inside a functional handler (see "Defining
/// Steps as Grain Calls" below).
type SagaStep =
    { name: string
      execute: unit -> Task<Result<unit, string>>
      compensate: unit -> Task<unit> }

module Orchestration =
    let executeSaga (steps: SagaStep list) : Task<SagaStatus> =
        task {
            let mutable completed = []
            let mutable outcome = None

            for step in steps do
                if outcome.IsNone then
                    match! step.execute () with
                    | Ok() -> completed <- step.name :: completed
                    | Error err ->
                        // Begin compensation: roll back completed steps in reverse
                        let toCompensate =
                            steps |> List.filter (fun s -> completed |> List.contains s.name) |> List.rev

                        for comp in toCompensate do
                            do! comp.compensate ()

                        outcome <- Some(Failed err)

            match outcome with
            | Some status -> return status
            | None -> return Completed
        }
```

## Defining Steps as Grain Calls

Each saga step calls a different grain. `context.grainFactory` is exactly the `FunctionalGrain.ref
context.grainFactory` mechanic the CQRS sample also uses for its own grain-to-grain call (see
[../cqrs-pattern](../cqrs-pattern/README.md)):

```fsharp
module Steps =
    let reserveInventory (factory: Orleans.IGrainFactory) (orderId: string) (items: string list) : SagaStep =
        let inventory = InventoryApi.ref factory orderId

        { name = "ReserveInventory"
          execute = fun () -> inventory.reserve items
          compensate = fun () -> inventory.release items }

    let chargePayment (factory: Orleans.IGrainFactory) (orderId: string) (amount: decimal) : SagaStep =
        let payment = PaymentApi.ref factory orderId

        { name = "ChargePayment"
          execute = fun () -> payment.charge amount
          compensate = fun () -> payment.refund amount }
```

## Persistence and Recovery

The saga state should be persisted so that if the orchestrator grain deactivates mid-saga, it can
resume or compensate on reactivation. Unlike the classic sample -- whose prose promises exactly
this but whose code (below) never calls `persist` -- this orchestrator actually wires
`stateFrom`, so a mid-saga reactivation reloads the last known outcome instead of starting blank:

```fsharp
module SagaDefinition =
    let sagaProgress = PersistentState.create<SagaProgress> "progress" "Default"

    let saga =
        grainFor SagaApi.contract {
            defaultState (fun () -> { status = NotStarted })
            stateFrom sagaProgress

            handle
                (_.start)
                (fun context _state request ->
                    task {
                        let steps =
                            [ Steps.reserveInventory context.grainFactory request.orderId request.items
                              Steps.chargePayment context.grainFactory request.orderId request.amount ]

                        let! status = Orchestration.executeSaga steps
                        let next = { status = status }

                        let storage = context.persistentState sagaProgress
                        storage.State <- next
                        do! storage.WriteStateAsync()

                        return next, status
                    })

            handle (_.status) (fun _context state () -> task { return state, state.status })
        }
```

Running this end to end against a real cluster (this repo verified it, not just compiled it):
starting a saga with a valid item list and an amount under 500 reserves inventory, charges
payment, and reports `Completed`; starting one with an amount over 500 reserves inventory,
fails to charge, releases the reservation, and reports `Failed "payment declined"` -- confirmed
by querying `InventoryApi.held` afterward and seeing it empty again.

## When to Use

- Multi-service operations that need atomicity guarantees
- Long-running business processes with compensating actions
- Order fulfillment, booking systems, financial transfers
- Prefer sagas over distributed transactions for better availability

## Classic model (deprecated)

This is the original write-up, kept unchanged. It is written against the `grain { }` CE, which
now carries `[<Obsolete>]` (warning, not error) -- it still compiles and runs as described. The
pattern it demonstrates is the same one presented on the functional runtime above; only the
authoring model differs.

### Architecture

```
SagaOrchestrator grain
  |
  +---> Step 1: ReserveInventory grain
  |       (compensate: ReleaseInventory)
  |
  +---> Step 2: ChargePayment grain
  |       (compensate: RefundPayment)
  |
  +---> Step 3: CreateShipment grain
          (compensate: CancelShipment)
```

### Saga State

```fsharp
type SagaStep = {
    Name: string
    Execute: unit -> System.Threading.Tasks.Task<Result<unit, string>>
    Compensate: unit -> System.Threading.Tasks.Task<unit>
}

type SagaStatus =
    | NotStarted
    | InProgress of completedSteps: string list
    | Completed
    | Compensating of failedAt: string * remainingCompensations: string list
    | Failed of error: string

type SagaState = {
    Status: SagaStatus
    Steps: SagaStep list
}
```

### Orchestrator Logic

```fsharp
open Orleans.FSharp

let executeSaga (ctx: GrainContext) (state: SagaState) =
    task {
        let mutable completed = []
        let mutable currentState = { state with Status = InProgress [] }

        for step in state.Steps do
            match! step.Execute() with
            | Ok () ->
                completed <- step.Name :: completed
                currentState <- { currentState with Status = InProgress completed }
            | Error err ->
                // Begin compensation: roll back completed steps in reverse
                let toCompensate =
                    state.Steps
                    |> List.filter (fun s -> completed |> List.contains s.Name)
                    |> List.rev

                for comp in toCompensate do
                    do! comp.Compensate()

                return { currentState with Status = Failed err }

        return { currentState with Status = Completed }
    }
```

### Defining Steps as Grain Calls

Each saga step calls a different grain. The orchestrator coordinates them.

```fsharp
let reserveInventory (factory: IGrainFactory) (orderId: string) (items: string list) : SagaStep =
    {
        Name = "ReserveInventory"
        Execute = fun () -> task {
            let grain = factory.GetGrain<IInventoryGrain>(orderId)
            return! grain.Reserve(items)
        }
        Compensate = fun () -> task {
            let grain = factory.GetGrain<IInventoryGrain>(orderId)
            do! grain.Release(items)
        }
    }

let chargePayment (factory: IGrainFactory) (orderId: string) (amount: decimal) : SagaStep =
    {
        Name = "ChargePayment"
        Execute = fun () -> task {
            let grain = factory.GetGrain<IPaymentGrain>(orderId)
            return! grain.Charge(amount)
        }
        Compensate = fun () -> task {
            let grain = factory.GetGrain<IPaymentGrain>(orderId)
            do! grain.Refund(amount)
        }
    }
```

### Persistence and Recovery

The saga state should be persisted so that if the orchestrator grain
deactivates mid-saga, it can resume or compensate on reactivation.

```fsharp
// Use grain persistent state to track saga progress
let sagaGrain = grain {
    defaultState { Status = NotStarted; Steps = [] }
    handleTypedWithContext (fun ctx state msg ->
        task {
            match msg with
            | StartSaga steps ->
                let! result = executeSaga ctx { state with Steps = steps }
                return result, result
            | GetStatus ->
                return state, state
        })
}
```

### When to Use

- Multi-service operations that need atomicity guarantees
- Long-running business processes with compensating actions
- Order fulfillment, booking systems, financial transfers
- Prefer sagas over distributed transactions for better availability
