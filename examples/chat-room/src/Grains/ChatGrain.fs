namespace ChatRoom.Grains

open System
open Orleans.FSharp

/// <summary>
/// Chat room state tracks recent message history and the observer manager.
/// The FSharpObserverManager is stored as an obj in the grain context's additional state
/// or as part of the grain state itself for simplicity.
/// </summary>
type ChatState =
    {
        /// <summary>Recent messages (sender, message, timestamp).</summary>
        Messages: (string * string * DateTime) list
        /// <summary>Observer manager for pub/sub notifications.</summary>
        ObserverManager: FSharpObserverManager<IChatObserver>
    }

/// <summary>
/// Module containing the chat grain definition.
/// Uses handleWithServices to access the GrainContext for observer management.
/// </summary>
module ChatGrainDef =

    /// <summary>
    /// The chat grain definition using the grain computation expression.
    /// Manages observer subscriptions and message history.
    /// </summary>
    let chat =
        grain {
            defaultState
                { Messages = []
                  ObserverManager = FSharpObserverManager<IChatObserver>(TimeSpan.FromMinutes(5.0)) }

            handle (fun state msg ->
                task {
                    match msg with
                    | Subscribe observer ->
                        state.ObserverManager.Subscribe(observer)
                        return state, box ()
                    | Unsubscribe observer ->
                        state.ObserverManager.Unsubscribe(observer)
                        return state, box ()
                    | SendMessage(sender, message) ->
                        let entry = (sender, message, DateTime.UtcNow)

                        let newState =
                            { state with
                                Messages = entry :: state.Messages |> List.truncate 100
                            }

                        do!
                            state.ObserverManager.Notify(fun observer ->
                                task { do! observer.ReceiveMessage(sender, message) })

                        return newState, box ()
                    | GetSubscriberCount ->
                        return state, box state.ObserverManager.Count
                })
        }
