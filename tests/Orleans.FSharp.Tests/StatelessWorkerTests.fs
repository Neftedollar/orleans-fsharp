module Orleans.FSharp.Tests.StatelessWorkerTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

[<Fact>]
let ``grain CE default has IsStatelessWorker false`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.IsStatelessWorker = false @>

[<Fact>]
let ``grain CE statelessWorker keyword sets IsStatelessWorker true`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            statelessWorker
        }

    test <@ def.IsStatelessWorker = true @>

[<Fact>]
let ``grain CE default has MaxLocalWorkers None`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.MaxLocalWorkers = None @>

[<Fact>]
let ``grain CE maxActivations sets MaxLocalWorkers`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            statelessWorker
            maxActivations 4
        }

    test <@ def.MaxLocalWorkers = Some 4 @>

[<Fact>]
let ``grain CE statelessWorker with persist throws InvalidOperationException`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            grain {
                defaultState 0
                handle (fun state _msg -> task { return state, box state })
                statelessWorker
                persist "Default"
            }
            |> ignore)

    test <@ ex.Message.Contains("stateless") || ex.Message.Contains("Stateless") @>

[<Fact>]
let ``grain CE statelessWorker does not affect other fields`` () =
    let def =
        grain {
            defaultState "hello"
            handle (fun state _msg -> task { return state, box state })
            statelessWorker
        }

    test <@ def.DefaultState = Some "hello" @>
    test <@ def.IsStatelessWorker = true @>
    test <@ def.Handler |> Option.isSome @>

[<Fact>]
let ``grain CE maxActivations without statelessWorker still sets value`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            maxActivations 2
        }

    test <@ def.MaxLocalWorkers = Some 2 @>

[<Fact>]
let ``grain CE statelessWorker and reentrant can coexist`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            statelessWorker
            reentrant
        }

    test <@ def.IsStatelessWorker = true @>
    test <@ def.IsReentrant = true @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``maxActivations stores correct value for any positive count`` (n: PositiveInt) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            maxActivations n.Get
        }
    def.MaxLocalWorkers = Some n.Get

[<Property>]
let ``statelessWorker does not affect DefaultState for any initial state`` (initial: int) =
    let def =
        grain {
            defaultState initial
            handle (fun state (_msg: string) -> task { return state, box state })
            statelessWorker
        }
    def.DefaultState = Some initial && def.IsStatelessWorker
