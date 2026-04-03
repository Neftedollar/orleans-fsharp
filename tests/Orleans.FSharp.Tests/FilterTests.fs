module Orleans.FSharp.Tests.FilterTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans
open Orleans.FSharp

/// <summary>Tests for Filters.fs — FSharpIncomingFilter, FSharpOutgoingFilter, and Filter module.</summary>

[<Fact>]
let ``FSharpIncomingFilter implements IIncomingGrainCallFilter`` () =
    let filter = FSharpIncomingFilter(fun _ -> Task.FromResult())
    test <@ (filter :> IIncomingGrainCallFilter) |> isNull |> not @>

[<Fact>]
let ``FSharpOutgoingFilter implements IOutgoingGrainCallFilter`` () =
    let filter = FSharpOutgoingFilter(fun _ -> Task.FromResult())
    test <@ (filter :> IOutgoingGrainCallFilter) |> isNull |> not @>

[<Fact>]
let ``Filter.incoming returns IIncomingGrainCallFilter`` () =
    let filter =
        Filter.incoming (fun _ctx -> task { return () })

    test <@ filter :? FSharpIncomingFilter @>

[<Fact>]
let ``Filter.outgoing returns IOutgoingGrainCallFilter`` () =
    let filter =
        Filter.outgoing (fun _ctx -> task { return () })

    test <@ filter :? FSharpOutgoingFilter @>

[<Fact>]
let ``Filter.incoming handler is invoked`` () =
    task {
        let mutable called = false

        let filter =
            Filter.incoming (fun _ctx ->
                task {
                    called <- true
                    return ()
                })

        // Invoke the filter directly (context is null but handler doesn't use it)
        do! filter.Invoke(Unchecked.defaultof<IIncomingGrainCallContext>)
        test <@ called @>
    }

[<Fact>]
let ``Filter.outgoing handler is invoked`` () =
    task {
        let mutable called = false

        let filter =
            Filter.outgoing (fun _ctx ->
                task {
                    called <- true
                    return ()
                })

        do! filter.Invoke(Unchecked.defaultof<IOutgoingGrainCallContext>)
        test <@ called @>
    }

[<Fact>]
let ``Filter.incoming creates distinct filter instances`` () =
    let handler = fun (_ctx: IIncomingGrainCallContext) -> Task.FromResult()
    let f1 = Filter.incoming handler
    let f2 = Filter.incoming handler
    test <@ not (obj.ReferenceEquals(f1, f2)) @>

[<Fact>]
let ``Filter.outgoing creates distinct filter instances`` () =
    let handler = fun (_ctx: IOutgoingGrainCallContext) -> Task.FromResult()
    let f1 = Filter.outgoing handler
    let f2 = Filter.outgoing handler
    test <@ not (obj.ReferenceEquals(f1, f2)) @>
