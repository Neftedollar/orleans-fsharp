/// <summary>
/// Functional-runtime equivalent of <c>CounterGrainDef.counter</c> in
/// <c>CounterGrain.fs</c> (the <c>grain { }</c> CE / universal-pattern original — now
/// <c>[&lt;Obsolete&gt;]</c>, see the deprecation pass in docs/functional-grains.md). Same
/// domain (increment / decrement / read a count), rebuilt as a <c>grainContract</c> +
/// <c>grainFor</c> pair so the two authoring styles sit side by side for comparison.
/// </summary>
namespace Counter.Contracts

open System.Threading.Tasks
open Orleans.FSharp

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      decrement: unit -> Task<int>
      value: unit -> Task<int> }

[<RequireQualifiedAccess>]
module CounterApi =
    let contract =
        grainContract<CounterActor, int64, CounterApi> () {
            grainType "counter.functional"
            version 1
            int64Key
        }

    let ref = FunctionalGrain.ref contract

namespace Counter.Server

open Counter.Contracts
open Orleans.FSharp

module Definition =
    let counterDefinition =
        grainFor CounterApi.contract {
            defaultState (fun () -> 0)

            handle
                (_.increment)
                (fun _context state () ->
                    task {
                        let next = state + 1
                        return next, next
                    })

            handle
                (_.decrement)
                (fun _context state () ->
                    task {
                        let next = max 0 (state - 1)
                        return next, next
                    })

            handle (_.value) (fun _context state () -> task { return state, state })
        }
