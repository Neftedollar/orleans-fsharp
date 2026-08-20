/// <summary>
/// Experiment 8 — grain observers from a functional grain, with the observer interface declared
/// in the sibling C# project so Orleans' proxy generator can see it.
/// </summary>
namespace FeatureTour.ObserverTour

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open FeatureTour.Interop
open Orleans.FSharp

type NotifierActor = private NotifierActor of unit

/// <summary>The notifier's state: the stock observer manager, held in state like any value.</summary>
type NotifierState =
    { manager: FSharpObserverManager<ITourObserver> }

[<NoEquality; NoComparison>]
type NotifierApi =
    { /// Registers an observer reference; replies with the live subscriber count.
      subscribe: ITourObserver -> Task<int>
      /// Removes an observer reference; replies with the live subscriber count.
      unsubscribe: ITourObserver -> Task<int>
      /// Notifies every subscriber; replies with how many were notified.
      notify: string -> Task<int> }

[<RequireQualifiedAccess>]
module NotifierApi =
    let contract =
        grainContract<NotifierActor, string, NotifierApi> {
            grainType "tour.notifier"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module NotifierDefinition =
    let definition =
        grainFor NotifierApi.contract {
            defaultState (fun () ->
                { manager = FSharpObserverManager<ITourObserver>(TimeSpan.FromMinutes 5.0) })

            handle
                (_.subscribe)
                (fun _context state observer ->
                    task {
                        state.manager.Subscribe observer
                        return state, state.manager.Count
                    })

            handle
                (_.unsubscribe)
                (fun _context state observer ->
                    task {
                        state.manager.Unsubscribe observer
                        return state, state.manager.Count
                    })

            handle
                (_.notify)
                (fun _context state text ->
                    task {
                        let notified = state.manager.Count
                        do! state.manager.Notify(fun observer -> task { do! observer.OnTourEvent text })
                        return state, notified
                    })
        }

/// <summary>A client-side observer that records everything it is told.</summary>
[<Sealed>]
type RecordingObserver() =
    let received = ConcurrentQueue<string>()

    /// <summary>Everything this observer has been notified of, in arrival order.</summary>
    member _.Received = received |> List.ofSeq

    interface ITourObserver with
        member _.OnTourEvent(message: string) =
            received.Enqueue message
            Task.CompletedTask
