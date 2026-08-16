/// <summary>
/// Functional-runtime equivalent of <c>TodoGrainDef.todos</c> in <c>TodoGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Full parity: all three commands
/// (add / list / toggle) and explicit <c>stateFrom</c> persistence, matching the original's
/// <c>persist "Default"</c>. This is the twin the server's Fable.Remoting API (<c>Program.fs</c>'s
/// <c>todoApi</c>, exposed at <c>/api/ITodoApi/*</c>) is wired to.
/// </summary>
namespace FableFullstack.Grains

open System
open System.Threading.Tasks
open Orleans.FSharp
open FableFullstack.Shared

type TodoActor = private TodoActor of unit

[<NoEquality; NoComparison>]
type TodoApi =
    { /// <summary>Adds a new todo item with the given text.</summary>
      addTodo: string -> Task<Todo>
      /// <summary>Lists all todo items (read-only).</summary>
      getTodos: unit -> Task<Todo list>
      /// <summary>Toggles the done status of the todo with the given id; <c>None</c> if not found.</summary>
      toggleTodo: Guid -> Task<Todo option> }

[<RequireQualifiedAccess>]
module TodoApi =
    let contract =
        grainContract<TodoActor, string, TodoApi> () {
            grainType "fable-fullstack.todos.functional"
            version 1
            stringKey

            readOnly (_.getTodos)
        }

    let ref = FunctionalGrain.ref contract

module TodoFunctionalDef =
    let todoState = PersistentState.create<Todo list> "state" "Default"

    let todos =
        grainFor TodoApi.contract {
            defaultState (fun () -> ([]: Todo list))
            stateFrom todoState

            handle
                (_.addTodo)
                (fun context state text ->
                    task {
                        let todo =
                            { Id = Guid.NewGuid()
                              Text = text
                              Done = false }

                        let next = todo :: state
                        let storage = context.persistentState todoState
                        storage.State <- next
                        do! storage.WriteStateAsync()
                        return next, todo
                    })

            handle (_.getTodos) (fun _context state () -> task { return state, state })

            handle
                (_.toggleTodo)
                (fun context state id ->
                    task {
                        let toggled =
                            state
                            |> List.tryFind (fun t -> t.Id = id)
                            |> Option.map (fun t -> { t with Done = not t.Done })

                        match toggled with
                        | Some updated ->
                            let next = state |> List.map (fun t -> if t.Id = id then updated else t)
                            let storage = context.persistentState todoState
                            storage.State <- next
                            do! storage.WriteStateAsync()
                            return next, Some updated
                        | None -> return state, None
                    })
        }
