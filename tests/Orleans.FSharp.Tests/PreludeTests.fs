module Orleans.FSharp.Tests.PreludeTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Fact>]
let ``taskResult wraps value in Ok`` () =
    let t = TaskHelpers.taskResult 42
    let result = t.Result
    test <@ result = Ok 42 @>

[<Fact>]
let ``taskResult Ok value is accessible`` () =
    let t = TaskHelpers.taskResult "hello"
    let result = t.Result
    test <@ result = Ok "hello" @>

[<Fact>]
let ``taskError wraps value in Error`` () =
    let t = TaskHelpers.taskError "fail"
    let result: Result<int, string> = t.Result
    test <@ result = Error "fail" @>

[<Fact>]
let ``taskMap transforms Ok value`` () =
    task {
        let! result =
            TaskHelpers.taskResult 10
            |> TaskHelpers.taskMap (fun x -> x * 2)

        test <@ result = Ok 20 @>
    }

[<Fact>]
let ``taskMap propagates Error unchanged`` () =
    task {
        let! result =
            TaskHelpers.taskError "oops"
            |> TaskHelpers.taskMap (fun (x: int) -> x * 2)

        test <@ result = Error "oops" @>
    }

[<Fact>]
let ``taskMap applies string transformation`` () =
    task {
        let! result =
            TaskHelpers.taskResult "world"
            |> TaskHelpers.taskMap (fun s -> $"hello {s}")

        test <@ result = Ok "hello world" @>
    }

[<Fact>]
let ``taskBind chains successful operations`` () =
    task {
        let! result =
            TaskHelpers.taskResult 5
            |> TaskHelpers.taskBind (fun x -> TaskHelpers.taskResult (x + 10))

        test <@ result = Ok 15 @>
    }

[<Fact>]
let ``taskBind propagates Error from first operation`` () =
    task {
        let! result =
            TaskHelpers.taskError "first error"
            |> TaskHelpers.taskBind (fun (x: int) -> TaskHelpers.taskResult (x + 10))

        test <@ result = Error "first error" @>
    }

[<Fact>]
let ``taskBind propagates Error from second operation`` () =
    task {
        let! result =
            TaskHelpers.taskResult 5
            |> TaskHelpers.taskBind (fun _ -> TaskHelpers.taskError "second error")

        test <@ result = Error "second error" @>
    }

[<Fact>]
let ``taskBind chains multiple operations`` () =
    task {
        let! result =
            TaskHelpers.taskResult 1
            |> TaskHelpers.taskBind (fun x -> TaskHelpers.taskResult (x + 1))
            |> TaskHelpers.taskBind (fun x -> TaskHelpers.taskResult (x * 3))

        test <@ result = Ok 6 @>
    }

[<Fact>]
let ``taskMap and taskBind compose together`` () =
    task {
        let! result =
            TaskHelpers.taskResult 10
            |> TaskHelpers.taskMap (fun x -> x + 5)
            |> TaskHelpers.taskBind (fun x -> TaskHelpers.taskResult (x * 2))

        test <@ result = Ok 30 @>
    }

[<Fact>]
let ``Orleans type aliases are accessible`` () =
    // Verify the type aliases compile and resolve correctly
    test <@ typeof<IGrain> = typeof<Orleans.IGrain> @>
    test <@ typeof<IGrainWithStringKey> = typeof<Orleans.IGrainWithStringKey> @>
    test <@ typeof<IGrainWithGuidKey> = typeof<Orleans.IGrainWithGuidKey> @>
    test <@ typeof<IGrainWithIntegerKey> = typeof<Orleans.IGrainWithIntegerKey> @>
    test <@ typeof<IGrainFactory> = typeof<Orleans.IGrainFactory> @>
