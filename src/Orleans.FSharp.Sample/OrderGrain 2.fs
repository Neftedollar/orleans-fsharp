namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

// ---------------------------------------------------------------------------
// Order DU State Machine
// ---------------------------------------------------------------------------

/// <summary>
/// Represents the state of an order as a discriminated union state machine.
/// Each case represents a distinct stage in the order lifecycle.
/// </summary>
[<GenerateSerializer>]
type OrderStatus =
    /// <summary>No order has been placed.</summary>
    | [<Id(0u)>] Idle
    /// <summary>An order is being processed with the given description.</summary>
    | [<Id(1u)>] Processing of description: string
    /// <summary>The order has been completed successfully with the given summary.</summary>
    | [<Id(2u)>] Completed of summary: string
    /// <summary>The order has failed with the given reason.</summary>
    | [<Id(3u)>] Failed of reason: string

/// <summary>
/// Wrapper class for OrderStatus that Orleans can instantiate for persistence.
/// F# discriminated unions compile to abstract classes, so this concrete wrapper
/// holds the DU value for IPersistentState.
/// </summary>
[<GenerateSerializer>]
[<Sealed>]
type OrderStatusHolder() =
    /// <summary>The order status value.</summary>
    [<Id(0u)>]
    member val State: OrderStatus = Idle with get, set

/// <summary>
/// Commands that can be sent to the order grain.
/// </summary>
[<GenerateSerializer>]
type OrderCommand =
    /// <summary>Place a new order with the given description.</summary>
    | [<Id(0u)>] Place of description: string
    /// <summary>Confirm a processing order.</summary>
    | [<Id(1u)>] Confirm
    /// <summary>Ship a confirmed order (completing it).</summary>
    | [<Id(2u)>] Ship
    /// <summary>Cancel the current order.</summary>
    | [<Id(3u)>] Cancel
    /// <summary>Get the current order status.</summary>
    | [<Id(4u)>] GetStatus

/// <summary>
/// Grain interface for the order processing grain.
/// </summary>
type IOrderGrain =
    inherit IGrainWithStringKey

    /// <summary>Handle an order command and return the result.</summary>
    abstract HandleMessage: OrderCommand -> Task<obj>

/// <summary>
/// Module containing the order grain definition and state transition logic.
/// </summary>
module OrderGrainDef =

    /// <summary>
    /// Validates and applies a state transition for the order state machine.
    /// Returns Ok with (newState, result) or Error with a reason string.
    /// </summary>
    /// <param name="state">The current order state.</param>
    /// <param name="cmd">The command to process.</param>
    /// <returns>A Result containing the new state and boxed result, or an error message.</returns>
    let transition (state: OrderStatus) (cmd: OrderCommand) : Result<OrderStatus * obj, string> =
        match state, cmd with
        | Idle, Place desc -> Ok(Processing desc, box (Processing desc))
        | Idle, Confirm -> Error "Cannot confirm: no order is being processed"
        | Idle, Ship -> Error "Cannot ship: no order is being processed"
        | Idle, Cancel -> Error "Cannot cancel: no active order"
        | Idle, GetStatus -> Ok(state, box state)

        | Processing desc, Confirm -> Ok(Completed $"Order confirmed: {desc}", box (Completed $"Order confirmed: {desc}"))
        | Processing _, Ship -> Error "Cannot ship: order must be confirmed first"
        | Processing _, Cancel -> Ok(Failed "Cancelled by user", box (Failed "Cancelled by user"))
        | Processing _, Place _ -> Error "Cannot place: an order is already being processed"
        | Processing _, GetStatus -> Ok(state, box state)

        | Completed _, Place _ -> Error "Cannot place: order is already completed"
        | Completed _, Confirm -> Error "Cannot confirm: order is already completed"
        | Completed _, Ship -> Error "Cannot ship: order is already completed"
        | Completed _, Cancel -> Error "Cannot cancel: order is already completed"
        | Completed _, GetStatus -> Ok(state, box state)

        | Failed _, Place desc -> Ok(Processing desc, box (Processing desc))
        | Failed _, Confirm -> Error "Cannot confirm: order has failed"
        | Failed _, Ship -> Error "Cannot ship: order has failed"
        | Failed _, Cancel -> Error "Cannot cancel: order has already failed"
        | Failed _, GetStatus -> Ok(state, box state)

    /// <summary>
    /// The order grain definition, built using the grain computation expression.
    /// </summary>
    let order =
        grain {
            defaultState Idle

            handle (fun state cmd ->
                task {
                    match transition state cmd with
                    | Ok(newState, result) -> return newState, result
                    | Error reason -> return state, box reason
                })

            persist "Default"
        }
