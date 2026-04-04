module Orleans.FSharp.Tests.PlacementTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

[<Fact>]
let ``grain CE default has Default placement strategy`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.PlacementStrategy = PlacementStrategy.Default @>

[<Fact>]
let ``grain CE preferLocalPlacement sets PreferLocal strategy`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            preferLocalPlacement
        }

    test <@ def.PlacementStrategy = PlacementStrategy.PreferLocal @>

[<Fact>]
let ``grain CE randomPlacement sets Random strategy`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            randomPlacement
        }

    test <@ def.PlacementStrategy = PlacementStrategy.Random @>

[<Fact>]
let ``grain CE hashBasedPlacement sets HashBased strategy`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            hashBasedPlacement
        }

    test <@ def.PlacementStrategy = PlacementStrategy.HashBased @>

[<Fact>]
let ``grain CE placement does not affect other fields`` () =
    let def =
        grain {
            defaultState 42
            handle (fun state _msg -> task { return state, box state })
            persist "Default"
            preferLocalPlacement
            reentrant
        }

    test <@ def.DefaultState = Some 42 @>
    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.IsReentrant = true @>
    test <@ def.PlacementStrategy = PlacementStrategy.PreferLocal @>
    test <@ def.Handler |> Option.isSome @>

[<Fact>]
let ``grain CE last placement strategy wins`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            preferLocalPlacement
            randomPlacement
        }

    test <@ def.PlacementStrategy = PlacementStrategy.Random @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``preferLocalPlacement does not affect DefaultState for any initial state`` (initial: int) =
    let def =
        grain {
            defaultState initial
            handle (fun state (_msg: string) -> task { return state, box state })
            preferLocalPlacement
        }
    def.DefaultState = Some initial
    && def.PlacementStrategy = PlacementStrategy.PreferLocal

[<Property>]
let ``randomPlacement does not affect Handler registration for any state`` (initial: int) =
    let def =
        grain {
            defaultState initial
            handle (fun state (_msg: string) -> task { return state, box state })
            randomPlacement
        }
    def.Handler |> Option.isSome
    && def.PlacementStrategy = PlacementStrategy.Random
