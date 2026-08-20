/// <summary>
/// Functional-runtime equivalent of <c>DashboardGrainDef.dashboard</c> in
/// <c>DashboardGrain.fs</c> (the <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>).
/// This is the twin <c>DashboardHub.fs</c> is wired to for the SignalR push on connect.
/// </summary>
/// <remarks>
/// Operation-for-command mapping against the original — the DU dispatched through one
/// <c>HandleMessage : DashboardCommand -> Task&lt;obj&gt;</c> becomes one typed operation per case,
/// and nothing else changes:
///
///   <c>GetLatestUpdate</c>   -> <c>latestUpdate</c>: same "bump the sequence number, then
///                               generate" semantics, calling the original's
///                               <c>DashboardGrainDef.generateMetrics</c> VERBATIM (it is a plain
///                               pure function, not part of the deprecated CE), returning a typed
///                               <c>DashboardUpdate</c> instead of <c>obj</c>.
///   <c>GetSequenceNumber</c> -> <c>sequenceNumber</c>: reads without advancing, now enforced by
///                               the runtime via <c>readOnly</c> instead of by convention.
///   timer <c>MetricTick</c>  -> the same declarative 2-second timer, same name, same
///                               state-advance body.
/// </remarks>
namespace SignalRRealtime.Grains

open System
open System.Threading.Tasks
open Orleans.Runtime
open Orleans.FSharp
open SignalRRealtime.Shared

type DashboardActor = private DashboardActor of unit

[<NoEquality; NoComparison>]
type DashboardApi =
    { /// <summary>Current sequence number, without advancing it (read-only).</summary>
      sequenceNumber: unit -> Task<int64>
      /// <summary>Advances the sequence number and returns a freshly generated
      /// <c>DashboardUpdate</c> -- the same "bump on every read" semantics
      /// <c>DashboardGrainDef.dashboard</c>'s <c>GetLatestUpdate</c> case has.</summary>
      latestUpdate: unit -> Task<DashboardUpdate> }

[<RequireQualifiedAccess>]
module DashboardApi =
    let contract =
        grainContract<DashboardActor, string, DashboardApi> {
            grainType "signalr-realtime.dashboard.functional"
            version 1
            stringKey

            readOnly (_.sequenceNumber)
        }

    let ref = FunctionalGrain.ref contract

module DashboardFunctionalDef =
    let dashboard =
        grainFor DashboardApi.contract {
            defaultState (fun () -> 0L)

            handle (_.sequenceNumber) (fun _context state () -> task { return state, state })

            handle
                (_.latestUpdate)
                (fun _context state () ->
                    task {
                        let next = state + 1L
                        let update = DashboardGrainDef.generateMetrics next
                        return next, update
                    })

            // Same 2-second cadence as the old grain's declarative timer -- advances the sequence
            // number on its own, independent of any caller. KeepAlive = true: this counts as
            // activity, so a connected dashboard keeps the activation alive.
            onTimer
                "MetricTick"
                (GrainTimerCreationOptions(DueTime = TimeSpan.FromSeconds 2.0, Period = TimeSpan.FromSeconds 2.0, KeepAlive = true))
                (fun _context state -> task { return state + 1L })
        }
