namespace OrderProcessing.Domain

open System.Threading.Tasks
open Orleans

/// <summary>
/// Commands that can be sent to the order processing grain.
/// </summary>
type OrderCommand =
    /// <summary>Place a new order with the given description.</summary>
    | Place of description: string
    /// <summary>Confirm a created order.</summary>
    | Confirm
    /// <summary>Ship a confirmed order.</summary>
    | Ship
    /// <summary>Mark a shipped order as delivered.</summary>
    | Deliver
    /// <summary>Cancel an active order with a reason.</summary>
    | Cancel of reason: string
    /// <summary>Get the current order status.</summary>
    | GetStatus

/// <summary>
/// Result of processing an order command.
/// </summary>
type OrderResult =
    /// <summary>Command succeeded with the new status.</summary>
    | Ok of OrderStatus
    /// <summary>Command was rejected with a reason.</summary>
    | Rejected of reason: string
    /// <summary>No order exists.</summary>
    | NoOrder

/// <summary>
/// Grain interface for the order processing grain.
/// </summary>
type IOrderGrain =
    inherit IGrainWithStringKey

    /// <summary>Handles an order command and returns the result.</summary>
    abstract HandleMessage: OrderCommand -> Task<obj>
