module Orleans.FSharp.Tests.AdvancedReentrancyTests

// FS44: deprecated CE keyword (mayInterleave) used here intentionally to assert legacy behaviour.
#nowarn "44"

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

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
let ``grain CE last mayInterleave wins`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            mayInterleave "First"
            mayInterleave "Second"
        }

    test <@ def.MayInterleavePredicate = Some "Second" @>
