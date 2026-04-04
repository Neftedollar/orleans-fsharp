module Orleans.FSharp.Tests.PreludeTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
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

// ---------------------------------------------------------------------------
// FsCheck property tests — monadic laws for TaskHelpers
// ---------------------------------------------------------------------------

/// Synchronously awaits a Task<Result<_,_>> for use in FsCheck properties.
let private run (t: Task<Result<'a, 'e>>) = t.GetAwaiter().GetResult()

[<Property>]
let ``taskResult wraps any value in Ok`` (value: int) =
    run (TaskHelpers.taskResult value) = Ok value

[<Property>]
let ``taskError wraps any value in Error`` (msg: NonNull<string>) =
    run (TaskHelpers.taskError msg.Get : Task<Result<int, string>>) = Error msg.Get

[<Property>]
let ``taskMap identity law: map id = id for any Ok value`` (value: int) =
    run (TaskHelpers.taskResult value |> TaskHelpers.taskMap id) = Ok value

[<Property>]
let ``taskMap composition law: map (f >> g) = map f >> map g for any value`` (value: int) =
    let f (x: int) = x + 1
    let g (x: int) = x * 3
    let direct = run (TaskHelpers.taskResult value |> TaskHelpers.taskMap (f >> g))
    let composed = run (TaskHelpers.taskResult value |> TaskHelpers.taskMap f |> TaskHelpers.taskMap g)
    direct = composed

[<Property>]
let ``taskMap does not alter Error for any error value`` (msg: NonNull<string>) =
    let original: Task<Result<int, string>> = TaskHelpers.taskError msg.Get
    run (original |> TaskHelpers.taskMap (fun x -> x + 1)) = Error msg.Get

[<Property>]
let ``taskBind left-identity law: return >=> f = f for any value`` (value: int) =
    let f x = TaskHelpers.taskResult (x * 2)
    run (TaskHelpers.taskResult value |> TaskHelpers.taskBind f) = run (f value)

[<Property>]
let ``taskBind short-circuits on Error — f is never called`` (msg: NonNull<string>) =
    let mutable called = false
    let f (x: int) =
        called <- true
        TaskHelpers.taskResult (x + 99)
    let err: Task<Result<int, string>> = TaskHelpers.taskError msg.Get
    run (err |> TaskHelpers.taskBind f) |> ignore
    not called

[<Property>]
let ``taskBind associativity: (m >>= f) >>= g = m >>= (f >=> g) for any value`` (value: int) =
    let f x = TaskHelpers.taskResult (x + 1)
    let g x = TaskHelpers.taskResult (x * 3)
    let lhs = run (TaskHelpers.taskResult value |> TaskHelpers.taskBind f |> TaskHelpers.taskBind g)
    let rhs = run (TaskHelpers.taskResult value |> TaskHelpers.taskBind (fun x -> f x |> TaskHelpers.taskBind g))
    lhs = rhs
