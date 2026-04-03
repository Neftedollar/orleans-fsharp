module Orleans.FSharp.Tests.AdditionalStateTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// --- additionalState CE keyword tests ---

[<Fact>]
let ``grain CE additionalState stores state spec`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            additionalState "config" "Default" {| Name = "test" |}
        }

    test <@ def.AdditionalStates |> Map.containsKey "config" @>

[<Fact>]
let ``grain CE additionalState stores correct name`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            additionalState "myState" "Default" 42
        }

    test <@ def.AdditionalStates.["myState"].Name = "myState" @>

[<Fact>]
let ``grain CE additionalState stores correct storage name`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            additionalState "myState" "AzureBlob" "hello"
        }

    test <@ def.AdditionalStates.["myState"].StorageName = "AzureBlob" @>

[<Fact>]
let ``grain CE additionalState stores correct default value`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            additionalState "counter" "Default" 99
        }

    test <@ unbox<int> def.AdditionalStates.["counter"].DefaultValue = 99 @>

[<Fact>]
let ``grain CE additionalState stores correct type`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            additionalState "counter" "Default" 42
        }

    test <@ def.AdditionalStates.["counter"].StateType = typeof<int> @>

[<Fact>]
let ``grain CE supports multiple additionalState declarations`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            additionalState "config" "Default" "initial-config"
            additionalState "metrics" "Default" 0
        }

    test <@ def.AdditionalStates |> Map.count = 2 @>
    test <@ def.AdditionalStates |> Map.containsKey "config" @>
    test <@ def.AdditionalStates |> Map.containsKey "metrics" @>

[<Fact>]
let ``grain CE additionalState defaults to empty map`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
        }

    test <@ def.AdditionalStates |> Map.isEmpty @>

// --- GrainContext.getState tests ---

[<Fact>]
let ``GrainContext has States field`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
        }

    test <@ ctx.States |> Map.isEmpty @>

[<Fact>]
let ``GrainContext.getState throws KeyNotFoundException for missing state`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
        }

    Assert.Throws<System.Collections.Generic.KeyNotFoundException>(fun () ->
        GrainContext.getState<int> ctx "nonexistent" |> ignore)
    |> ignore

[<Fact>]
let ``GrainContext.getState error message contains state name`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
        }

    let ex =
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(fun () ->
            GrainContext.getState<int> ctx "missingState" |> ignore)

    test <@ ex.Message.Contains("missingState") @>

// --- AdditionalStateSpec type tests ---

[<Fact>]
let ``AdditionalStateSpec is a record type`` () =
    test <@ Microsoft.FSharp.Reflection.FSharpType.IsRecord(typeof<AdditionalStateSpec>) @>

[<Fact>]
let ``AdditionalStateSpec has Name field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<AdditionalStateSpec>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Name" @>

[<Fact>]
let ``AdditionalStateSpec has StorageName field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<AdditionalStateSpec>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "StorageName" @>

[<Fact>]
let ``AdditionalStateSpec has DefaultValue field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<AdditionalStateSpec>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "DefaultValue" @>

[<Fact>]
let ``AdditionalStateSpec has StateType field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<AdditionalStateSpec>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "StateType" @>

[<Fact>]
let ``GrainDefinition has AdditionalStates field`` () =
    let defType = typeof<GrainDefinition<int, string>>

    let field =
        defType.GetProperties(System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Instance)
        |> Array.tryFind (fun p -> p.Name = "AdditionalStates")

    test <@ field.IsSome @>
