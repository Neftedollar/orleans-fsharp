/// <summary>
/// Functional-runtime equivalent of <c>RouterGrainDef.router</c> in <c>RouterGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Same routing behavior, reusing
/// <c>Routing.routeMessage</c> and the <c>Spam</c> active pattern verbatim -- both are plain F#
/// functions/patterns, not part of the deprecated CE, so the "impossible in C#" active-pattern
/// composition this example demonstrates is unaffected by which grain-authoring style calls it.
/// </summary>
namespace TypeSafeIds.Domain

open System.Threading.Tasks
open Orleans.FSharp
open TypeSafeIds.Domain.Routing

type RouterActor = private RouterActor of unit

[<NoEquality; NoComparison>]
type RouterApi =
    { /// <summary>Classifies and routes an incoming message, returning the destination queue name.</summary>
      route: IncomingMessage -> Task<string>
      /// <summary>Processed/dropped message counts so far (read-only).</summary>
      stats: unit -> Task<int * int> }

[<RequireQualifiedAccess>]
module RouterApi =
    let contract =
        grainContract<RouterActor, string, RouterApi> {
            grainType "typesafe-ids.router.functional"
            version 1
            stringKey

            readOnly (_.stats)
        }

    let ref = FunctionalGrain.ref contract

module RouterFunctionalDef =
    let router =
        grainFor RouterApi.contract {
            defaultState (fun () -> { Processed = 0; Dropped = 0 })

            handle
                (_.route)
                (fun _context state msg ->
                    task {
                        let route = routeMessage msg

                        match msg with
                        | Spam -> return { state with Dropped = state.Dropped + 1 }, route
                        | _ -> return { state with Processed = state.Processed + 1 }, route
                    })

            handle (_.stats) (fun _context state () -> task { return state, (state.Processed, state.Dropped) })
        }
