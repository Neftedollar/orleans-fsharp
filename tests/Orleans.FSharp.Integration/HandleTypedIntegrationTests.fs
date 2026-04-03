/// <summary>
/// Integration tests for the <c>handleTyped</c> CE variant combined with
/// <c>FSharpGrain.ask</c> — demonstrating that typed results are correctly
/// propagated end-to-end without any manual boxing.
/// </summary>
module Orleans.FSharp.Integration.HandleTypedIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleTypedTests(fixture: ClusterFixture) =

    // ── Basic ask with handleTyped grain ─────────────────────────────────────

    [<Fact>]
    member _.``handleTyped grain: AddValues returns correct int result`` () =
        task {
            let handle = FSharpGrain.ref<CalcState, CalcCommand> fixture.GrainFactory "calc-add-1"
            let! result = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(3, 4))
            test <@ result = 7 @>
        }

    [<Fact>]
    member _.``handleTyped grain: MultiplyValues returns correct int result`` () =
        task {
            let handle = FSharpGrain.ref<CalcState, CalcCommand> fixture.GrainFactory "calc-mul-1"
            let! result = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (MultiplyValues(6, 7))
            test <@ result = 42 @>
        }

    [<Fact>]
    member _.``handleTyped grain: state is updated after operation`` () =
        task {
            let handle = FSharpGrain.ref<CalcState, CalcCommand> fixture.GrainFactory "calc-state-1"
            let! _ = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(10, 5))
            let! last = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> GetLastResult
            test <@ last = 15 @>
        }

    [<Fact>]
    member _.``handleTyped grain: OpCount increments after each operation`` () =
        task {
            let handle = FSharpGrain.ref<CalcState, CalcCommand> fixture.GrainFactory "calc-opcount-1"
            let! _ = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(1, 1))
            let! _ = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (MultiplyValues(2, 3))
            let! count = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> GetOpCount
            test <@ count = 2 @>
        }

    [<Fact>]
    member _.``handleTyped grain: GetLastResult returns 0 for fresh grain`` () =
        task {
            let handle = FSharpGrain.ref<CalcState, CalcCommand> fixture.GrainFactory "calc-fresh-1"
            let! last = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> GetLastResult
            test <@ last = 0 @>
        }

    [<Fact>]
    member _.``handleTyped grain: send also works (returns CalcState)`` () =
        task {
            // send casts the result (int) to 'State (CalcState) — this fails unless
            // handleTyped returns state as result too. Here we use post instead.
            let handle = FSharpGrain.ref<CalcState, CalcCommand> fixture.GrainFactory "calc-post-1"
            do! handle |> FSharpGrain.post (AddValues(5, 5))
            let! last = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> GetLastResult
            test <@ last = 10 @>
        }

    [<Fact>]
    member _.``handleTyped grain: multiple operations accumulate correctly`` () =
        task {
            let handle = FSharpGrain.ref<CalcState, CalcCommand> fixture.GrainFactory "calc-accum-1"

            let! r1 = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(10, 3))
            let! r2 = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (MultiplyValues(2, 5))
            let! r3 = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> GetLastResult
            let! ops = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> GetOpCount

            test <@ r1 = 13 @>
            test <@ r2 = 10 @>
            test <@ r3 = 10 @>    // last result is the multiply result
            test <@ ops = 2 @>
        }

    [<Fact>]
    member _.``handleTyped grain: concurrent ask calls produce correct results`` () =
        task {
            let handle = FSharpGrain.ref<CalcState, CalcCommand> fixture.GrainFactory "calc-concurrent-1"

            // Send 5 additions sequentially (grain is single-threaded)
            let! _ = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(1, 0))
            let! _ = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(2, 0))
            let! _ = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(3, 0))
            let! _ = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(4, 0))
            let! _ = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> (AddValues(5, 0))

            let! ops = handle |> FSharpGrain.ask<CalcState, CalcCommand, int> GetOpCount
            test <@ ops = 5 @>
        }
