namespace FableFullstack.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp
open FableFullstack.Shared

/// <summary>
/// State for the todo grain: a list of todo items.
/// </summary>
type TodoState =
    { Todos: Todo list }

/// <summary>
/// Commands that can be sent to the todo grain.
/// </summary>
type TodoCommand =
    /// <summary>Get all todo items.</summary>
    | GetTodos
    /// <summary>Add a new todo item with the given text.</summary>
    | AddTodo of text: string
    /// <summary>Toggle the done status of a todo item by its id.</summary>
    | ToggleTodo of id: Guid

/// <summary>
/// Grain interface for the todo grain.
/// </summary>
type ITodoGrain =
    inherit IGrainWithStringKey

    /// <summary>Sends a command to the todo grain and returns the result.</summary>
    abstract HandleMessage: TodoCommand -> Task<obj>

/// <summary>
/// Module containing the todo grain definition using the grain computation expression.
/// </summary>
module TodoGrainDef =

    /// <summary>
    /// The todo grain: manages a list of todo items with add, toggle, and list operations.
    /// </summary>
    let todos =
        grain {
            defaultState { Todos = [] }

            handle (fun state cmd ->
                task {
                    match cmd with
                    | GetTodos ->
                        return state, box state.Todos

                    | AddTodo text ->
                        let todo =
                            { Id = Guid.NewGuid()
                              Text = text
                              Done = false }

                        let newState = { Todos = todo :: state.Todos }
                        return newState, box todo

                    | ToggleTodo id ->
                        let toggled =
                            state.Todos
                            |> List.tryFind (fun t -> t.Id = id)
                            |> Option.map (fun t -> { t with Done = not t.Done })

                        match toggled with
                        | Some updated ->
                            let newTodos =
                                state.Todos
                                |> List.map (fun t -> if t.Id = id then updated else t)

                            let newState = { Todos = newTodos }
                            return newState, box (Some updated)
                        | None ->
                            return state, box (None: Todo option)
                })

            persist "Default"
        }
