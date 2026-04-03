namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Observer interface for receiving chat messages.
/// Clients implement this interface to receive real-time notifications from a chat grain.
/// </summary>
type IChatObserver =
    inherit IGrainObserver

    /// <summary>
    /// Called when a new message is sent to the chat room.
    /// </summary>
    /// <param name="sender">The name of the message sender.</param>
    /// <param name="message">The message content.</param>
    /// <returns>A Task that completes when the notification has been processed.</returns>
    abstract ReceiveMessage: sender: string * message: string -> Task

/// <summary>
/// Grain interface for the chat grain with pub/sub observer support.
/// </summary>
type IChatGrain =
    inherit IGrainWithStringKey

    /// <summary>Subscribe an observer to receive chat messages.</summary>
    /// <param name="observer">The observer reference to subscribe.</param>
    /// <returns>A Task that completes when the subscription is registered.</returns>
    abstract Subscribe: observer: IChatObserver -> Task

    /// <summary>Unsubscribe an observer from receiving chat messages.</summary>
    /// <param name="observer">The observer reference to unsubscribe.</param>
    /// <returns>A Task that completes when the subscription is removed.</returns>
    abstract Unsubscribe: observer: IChatObserver -> Task

    /// <summary>Send a message to all subscribed observers.</summary>
    /// <param name="sender">The name of the message sender.</param>
    /// <param name="message">The message content.</param>
    /// <returns>A Task that completes when all observers have been notified.</returns>
    abstract SendMessage: sender: string * message: string -> Task

    /// <summary>Gets the current number of active subscribers.</summary>
    /// <returns>A Task containing the subscriber count.</returns>
    abstract GetSubscriberCount: unit -> Task<int>
