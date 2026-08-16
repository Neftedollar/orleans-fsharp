/// <summary>
/// Functional-runtime equivalent of the <c>counterGrain</c> <c>grain { }</c> definition in
/// <c>Program.fs</c> (now <c>[&lt;Obsolete&gt;]</c>). Same domain (increment / decrement / read)
/// rebuilt as a <c>grainContract</c> + <c>grainFor</c> pair, registered alongside the old grain.
/// The contract itself lives in <c>Shared</c> (<c>Testbed.Shared.CounterFunctionalContract</c>)
/// because the Client process calls this grain too; only the definition is server-side.
/// </summary>
module Testbed.CounterFunctional

open Orleans.FSharp
open Testbed.Shared.CounterFunctionalContract

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
