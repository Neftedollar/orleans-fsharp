/// <summary>
/// Feature 4 — Orleans request context across the functional transport: a client-set correlation
/// id read by the handler, and a handler-set value flowing onward into a grain-to-grain call.
/// </summary>
namespace FeatureTour.RequestContextTour

open System.Threading.Tasks
open Orleans.FSharp

/// <summary>The two request-context keys this section uses.</summary>
[<RequireQualifiedAccess>]
module ContextKeys =
    /// <summary>Set by the CLIENT before the call, read by the front grain.</summary>
    [<Literal>]
    let Correlation = "tour.correlationId"

    /// <summary>Set by the FRONT GRAIN's handler, read by the downstream grain.</summary>
    [<Literal>]
    let Hop = "tour.hop"

type DownstreamActor = private DownstreamActor of unit

/// <summary>
/// Both context keys as one activation sees them.
/// </summary>
/// <remarks>
/// This is a RECORD and not the obvious <c>string option * string option</c> tuple on purpose.
/// A tuple whose elements are generic FSharp.Core types (<c>option</c>, <c>list</c>, <c>Map</c>)
/// does not survive the functional transport today — see the "F# tuples of FSharp.Core generics"
/// wall in this example's README. A record with the same two fields works.
/// </remarks>
type ContextView =
    { correlation: string option
      hop: string option }

[<NoEquality; NoComparison>]
type DownstreamApi =
    { /// Reports both keys as this activation sees them.
      inspect: unit -> Task<ContextView> }

[<RequireQualifiedAccess>]
module DownstreamApi =
    let contract =
        grainContract<DownstreamActor, string, DownstreamApi> () {
            grainType "tour.context.downstream"
            version 1
            stringKey

            readOnly (_.inspect)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module DownstreamDefinition =
    let definition =
        grainFor DownstreamApi.contract {
            defaultState (fun () -> ())

            handle
                (_.inspect)
                (fun context state () ->
                    task {
                        return
                            state,
                            { correlation = context.tryGetRequestContext<string> ContextKeys.Correlation
                              hop = context.tryGetRequestContext<string> ContextKeys.Hop }
                    })
        }

type FrontActor = private FrontActor of unit

/// <summary>What one <c>trace</c> call reports about both hops.</summary>
type TraceReport =
    { correlationSeenByFront: string option
      correlationSeenByDownstream: string option
      hopSetByFront: string
      hopSeenByDownstream: string option }

[<NoEquality; NoComparison>]
type FrontApi =
    { /// Reads the client's correlation id, adds a hop marker, calls downstream, reports both.
      trace: unit -> Task<TraceReport> }

[<RequireQualifiedAccess>]
module FrontApi =
    let contract =
        grainContract<FrontActor, string, FrontApi> () {
            grainType "tour.context.front"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module FrontDefinition =
    let definition =
        grainFor FrontApi.contract {
            defaultState (fun () -> ())

            handle
                (_.trace)
                (fun context state () ->
                    task {
                        // Propagated automatically by Orleans from the caller's ambient context.
                        let correlation = context.tryGetRequestContext<string> ContextKeys.Correlation

                        // Add our own marker; it flows onward on every call this turn makes.
                        let hop = $"front:{context.key}"
                        context.setRequestContext ContextKeys.Hop hop

                        let downstream = DownstreamApi.ref context.grainFactory "downstream-1"
                        let! seen = downstream.inspect ()

                        return
                            state,
                            { correlationSeenByFront = correlation
                              correlationSeenByDownstream = seen.correlation
                              hopSetByFront = hop
                              hopSeenByDownstream = seen.hop }
                    })
        }
