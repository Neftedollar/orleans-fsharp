/// <summary>
/// Functional-runtime equivalent of <c>TodoGrainDef.todos</c> in <c>TodoGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Same domain (add a todo, list
/// them) rebuilt as a <c>grainContract</c> + <c>grainFor</c> pair. Kept small: covers add/list,
/// not toggle.
/// </summary>
namespace FableFullstack.Grains

open System.Threading.Tasks
open Orleans.FSharp
open FableFullstack.Shared

type TodoActor = private TodoActor of unit

[<NoEquality; NoComparison>]
type TodoApi =
    { addTodo: string -> Task<Todo>
      getTodos: unit -> Task<Todo list> }

[<RequireQualifiedAccess>]
module TodoApi =
    let contract =
        grainContract<TodoActor, string, TodoApi> () {
            grainType "fable-fullstack.todos.functional"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

module TodoFunctionalDef =
    let todos =
        grainFor TodoApi.contract {
            defaultState (fun () -> ([]: Todo list))

            handle
                (_.addTodo)
                (fun _context state text ->
                    task {
                        let todo =
                            { Id = System.Guid.NewGuid()
                              Text = text
                              Done = false }

                        return todo :: state, todo
                    })

            handle (_.getTodos) (fun _context state () -> task { return state, state })
        }
