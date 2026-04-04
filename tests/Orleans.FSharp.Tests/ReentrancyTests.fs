module Orleans.FSharp.Tests.ReentrancyTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

[<Fact>]
let ``grain CE default has IsReentrant false`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.IsReentrant = false @>

[<Fact>]
let ``grain CE reentrant keyword sets IsReentrant true`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            reentrant
        }

    test <@ def.IsReentrant = true @>

[<Fact>]
let ``grain CE default has empty InterleavedMethods`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.InterleavedMethods = Set.empty @>

[<Fact>]
let ``grain CE interleave keyword adds method name to InterleavedMethods`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            interleave "GetValue"
        }

    test <@ def.InterleavedMethods |> Set.contains "GetValue" @>

[<Fact>]
let ``grain CE multiple interleave calls add multiple methods`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            interleave "GetValue"
            interleave "GetStatus"
        }

    test <@ def.InterleavedMethods |> Set.count = 2 @>
    test <@ def.InterleavedMethods |> Set.contains "GetValue" @>
    test <@ def.InterleavedMethods |> Set.contains "GetStatus" @>

[<Fact>]
let ``grain CE reentrant and interleave can coexist`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            reentrant
            interleave "GetValue"
        }

    test <@ def.IsReentrant = true @>
    test <@ def.InterleavedMethods |> Set.contains "GetValue" @>

[<Fact>]
let ``grain CE reentrant does not affect other fields`` () =
    let def =
        grain {
            defaultState 42
            handle (fun state _msg -> task { return state, box state })
            persist "Default"
            reentrant
        }

    test <@ def.DefaultState = Some 42 @>
    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.IsReentrant = true @>
    test <@ def.Handler |> Option.isSome @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``interleave stores any given method name in InterleavedMethods`` (name: NonEmptyString) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            interleave name.Get
        }
    def.InterleavedMethods |> Set.contains name.Get

[<Property>]
let ``interleave is idempotent: adding same name twice keeps set size at 1`` (name: NonEmptyString) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            interleave name.Get
            interleave name.Get
        }
    def.InterleavedMethods |> Set.count = 1

[<Property>]
let ``reentrant does not affect DefaultState for any default value`` (value: int) =
    let def =
        grain {
            defaultState value
            handle (fun state (_msg: string) -> task { return state, box state })
            reentrant
        }
    def.DefaultState = Some value && def.IsReentrant = true