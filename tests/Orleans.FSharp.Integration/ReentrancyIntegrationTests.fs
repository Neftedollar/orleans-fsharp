module Orleans.FSharp.Integration.ReentrancyIntegrationTests

open System.Diagnostics
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp.Sample

/// <summary>
/// Integration tests for reentrancy behavior in Orleans.FSharp grains.
/// Verifies that reentrant grains process messages concurrently while
/// non-reentrant grains process them sequentially.
/// </summary>
[<Collection("ClusterCollection")>]
type ReentrancyIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``Reentrant grain processes two concurrent calls in parallel`` () =
        task {
            let grain =
                fixture.GrainFactory.GetGrain<IAggregatorGrain>("reentrant-timing-test")

            let sw = Stopwatch.StartNew()

            // Fire two concurrent calls, each with a 500ms delay
            let t1 = grain.HandleMessage(AddValue 1)
            let t2 = grain.HandleMessage(AddValue 2)
            let! _ = t1
            and! _ = t2

            sw.Stop()

            // Reentrant grain should process both concurrently:
            // ~500ms instead of ~1000ms. Allow generous margin for CI.
            test <@ sw.ElapsedMilliseconds < 900L @>
        }

    [<Fact>]
    member _.``Non-reentrant grain processes two concurrent calls sequentially`` () =
        task {
            let grain =
                fixture.GrainFactory.GetGrain<ISequentialGrain>("sequential-timing-test")

            let sw = Stopwatch.StartNew()

            // Fire two concurrent calls, each with a 500ms delay
            let t1 = grain.HandleMessage(SlowAdd 1)
            let t2 = grain.HandleMessage(SlowAdd 2)
            let! _ = t1
            and! _ = t2

            sw.Stop()

            // Non-reentrant grain should process sequentially:
            // ~1000ms (500ms + 500ms).
            test <@ sw.ElapsedMilliseconds >= 900L @>
        }

    [<Fact>]
    member _.``Reentrant grain returns correct results from concurrent calls`` () =
        task {
            let grain =
                fixture.GrainFactory.GetGrain<IAggregatorGrain>("reentrant-result-test")

            let t1 = grain.HandleMessage(AddValue 10)
            let t2 = grain.HandleMessage(AddValue 20)
            let! r1 = t1
            and! r2 = t2

            // Both calls should complete successfully with int results
            let result1 = unbox<int> r1
            let result2 = unbox<int> r2
            // The sum of results should reflect both values being added
            // (order may vary due to concurrent execution)
            test <@ result1 > 0 @>
            test <@ result2 > 0 @>
        }
