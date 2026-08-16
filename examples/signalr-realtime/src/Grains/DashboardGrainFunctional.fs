/// <summary>
/// Functional-runtime equivalent of <c>DashboardGrainDef.dashboard</c> in
/// <c>DashboardGrain.fs</c> (the <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>).
/// Full parity: reuses <c>DashboardGrainDef.generateMetrics</c> verbatim (a plain pure function,
/// not part of the deprecated CE) so <c>latestUpdate</c> produces the exact same randomized
/// <c>DashboardUpdate</c> the original did, and declares the same 2-second <c>onTimer</c> that
/// advances the sequence number on its own, independent of any caller. This is the twin
/// <c>DashboardHub.fs</c> is wired to for the SignalR push on connect.
/// </summary>
namespace SignalRRealtime.Grains

open System
open System.Threading.Tasks
open Orleans.Runtime
open Orleans.FSharp
open SignalRRealtime.Shared

type DashboardActor = private DashboardActor of unit

[<NoEquality; NoComparison>]
type DashboardApi =
    { /// <summary>Advances the sequence number by one and returns the new value (no metrics).</summary>
      tick: unit -> Task<int64>
      /// <summary>Current sequence number, without advancing it (read-only).</summary>
      sequenceNumber: unit -> Task<int64>
      /// <summary>Advances the sequence number and returns a freshly generated
      /// <c>DashboardUpdate</c> -- the same "bump on every read" semantics
      /// <c>DashboardGrainDef.dashboard</c>'s <c>GetLatestUpdate</c> case has.</summary>
      latestUpdate: unit -> Task<DashboardUpdate> }

[<RequireQualifiedAccess>]
module DashboardApi =
    let contract =
        grainContract<DashboardActor, string, DashboardApi> () {
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

            handle (_.tick) (fun _context state () -> task { return state + 1L, state + 1L })

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
