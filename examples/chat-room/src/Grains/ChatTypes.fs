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
/// Commands for the chat grain, enabling the HandleMessage pattern.
/// </summary>
type ChatMessage =
    /// <summary>Subscribe an observer to receive chat messages.</summary>
    | Subscribe of observer: IChatObserver
    /// <summary>Unsubscribe an observer from receiving chat messages.</summary>
    | Unsubscribe of observer: IChatObserver
    /// <summary>Send a message to all subscribers in the chat room.</summary>
    | SendMessage of sender: string * message: string
    /// <summary>Gets the number of active subscribers.</summary>
    | GetSubscriberCount

/// <summary>
/// Grain interface for the chat room grain using the HandleMessage pattern.
/// </summary>
type IChatGrain =
    inherit IGrainWithStringKey

    /// <summary>Sends a command to the chat grain and returns a boxed result.</summary>
    abstract HandleMessage: ChatMessage -> Task<obj>
