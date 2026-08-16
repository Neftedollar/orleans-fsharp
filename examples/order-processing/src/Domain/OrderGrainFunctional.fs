/// <summary>
/// Functional-runtime equivalent of <c>OrderGrainDef.order</c> in <c>OrderGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Same domain slice (place an
/// order, read its status) rebuilt as a <c>grainContract</c> + <c>grainFor</c> pair. Kept small
/// on purpose: the reminder/timer status-check machinery from the original is demonstrated via
/// <c>onReminder</c> / <c>onTimer</c> operations in <c>grainFor { }</c> -- see
/// docs/functional-grains.md -- and is not duplicated here.
/// </summary>
namespace OrderProcessing.Domain

open System.Threading.Tasks
open Orleans.FSharp

type OrderActor = private OrderActor of unit

[<NoEquality; NoComparison>]
type OrderApi =
    { place: string -> Task<string>
      status: unit -> Task<string option> }

[<RequireQualifiedAccess>]
module OrderApi =
    let contract =
        grainContract<OrderActor, string, OrderApi> () {
            grainType "order-processing.order.functional"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

module OrderFunctionalDef =
    let order =
        grainFor OrderApi.contract {
            defaultState (fun () -> None)

            handle
                (_.place)
                (fun _context _state description -> task { return Some description, description })

            handle (_.status) (fun _context state () -> task { return state, state })
        }
