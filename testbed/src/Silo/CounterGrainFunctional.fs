/// <summary>
/// Functional-runtime equivalent of the <c>counterGrain</c> <c>grain { }</c> definition in
/// <c>Program.fs</c> (now <c>[&lt;Obsolete&gt;]</c>). Same domain (increment / decrement / read)
/// rebuilt as a <c>grainContract</c> + <c>grainFor</c> pair, registered alongside the old grain.
/// </summary>
module Testbed.CounterFunctional

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
        grainContract<CounterActor, string, CounterApi> () {
            grainType "testbed.counter.functional"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

let counterDefinition =
    grainFor CounterApi.contract {
        defaultState (fun () -> 0)

        handle (_.increment) (fun _context state () -> task { return state + 1, state + 1 })

        handle
            (_.decrement)
            (fun _context state () ->
                task {
                    let next = max 0 (state - 1)
                    return next, next
                })

        handle (_.value) (fun _context state () -> task { return state, state })
    }
