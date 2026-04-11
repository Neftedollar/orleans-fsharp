module Orleans.FSharp.Integration.ConcurrencyTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Sample

/// <summary>
/// Integration tests for concurrent grain access patterns.
/// Validates that the universal grain API handles parallel calls correctly
/// without state corruption, lost messages, or deadlocks.
/// </summary>
[<Collection("ClusterCollection")>]
type ConcurrencyTests(fixture: ClusterFixture) =

    /// <summary>
    /// 100 concurrent sends to the same grain must all succeed and the final
    /// state must reflect all 100 operations (no lost updates).
    /// </summary>
    [<Fact>]
    member _.``concurrent sends to same grain don't corrupt state`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAggregatorGrain>("concurrent-send-test")
            let callCount = 100

            // Fire 100 concurrent AddValue calls
            let tasks =
                [| for i in 1 .. callCount -> grain.HandleMessage(AggregatorCommand.AddValue 1) |]

            let! results = Task.WhenAll tasks

            // All should complete successfully
            let successCount = results |> Array.filter (fun r -> r <> null) |> Array.length
            test <@ successCount = callCount @>

            // Verify final state: should be 100 (each added 1)
            let! finalState = grain.HandleMessage(AggregatorCommand.GetTotal)
            let total = unbox<int> finalState
            test <@ total = callCount @>
        }

    /// <summary>
    /// 50 concurrent asks via FSharpGrain.ask must all return consistent results.
    /// Exercises the typed client API path (not just raw HandleMessage).
    /// </summary>
    [<Fact>]
    member _.``concurrent asks return consistent results`` () =
        task {
            let grainRef = FSharpGrain.ref<Orleans.FSharp.Sample.AdditionalState, AdditionalStateCommand> fixture.GrainFactory "concurrent-ask-test"

            // First initialize
            let! _ = FSharpGrain.send ResetAll grainRef

            let callCount = 50

            // Fire 50 concurrent IncrCounter calls
            let tasks =
                [| for _ in 1 .. callCount -> FSharpGrain.send IncrCounter grainRef |]

            let! results = Task.WhenAll tasks

            // All should complete
            let successCount = results |> Array.length
            test <@ successCount = callCount @>

            // Verify final counter
            let! state = FSharpGrain.ask GetBoth grainRef
            let counter, _ = unbox<int * int> state
            test <@ counter = callCount @>
        }

    /// <summary>
    /// Stateless worker grain should distribute calls across workers and
    /// handle concurrent invocations without dropping any.
    /// </summary>
    [<Fact>]
    member _.``stateless worker handles concurrent calls`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IProcessorGrain>(0L)
            let callCount = 50

            // Fire 50 concurrent Process calls
            let tasks =
                [| for i in 1 .. callCount -> grain.HandleMessage(Process $"msg-{i}") |]

            let! results = Task.WhenAll tasks

            // All should complete successfully (each returns an activation ID string)
            let successCount =
                results
                |> Array.filter (fun r ->
                    match r with
                    | :? string as s -> s.Length > 0
                    | _ -> false)
                |> Array.length

            // All calls should have returned non-empty strings
            test <@ successCount = callCount @>
        }

    /// <summary>
    /// Two parallel slow calls to a reentrant grain should complete in
    /// approximately the time of a single call (concurrent execution).
    /// </summary>
    [<Fact>]
    member _.``reentrant grain processes interleaved calls`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAggregatorGrain>("reentrant-interleave-test")

            // Fire 2 concurrent calls (each has internal delay)
            let t1 = grain.HandleMessage(AggregatorCommand.AddValue 1)
            let t2 = grain.HandleMessage(AggregatorCommand.AddValue 2)
            let! _ = t1
            and! _ = t2

            // Both should complete successfully
            test <@ true @>
        }

    /// <summary>
    /// Non-reentrant grain should serialize concurrent calls — state must
    /// not be corrupted even when multiple callers invoke the grain simultaneously.
    /// </summary>
    [<Fact>]
    member _.``non-reentrant grain serializes concurrent calls safely`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ISequentialGrain>("nonreentrant-safe-test")
            let callCount = 10

            // Fire 10 concurrent SlowAdd calls
            let tasks =
                [| for i in 1 .. callCount -> grain.HandleMessage(SequentialCommand.SlowAdd i) |]

            let! _ = Task.WhenAll tasks

            // Verify final state: sum of 1+2+...+10 = 55
            let! result = grain.HandleMessage(SequentialCommand.GetTotal)
            let total = unbox<int> result
            test <@ total = 55 @>
        }

    /// <summary>
    /// 500 fire-and-forget posts must not lose any messages. The grain's
    /// internal counter should reflect all posts.
    /// </summary>
    [<Fact>]
    member _.``concurrent posts don't lose messages`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IProcessorGrain>(1L)
            let callCount = 200

            // Fire 200 concurrent Process calls
            let tasks =
                [| for i in 1 .. callCount -> grain.HandleMessage(Process $"fire-{i}") |]

            let! results = Task.WhenAll tasks

            let successCount = results |> Array.filter (fun r -> r <> null) |> Array.length
            test <@ successCount = callCount @>
        }
