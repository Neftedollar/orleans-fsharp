module Orleans.FSharp.Tests.AdvancedReentrancyTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Fact>]
let ``grain CE default has empty ReadOnlyMethods`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.ReadOnlyMethods = Set.empty @>

[<Fact>]
let ``grain CE readOnly adds method name to ReadOnlyMethods`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            readOnly "GetValue"
        }

    test <@ def.ReadOnlyMethods |> Set.contains "GetValue" @>

[<Fact>]
let ``grain CE multiple readOnly calls add multiple methods`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            readOnly "GetValue"
            readOnly "GetStatus"
        }

    test <@ def.ReadOnlyMethods |> Set.count = 2 @>
    test <@ def.ReadOnlyMethods |> Set.contains "GetValue" @>
    test <@ def.ReadOnlyMethods |> Set.contains "GetStatus" @>

[<Fact>]
let ``grain CE default has no MayInterleavePredicate`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.MayInterleavePredicate = None @>

[<Fact>]
let ``grain CE mayInterleave stores predicate method name`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            mayInterleave "ArgHasInterleaveAttribute"
        }

    test <@ def.MayInterleavePredicate = Some "ArgHasInterleaveAttribute" @>

[<Fact>]
let ``grain CE readOnly and mayInterleave can coexist`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            readOnly "GetValue"
            mayInterleave "ShouldInterleave"
        }

    test <@ def.ReadOnlyMethods |> Set.contains "GetValue" @>
    test <@ def.MayInterleavePredicate = Some "ShouldInterleave" @>

[<Fact>]
let ``grain CE reentrant readOnly and mayInterleave can all coexist`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            reentrant
            readOnly "GetValue"
            mayInterleave "ShouldInterleave"
        }

    test <@ def.IsReentrant = true @>
    test <@ def.ReadOnlyMethods |> Set.contains "GetValue" @>
    test <@ def.MayInterleavePredicate = Some "ShouldInterleave" @>

[<Fact>]
let ``grain CE readOnly does not affect other fields`` () =
    let def =
        grain {
            defaultState 42
            handle (fun state _msg -> task { return state, box state })
            persist "Default"
            readOnly "GetValue"
        }

    test <@ def.DefaultState = 42 @>
    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.ReadOnlyMethods |> Set.contains "GetValue" @>
    test <@ def.IsReentrant = false @>

[<Fact>]
let ``grain CE last mayInterleave wins`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            mayInterleave "First"
            mayInterleave "Second"
        }

    test <@ def.MayInterleavePredicate = Some "Second" @>
