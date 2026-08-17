/// <summary>
/// Functional-runtime equivalent of <c>CounterGrainDef.counter</c> in
/// <c>CounterGrain.fs</c> (the <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>).
/// Same domain (increment / decrement / read a count), rebuilt as a <c>grainContract</c> +
/// <c>grainFor</c> pair. See docs/functional-grains.md in the Orleans.FSharp repo for the
/// full migration guide.
/// </summary>
namespace HelloWorld.Grains

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
            grainType "hello-world.counter.functional"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

module CounterFunctionalDef =
    let counter =
        grainFor CounterApi.contract {
            defaultState (fun () -> 0)

            handle (_.increment) (fun _context state () -> task { return state + 1, state + 1 })

            handle
                (_.decrement)
                (fun _context state () ->
                    task {
                        let next = state - 1
                        return next, next
                    })

            handle (_.value) (fun _context state () -> task { return state, state })
        }
