/// <summary>
/// Functional-runtime equivalent of <c>DashboardGrainDef.dashboard</c> in
/// <c>DashboardGrain.fs</c> (the <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>).
/// Same domain (a monotonically increasing sequence number) rebuilt as a
/// <c>grainContract</c> + <c>grainFor</c> pair. Kept small: it demonstrates registration
/// alongside the old grain, not the timer-driven metric generation.
/// </summary>
namespace SignalRRealtime.Grains

open System.Threading.Tasks
open Orleans.FSharp

type DashboardActor = private DashboardActor of unit

[<NoEquality; NoComparison>]
type DashboardApi =
    { tick: unit -> Task<int64>
      sequenceNumber: unit -> Task<int64> }

[<RequireQualifiedAccess>]
module DashboardApi =
    let contract =
        grainContract<DashboardActor, string, DashboardApi> () {
            grainType "signalr-realtime.dashboard.functional"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

module DashboardFunctionalDef =
    let dashboard =
        grainFor DashboardApi.contract {
            defaultState (fun () -> 0L)

            handle (_.tick) (fun _context state () -> task { return state + 1L, state + 1L })

            handle (_.sequenceNumber) (fun _context state () -> task { return state, state })
        }
