module Orleans.FSharp.Tests.ImplicitStreamSubscriptionTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Fact>]
let ``grain CE default has no implicit subscriptions`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.ImplicitSubscriptions |> Map.isEmpty @>

[<Fact>]
let ``grain CE adds implicit stream subscription`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            implicitStreamSubscription "my-namespace" (fun state _event -> task { return state + 1 })
        }

    test <@ def.ImplicitSubscriptions |> Map.containsKey "my-namespace" @>
    test <@ def.ImplicitSubscriptions |> Map.count = 1 @>

[<Fact>]
let ``grain CE adds multiple implicit stream subscriptions`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            implicitStreamSubscription "ns1" (fun state _event -> task { return state + 1 })
            implicitStreamSubscription "ns2" (fun state _event -> task { return state + 2 })
        }

    test <@ def.ImplicitSubscriptions |> Map.count = 2 @>
    test <@ def.ImplicitSubscriptions |> Map.containsKey "ns1" @>
    test <@ def.ImplicitSubscriptions |> Map.containsKey "ns2" @>

[<Fact>]
let ``grain CE implicit subscription handler is invoked correctly`` () =
    let def =
        grain {
            defaultState 10
            handle (fun state _msg -> task { return state, box state })
            implicitStreamSubscription "test-ns" (fun state event ->
                let increment = event :?> int
                task { return state + increment })
        }

    let handler = def.ImplicitSubscriptions.["test-ns"]
    let result = handler 10 (box 5) |> Async.AwaitTask |> Async.RunSynchronously
    test <@ result = 15 @>

[<Fact>]
let ``grain CE implicit subscription composes with other features`` () =
    let def =
        grain {
            defaultState "initial"
            handle (fun state _msg -> task { return state, box state })
            persist "Default"
            reentrant
            implicitStreamSubscription "events" (fun state _event -> task { return state })
        }

    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.IsReentrant = true @>
    test <@ def.ImplicitSubscriptions |> Map.containsKey "events" @>

[<Fact>]
let ``grain CE later implicit subscription overrides earlier with same namespace`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            implicitStreamSubscription "ns" (fun state _event -> task { return state + 1 })
            implicitStreamSubscription "ns" (fun state _event -> task { return state + 100 })
        }

    test <@ def.ImplicitSubscriptions |> Map.count = 1 @>

    let handler = def.ImplicitSubscriptions.["ns"]
    let result = handler 0 (box ()) |> Async.AwaitTask |> Async.RunSynchronously
    test <@ result = 100 @>
