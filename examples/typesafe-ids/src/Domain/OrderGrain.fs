/// <summary>
/// Order grain demonstrating type-safe ID access.
/// The <c>getOrder</c> function only accepts <c>int64&lt;OrderId&gt;</c> -- passing a
/// <c>int64&lt;UserId&gt;</c> is a compile error.
/// </summary>
namespace TypeSafeIds.Domain

open System.Threading.Tasks
open Orleans
open Orleans.FSharp
open TypeSafeIds.Domain.Ids

/// <summary>
/// Possible states of an order.
/// Discriminated union with exhaustive matching -- add a new case and the compiler
/// tells you every match expression that needs updating.
/// </summary>
type OrderStatus =
    /// <summary>Order has been created but not yet confirmed.</summary>
    | Pending
    /// <summary>Order has been confirmed and is being processed.</summary>
    | Confirmed
    /// <summary>Order has been shipped to the customer.</summary>
    | Shipped
    /// <summary>Order has been delivered to the customer.</summary>
    | Delivered
    /// <summary>Order was cancelled before delivery.</summary>
    | Cancelled

/// <summary>
/// State of an order grain.
/// </summary>
type OrderState =
    {
        /// <summary>The typed owner (user) ID for this order.</summary>
        OwnerId: int64<UserId>
        /// <summary>Total monetary amount of the order.</summary>
        Total: decimal
        /// <summary>Current status of the order.</summary>
        Status: OrderStatus
    }

/// <summary>
/// Commands that can be sent to the order grain.
/// </summary>
type OrderCommand =
    /// <summary>Create a new order for the given user with the given total.</summary>
    | CreateOrder of ownerId: int64<UserId> * total: decimal
    /// <summary>Confirm a pending order.</summary>
    | ConfirmOrder
    /// <summary>Ship a confirmed order.</summary>
    | ShipOrder
    /// <summary>Mark a shipped order as delivered.</summary>
    | DeliverOrder
    /// <summary>Cancel a pending or confirmed order.</summary>
    | CancelOrder
    /// <summary>Query the current order state (read-only).</summary>
    | GetOrder

/// <summary>
/// Grain interface for the order grain.
/// </summary>
type IOrderGrain =
    inherit IGrainWithStringKey

    /// <summary>Sends a command to the order grain and returns the result.</summary>
    abstract HandleMessage: OrderCommand -> Task<obj>

/// <summary>
/// Order grain definition and type-safe access functions.
/// </summary>
module OrderGrainDef =

    /// <summary>
    /// Advance the order status through its lifecycle.
    /// The compiler ensures every OrderStatus case is handled -- adding a new
    /// status forces updates in all match expressions (exhaustiveness checking).
    /// </summary>
    /// <param name="current">The current order status.</param>
    /// <param name="target">The desired transition target.</param>
    /// <returns>The new status if the transition is valid, or the current status if not.</returns>
    let tryTransition (current: OrderStatus) (target: OrderStatus) : OrderStatus =
        match current, target with
        | Pending, Confirmed -> Confirmed
        | Confirmed, Shipped -> Shipped
        | Shipped, Delivered -> Delivered
        | Pending, Cancelled -> Cancelled
        | Confirmed, Cancelled -> Cancelled
        | _ -> current

    /// <summary>
    /// The order grain: manages order lifecycle via typed commands with exhaustive matching.
    /// </summary>
    let order =
        grain {
            defaultState { OwnerId = 0L<UserId>; Total = 0m; Status = Pending }

            handle (fun state cmd ->
                task {
                    match cmd with
                    | CreateOrder(ownerId, total) ->
                        let next = { OwnerId = ownerId; Total = total; Status = Pending }
                        return next, box true
                    | ConfirmOrder ->
                        let next = { state with Status = tryTransition state.Status Confirmed }
                        return next, box (next.Status = Confirmed)
                    | ShipOrder ->
                        let next = { state with Status = tryTransition state.Status Shipped }
                        return next, box (next.Status = Shipped)
                    | DeliverOrder ->
                        let next = { state with Status = tryTransition state.Status Delivered }
                        return next, box (next.Status = Delivered)
                    | CancelOrder ->
                        let next = { state with Status = tryTransition state.Status Cancelled }
                        return next, box (next.Status = Cancelled)
                    | GetOrder ->
                        return state, box state
                })
        }

    /// <summary>
    /// Type-safe grain access -- IMPOSSIBLE to pass wrong ID type.
    /// Passing <c>userId 1L</c> instead of an <c>orderId</c> is a compile error.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="id">A typed <c>int64&lt;OrderId&gt;</c>.</param>
    /// <returns>A type-safe grain reference to the order grain.</returns>
    let getOrder (factory: IGrainFactory) (id: int64<OrderId>) =
        GrainRef.ofString<IOrderGrain> factory (toStringKey id)
