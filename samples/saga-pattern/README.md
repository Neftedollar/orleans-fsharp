# Saga Pattern with Orleans F#

Distributed transactions across multiple grains using an orchestrator grain
that coordinates compensating actions on failure.

## Architecture

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

## Saga State

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

## Orchestrator Logic

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

## Defining Steps as Grain Calls

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

## Persistence and Recovery

The saga state should be persisted so that if the orchestrator grain
deactivates mid-saga, it can resume or compensate on reactivation.

```fsharp
// Use grain persistent state to track saga progress
let sagaGrain = grain {
    name "Saga"
    state { Status = NotStarted; Steps = [] }
    handle (fun ctx state msg ->
        task {
            match msg with
            | StartSaga steps ->
                let! result = executeSaga ctx { state with Steps = steps }
                return Ok result
            | GetStatus ->
                return Ok state
        })
}
```

## When to Use

- Multi-service operations that need atomicity guarantees
- Long-running business processes with compensating actions
- Order fulfillment, booking systems, financial transfers
- Prefer sagas over distributed transactions for better availability
