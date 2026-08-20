/// <summary>
/// Feature 5 — cooperative cancellation: a deliberately slow operation called through
/// <c>rawRef</c>'s <c>callCancellable</c>, cancelled mid-flight, with the handler's own view of
/// <c>context.cancellationToken</c> printed beside the caller's.
/// </summary>
namespace FeatureTour.Cancellation

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Orleans.FSharp

/// <summary>What the target observed, recorded in-process because it cannot travel in a reply
/// of a call that never returns one.</summary>
[<RequireQualifiedAccess>]
module TargetObservation =
    let private entries = ConcurrentQueue<string>()

    /// <summary>Record what the handler saw.</summary>
    let record (text: string) = entries.Enqueue text

    /// <summary>Everything observed so far.</summary>
    let all () = entries |> List.ofSeq

type SlowActor = private SlowActor of unit

[<NoEquality; NoComparison>]
type SlowApi =
    { /// Sleeps for the requested milliseconds, observing context.cancellationToken.
      slow: int -> Task<string> }

[<RequireQualifiedAccess>]
module SlowApi =
    let contract =
        grainContract<SlowActor, string, SlowApi> {
            grainType "tour.slow"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract
    let rawRef = FunctionalGrain.rawRef contract

[<RequireQualifiedAccess>]
module SlowDefinition =
    let definition =
        grainFor SlowApi.contract {
            defaultState (fun () -> ())

            handle
                (_.slow)
                (fun context state milliseconds ->
                    task {
                        TargetObservation.record
                            $"handler entered; token can be cancelled = {context.cancellationToken.CanBeCanceled}"

                        try
                            do! Task.Delay(milliseconds, context.cancellationToken)
                            TargetObservation.record "handler ran to completion (token never signalled)"
                            return state, "completed"
                        with :? OperationCanceledException ->
                            // Cancellation is cooperative and rolls NOTHING back: anything this
                            // handler already wrote or sent stays done.
                            TargetObservation.record "handler observed cancellation on context.cancellationToken"
                            return state, "cancelled"
                    })
        }
