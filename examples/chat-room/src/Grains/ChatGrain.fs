namespace ChatRoom.Grains

open System
open Orleans.FSharp

/// <summary>
/// Chat room state tracks recent message history.
/// </summary>
type ChatState =
    { Messages: (string * string * DateTime) list }

/// <summary>
/// Commands for the chat grain.
/// </summary>
type ChatCommand =
    | Send of sender: string * message: string
    | GetHistory

/// <summary>
/// Module containing the chat grain definition.
/// The actual observer management is handled in the C# CodeGen grain class
/// since it requires direct grain access. The F# definition tracks message history.
/// </summary>
module ChatGrainDef =

    /// <summary>
    /// The chat grain definition: stores message history in state.
    /// </summary>
    let chat =
        grain {
            defaultState { Messages = [] }

            handle (fun state cmd ->
                task {
                    match cmd with
                    | Send(sender, message) ->
                        let entry = (sender, message, DateTime.UtcNow)
                        let newState = { Messages = entry :: state.Messages |> List.truncate 100 }
                        return newState, box ()
                    | GetHistory ->
                        return state, box state.Messages
                })
        }
