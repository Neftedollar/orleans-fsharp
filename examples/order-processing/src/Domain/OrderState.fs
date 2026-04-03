namespace OrderProcessing.Domain

open System

/// <summary>
/// Represents the lifecycle state of an order as a discriminated union state machine.
/// Each case is a distinct stage with only valid transitions permitted.
/// </summary>
type OrderStatus =
    /// <summary>Order has been created but not yet confirmed.</summary>
    | Created of description: string * createdAt: DateTime
    /// <summary>Order has been confirmed and is awaiting shipment.</summary>
    | Confirmed of description: string * confirmedAt: DateTime
    /// <summary>Order has been shipped and is in transit.</summary>
    | Shipped of description: string * shippedAt: DateTime
    /// <summary>Order has been delivered to the customer.</summary>
    | Delivered of description: string * deliveredAt: DateTime
    /// <summary>Order has been cancelled with a reason.</summary>
    | Cancelled of reason: string * cancelledAt: DateTime

/// <summary>
/// Overall order grain state wrapping the current status and metadata.
/// </summary>
type OrderState =
    {
        /// <summary>The current order status, or None if no order has been placed.</summary>
        Status: OrderStatus option
        /// <summary>Number of status check ticks from the timer.</summary>
        StatusCheckCount: int
        /// <summary>Number of reminder ticks received.</summary>
        ReminderTickCount: int
    }
