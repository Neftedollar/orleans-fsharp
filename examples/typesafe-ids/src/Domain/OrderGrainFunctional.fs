/// <summary>
/// Functional-runtime equivalent of <c>OrderGrainDef.order</c> in <c>OrderGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Same order lifecycle
/// (create/confirm/ship/deliver/cancel), reusing <c>OrderGrainDef.tryTransition</c> verbatim --
/// that function is plain pattern matching, not part of the deprecated CE, so both authoring
/// styles share the exact same exhaustive-matching state machine with zero drift risk. The
/// order's own identity is the contract's typed <c>int64&lt;OrderId&gt;</c> key
/// (<c>int64KeyMapped</c>), the functional runtime's own equivalent of the "impossible to pass the
/// wrong ID type" guarantee this example demonstrates with units of measure for the user grain --
/// see <c>UserGrainFunctional.fs</c> and docs/functional-grains.md, "Key-codec identity rules".
/// </summary>
namespace TypeSafeIds.Domain

open System.Threading.Tasks
open Orleans.FSharp
open TypeSafeIds.Domain.Ids

type OrderActor = private OrderActor of unit

[<NoEquality; NoComparison>]
type OrderApi =
    { /// <summary>Creates the order for the given owner and total, replacing any previous state.</summary>
      create: int64<UserId> * decimal -> Task<bool>
      /// <summary>Confirm a pending order. No-op (returns <c>false</c>) if not currently pending.</summary>
      confirm: unit -> Task<bool>
      /// <summary>Ship a confirmed order. No-op if not currently confirmed.</summary>
      ship: unit -> Task<bool>
      /// <summary>Deliver a shipped order. No-op if not currently shipped.</summary>
      deliver: unit -> Task<bool>
      /// <summary>Cancel a pending or confirmed order. No-op otherwise.</summary>
      cancel: unit -> Task<bool>
      /// <summary>Current order state (read-only).</summary>
      get: unit -> Task<OrderState> }

[<RequireQualifiedAccess>]
module OrderApi =
    let contract =
        grainContract<OrderActor, int64<OrderId>, OrderApi> {
            grainType "typesafe-ids.order.functional"
            version 1
            int64KeyMapped rawId orderId

            readOnly (_.get)
        }

    let ref = FunctionalGrain.ref contract

module OrderFunctionalDef =
    let order =
        grainFor OrderApi.contract {
            defaultState (fun () ->
                { OwnerId = 0L<UserId>
                  Total = 0m
                  Status = Pending })

            handle
                (_.create)
                (fun _context _state (ownerId, total) ->
                    task {
                        let next = { OwnerId = ownerId; Total = total; Status = Pending }
                        return next, true
                    })

            handle
                (_.confirm)
                (fun _context state () ->
                    task {
                        let next = { state with Status = OrderGrainDef.tryTransition state.Status Confirmed }
                        return next, next.Status = Confirmed
                    })

            handle
                (_.ship)
                (fun _context state () ->
                    task {
                        let next = { state with Status = OrderGrainDef.tryTransition state.Status Shipped }
                        return next, next.Status = Shipped
                    })

            handle
                (_.deliver)
                (fun _context state () ->
                    task {
                        let next = { state with Status = OrderGrainDef.tryTransition state.Status Delivered }
                        return next, next.Status = Delivered
                    })

            handle
                (_.cancel)
                (fun _context state () ->
                    task {
                        let next = { state with Status = OrderGrainDef.tryTransition state.Status Cancelled }
                        return next, next.Status = Cancelled
                    })

            handle (_.get) (fun _context state () -> task { return state, state })
        }
