namespace ChatRoom.Grains

open System.Threading.Tasks
open Orleans

/// <summary>
/// Observer interface for receiving chat messages.
/// Clients implement this to receive real-time notifications from the chat grain.
/// </summary>
type IChatObserver =
    inherit IGrainObserver

    /// <summary>Called when a new message arrives in the chat room.</summary>
    abstract ReceiveMessage: sender: string * message: string -> Task

/// <summary>
/// Grain interface for the chat room grain.
/// </summary>
type IChatGrain =
    inherit IGrainWithStringKey

    /// <summary>Subscribe an observer to receive chat messages.</summary>
    abstract Subscribe: observer: IChatObserver -> Task

    /// <summary>Unsubscribe an observer from receiving chat messages.</summary>
    abstract Unsubscribe: observer: IChatObserver -> Task

    /// <summary>Send a message to all subscribers in the chat room.</summary>
    abstract SendMessage: sender: string * message: string -> Task

    /// <summary>Gets the number of active subscribers.</summary>
    abstract GetSubscriberCount: unit -> Task<int>
