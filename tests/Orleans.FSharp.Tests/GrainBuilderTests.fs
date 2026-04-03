module Orleans.FSharp.Tests.GrainBuilderTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Fact>]
let ``grain CE sets defaultState`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.DefaultState = Some 0 @>

[<Fact>]
let ``grain CE sets defaultState with DU`` () =
    let def =
        grain {
            defaultState "initial"
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.DefaultState = Some "initial" @>

[<Fact>]
let ``grain CE registers handler`` () =
    let def =
        grain {
            defaultState 0

            handle (fun state (msg: string) ->
                task {
                    let newState = state + msg.Length
                    return newState, box newState
                })
        }

    test <@ def.Handler |> Option.isSome @>

[<Fact>]
let ``grain CE handler produces correct result`` () =
    task {
        let def =
            grain {
                defaultState 10

                handle (fun state (msg: int) ->
                    task {
                        let newState = state + msg
                        return newState, box newState
                    })
            }

        let handler = def.Handler.Value
        let! (newState, result) = handler 10 5
        test <@ newState = 15 @>
        test <@ unbox<int> result = 15 @>
    }

[<Fact>]
let ``grain CE sets persist name`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            persist "MyStorage"
        }

    test <@ def.PersistenceName = Some "MyStorage" @>

[<Fact>]
let ``grain CE without persist has None persistence`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.PersistenceName = None @>

[<Fact>]
let ``grain CE sets onActivate`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onActivate (fun state -> task { return state + 1 })
        }

    test <@ def.OnActivate |> Option.isSome @>

[<Fact>]
let ``grain CE onActivate handler produces correct result`` () =
    task {
        let def =
            grain {
                defaultState 0
                handle (fun state _msg -> task { return state, box state })
                onActivate (fun state -> task { return state + 100 })
            }

        let! result = def.OnActivate.Value 5
        test <@ result = 105 @>
    }

[<Fact>]
let ``grain CE sets onDeactivate`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onDeactivate (fun _state -> task { return () })
        }

    test <@ def.OnDeactivate |> Option.isSome @>

[<Fact>]
let ``grain CE missing handler throws in Run`` () =
    let ex =
        Assert.Throws<System.InvalidOperationException>(fun () ->
            grain {
                defaultState 0
            }
            |> ignore<GrainDefinition<int, string>>)

    test <@ ex.Message.Contains("handler") @>
    test <@ ex.Message.Contains("handle") @>

[<Fact>]
let ``grain CE missing handler error contains type names`` () =
    let ex =
        Assert.Throws<System.InvalidOperationException>(fun () ->
            grain {
                defaultState 0
            }
            |> ignore<GrainDefinition<int, string>>)

    test <@ ex.Message.Contains("Int32") @>
    test <@ ex.Message.Contains("String") @>

[<Fact>]
let ``grain CE produces complete definition`` () =
    let def =
        grain {
            defaultState "idle"

            handle (fun state (cmd: int) ->
                task {
                    let newState = $"{state}_{cmd}"
                    return newState, box newState
                })

            persist "Default"
            onActivate (fun s -> task { return s })
            onDeactivate (fun _ -> task { return () })
        }

    test <@ def.DefaultState = Some "idle" @>
    test <@ def.Handler |> Option.isSome @>
    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.OnActivate |> Option.isSome @>
    test <@ def.OnDeactivate |> Option.isSome @>
