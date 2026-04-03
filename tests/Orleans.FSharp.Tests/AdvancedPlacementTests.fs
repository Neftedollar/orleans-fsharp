module Orleans.FSharp.Tests.AdvancedPlacementTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

/// Dummy type to use for custom placement strategy tests.
type MyCustomStrategy() = class end

[<Fact>]
let ``grain CE activationCountPlacement sets ActivationCountBased strategy`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            activationCountPlacement
        }

    test <@ def.PlacementStrategy = PlacementStrategy.ActivationCountBased @>

[<Fact>]
let ``grain CE resourceOptimizedPlacement sets ResourceOptimized strategy`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            resourceOptimizedPlacement
        }

    test <@ def.PlacementStrategy = PlacementStrategy.ResourceOptimized @>

[<Fact>]
let ``grain CE siloRolePlacement sets SiloRoleBased with role string`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            siloRolePlacement "backend-worker"
        }

    test <@ def.PlacementStrategy = PlacementStrategy.SiloRoleBased "backend-worker" @>

[<Fact>]
let ``grain CE siloRolePlacement stores the role string`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            siloRolePlacement "frontend"
        }

    let role =
        match def.PlacementStrategy with
        | PlacementStrategy.SiloRoleBased r -> Some r
        | _ -> None

    test <@ role = Some "frontend" @>

[<Fact>]
let ``grain CE customPlacement sets Custom with strategy type`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            customPlacement typeof<MyCustomStrategy>
        }

    test <@ def.PlacementStrategy = PlacementStrategy.Custom typeof<MyCustomStrategy> @>

[<Fact>]
let ``grain CE customPlacement stores the type`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            customPlacement typeof<MyCustomStrategy>
        }

    let strategyType =
        match def.PlacementStrategy with
        | PlacementStrategy.Custom t -> Some t
        | _ -> None

    test <@ strategyType = Some typeof<MyCustomStrategy> @>

[<Fact>]
let ``grain CE advanced placement does not affect other fields`` () =
    let def =
        grain {
            defaultState 42
            handle (fun state _msg -> task { return state, box state })
            persist "Default"
            reentrant
            activationCountPlacement
        }

    test <@ def.DefaultState = Some 42 @>
    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.IsReentrant = true @>
    test <@ def.PlacementStrategy = PlacementStrategy.ActivationCountBased @>
    test <@ def.Handler |> Option.isSome @>

[<Fact>]
let ``grain CE last advanced placement strategy wins`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            activationCountPlacement
            resourceOptimizedPlacement
        }

    test <@ def.PlacementStrategy = PlacementStrategy.ResourceOptimized @>

[<Fact>]
let ``grain CE advanced placement overrides basic placement`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            preferLocalPlacement
            siloRolePlacement "compute"
        }

    test <@ def.PlacementStrategy = PlacementStrategy.SiloRoleBased "compute" @>
