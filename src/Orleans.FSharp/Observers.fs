namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Threading.Tasks
open Orleans

/// <summary>
/// Module for managing grain observers -- pub/sub between grains and clients.
/// Observers enable real-time notifications from grains to external subscribers.
/// Uses Orleans <c>IGrainFactory.CreateObjectReference</c> and <c>DeleteObjectReference</c>
/// to manage the lifecycle of observer references.
/// </summary>
[<RequireQualifiedAccess>]
module Observer =

    /// <summary>
    /// Creates a grain object reference for a local observer instance.
    /// This turns a local F# object implementing <c>IGrainObserver</c> into an
    /// Orleans-addressable reference that can be passed to grains for callbacks.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="observer">The local observer instance to create a reference for.</param>
    /// <typeparam name="'T">The observer interface type, must implement <c>IGrainObserver</c>.</typeparam>
    /// <returns>An Orleans-addressable observer reference.</returns>
    let createRef<'T when 'T :> IGrainObserver> (factory: IGrainFactory) (observer: 'T) : 'T =
        factory.CreateObjectReference<'T>(observer)

    /// <summary>
    /// Deletes a grain object reference, releasing the associated resources.
    /// MUST be called when the observer is no longer needed to prevent memory leaks.
    /// After deletion, the observer reference is no longer valid for receiving notifications.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="observerRef">The observer reference to delete.</param>
    /// <typeparam name="'T">The observer interface type, must implement <c>IGrainObserver</c>.</typeparam>
    let deleteRef<'T when 'T :> IGrainObserver> (factory: IGrainFactory) (observerRef: 'T) : unit =
        factory.DeleteObjectReference<'T>(observerRef)

    /// <summary>
    /// Subscribes an observer and returns an <c>IDisposable</c> that automatically
    /// calls <c>deleteRef</c> when disposed. This is the recommended way to manage
    /// observer lifecycle, as it prevents memory leaks via the standard dispose pattern.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="observer">The local observer instance to subscribe.</param>
    /// <typeparam name="'T">The observer interface type, must implement <c>IGrainObserver</c>.</typeparam>
    /// <returns>An <c>IDisposable</c> that deletes the observer reference when disposed.</returns>
    let subscribe<'T when 'T :> IGrainObserver> (factory: IGrainFactory) (observer: 'T) : IDisposable =
        let observerRef = createRef<'T> factory observer
        let mutable disposed = false

        { new IDisposable with
            member _.Dispose() =
                if not disposed then
                    disposed <- true
                    deleteRef<'T> factory observerRef
        }

/// <summary>
/// F# wrapper over a subscription management pattern for grain-side observer management.
/// Handles subscription registration, expiry of stale subscriptions, notification dispatch,
/// and graceful error handling when observers become unreachable.
/// </summary>
/// <typeparam name="'T">The observer interface type, must implement <c>IGrainObserver</c>.</typeparam>
type FSharpObserverManager<'T when 'T :> IGrainObserver and 'T : equality>(expiryDuration: TimeSpan) =

    /// <summary>Internal storage mapping observer to its last-seen timestamp.</summary>
    let subscriptions = Dictionary<'T, DateTime>()

    /// <summary>
    /// Removes all subscriptions that have not been renewed within the expiry duration.
    /// Called automatically before notify to clean up stale observers.
    /// </summary>
    let clearExpired () =
        let now = DateTime.UtcNow
        let expired =
            subscriptions
            |> Seq.filter (fun kvp -> now - kvp.Value > expiryDuration)
            |> Seq.map (fun kvp -> kvp.Key)
            |> Seq.toList

        for key in expired do
            subscriptions.Remove(key) |> ignore

    /// <summary>
    /// Subscribes an observer. If the observer is already subscribed,
    /// its timestamp is refreshed (re-subscription extends the expiry window).
    /// </summary>
    /// <param name="observer">The observer reference to subscribe.</param>
    member _.Subscribe(observer: 'T) : unit =
        subscriptions.[observer] <- DateTime.UtcNow

    /// <summary>
    /// Unsubscribes an observer, immediately removing it from the subscription list.
    /// </summary>
    /// <param name="observer">The observer reference to unsubscribe.</param>
    member _.Unsubscribe(observer: 'T) : unit =
        subscriptions.Remove(observer) |> ignore

    /// <summary>
    /// Notifies all subscribed observers by invoking the specified action.
    /// Automatically clears expired subscriptions before notifying.
    /// Each observer notification has a timeout (5 seconds by default). If an observer
    /// throws an exception or times out during notification (e.g., because it has been
    /// disconnected), the exception is caught and the observer is removed from the
    /// subscription list. Other observers continue to be notified.
    /// </summary>
    /// <param name="action">The notification action to invoke on each observer. Should return a Task.</param>
    /// <returns>A Task that completes when all notifications have been dispatched.</returns>
    member _.Notify(action: 'T -> Task<unit>) : Task<unit> =
        task {
            clearExpired ()

            let observers = subscriptions.Keys |> Seq.toList
            let notifyTimeout = TimeSpan.FromSeconds(5.0)

            for observer in observers do
                try
                    let notifyTask = action observer :> Task
                    let! completed = Task.WhenAny(notifyTask, Task.Delay(notifyTimeout))

                    if not (obj.ReferenceEquals(completed, notifyTask)) then
                        // Timed out -- observer is unreachable
                        subscriptions.Remove(observer) |> ignore
                    elif notifyTask.IsFaulted then
                        subscriptions.Remove(observer) |> ignore
                with
                | _ ->
                    // Observer is unreachable or failed -- remove it gracefully
                    subscriptions.Remove(observer) |> ignore
        }

    /// <summary>
    /// Notifies all subscribed observers using a C#-compatible <c>Func&lt;'T, Task&gt;</c> delegate.
    /// This overload is provided for convenient use from C# CodeGen projects.
    /// Automatically clears expired subscriptions before notifying.
    /// If an observer throws an exception during notification, it is removed gracefully.
    /// </summary>
    /// <param name="action">The notification delegate to invoke on each observer.</param>
    /// <returns>A Task that completes when all notifications have been dispatched.</returns>
    member this.NotifyAsync(action: Func<'T, Task>) : Task =
        this.Notify(fun observer -> task { do! action.Invoke(observer) }) :> Task

    /// <summary>
    /// Gets the number of currently active (non-expired) subscriptions.
    /// </summary>
    member _.Count : int =
        clearExpired ()
        subscriptions.Count
