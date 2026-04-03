module Orleans.FSharp.Tests.ErrorMessageTests

open System
open Xunit
open Swensen.Unquote
open Orleans.FSharp

/// <summary>Test DU state type for error message verification.</summary>
type MyState =
    | Active
    | Inactive

/// <summary>Test DU message type for error message verification.</summary>
type MyMsg = | DoStuff

[<Fact>]
let ``Missing handler error contains F# state type name`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            grain {
                defaultState 0
            }
            |> ignore<GrainDefinition<int, string>>)

    test <@ ex.Message.Contains("Int32") @>
    test <@ ex.Message.Contains("String") @>

[<Fact>]
let ``Missing handler error mentions handler custom operation`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            grain {
                defaultState 0
            }
            |> ignore<GrainDefinition<int, string>>)

    test <@ ex.Message.Contains("handle") @>
    test <@ ex.Message.Contains("grain") @>

[<Fact>]
let ``Missing handler error for custom DU types contains DU type names`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            grain {
                defaultState Active
            }
            |> ignore<GrainDefinition<MyState, MyMsg>>)

    test <@ ex.Message.Contains("MyState") @>
    test <@ ex.Message.Contains("MyMsg") @>
