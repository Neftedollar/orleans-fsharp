module Orleans.FSharp.Tests.AdvancedReentrancyTests

/// <summary>
/// CE-builder tests for the message-type reentrancy operation <c>interleaveMessage</c>.
/// These exercise only the <c>GrainDefinition.InterleaveMessageTypes</c> record field built
/// by the <c>grain { }</c> CE; the process-wide registry and AddFSharpGrain wiring are
/// covered by <c>MayInterleaveTests</c>.
/// </summary>

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

type private ReentState = { N: int }

type ReadQuery =
    | Read

type WriteCmd =
    | Write

[<Fact>]
let ``grain CE default has empty InterleaveMessageTypes`` () =
    let def =
        grain {
            defaultState { N = 0 }
            handle (fun (state: ReentState) (_msg: ReadQuery) -> task { return state, box state })
        }

    test <@ def.InterleaveMessageTypes = [] @>

[<Fact>]
let ``grain CE interleaveMessage records the message type`` () =
    let def =
        grain {
            defaultState { N = 0 }
            handle (fun (state: ReentState) (_msg: ReadQuery) -> task { return state, box state })
            interleaveMessage typeof<ReadQuery>
        }

    test <@ def.InterleaveMessageTypes |> List.contains typeof<ReadQuery> @>

[<Fact>]
let ``grain CE interleaveMessage can record several types`` () =
    let def =
        grain {
            defaultState { N = 0 }
            handle (fun (state: ReentState) (_msg: ReadQuery) -> task { return state, box state })
            interleaveMessage typeof<ReadQuery>
            interleaveMessage typeof<WriteCmd>
        }

    test <@ def.InterleaveMessageTypes |> List.contains typeof<ReadQuery> @>
    test <@ def.InterleaveMessageTypes |> List.contains typeof<WriteCmd> @>
