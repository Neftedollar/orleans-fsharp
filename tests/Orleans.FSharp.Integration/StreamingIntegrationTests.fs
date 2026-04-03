module Orleans.FSharp.Integration.StreamingIntegrationTests

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open Xunit
open Swensen.Unquote
open Orleans
open Orleans.Streams
open Orleans.FSharp.Streaming
open FSharp.Control

/// <summary>
/// Integration tests for the Orleans.FSharp.Streaming module.
/// Tests publish/subscribe, TaskSeq consumption, and backpressure behavior.
/// </summary>
[<Collection("ClusterCollection")>]
type StreamingIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``Producer emits 100 events and consumer receives all 100 in order`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "test-ns" (Guid.NewGuid().ToString())
            let received = ConcurrentBag<int>()

            let! sub =
                Stream.subscribe streamRef (fun item ->
                    task { received.Add(item) })

            // Publish 100 events
            for i in 1..100 do
                do! Stream.publish streamRef i

            // Allow time for async delivery
            do! Task.Delay(2000)

            let items = received |> Seq.toList |> List.sort
            test <@ items.Length = 100 @>
            test <@ items = [ 1..100 ] @>

            do! Stream.unsubscribe sub
        }

    [<Fact>]
    member _.``Consumer applies filter and counts correctly`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "filter-ns" (Guid.NewGuid().ToString())
            let received = ConcurrentBag<int>()

            // Subscribe with a filter: only even numbers
            let! sub =
                Stream.subscribe streamRef (fun item ->
                    task {
                        if item % 2 = 0 then
                            received.Add(item)
                    })

            // Publish 100 events
            for i in 1..100 do
                do! Stream.publish streamRef i

            do! Task.Delay(2000)

            let items = received |> Seq.toList |> List.sort
            // Should have exactly 50 even numbers
            test <@ items.Length = 50 @>
            test <@ items = [ 2..2..100 ] @>

            do! Stream.unsubscribe sub
        }

    [<Fact>]
    member _.``asTaskSeq consumes stream events as a pull-based sequence`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let key = Guid.NewGuid().ToString()
            let streamRef = Stream.getStream<int> streamProvider "taskseq-ns" key

            let eventCount = 20
            let received = ConcurrentBag<int>()

            // Start consuming in background via TaskSeq — take only eventCount items
            let consumerTask =
                task {
                    let seq = Stream.asTaskSeq streamRef

                    do!
                        seq
                        |> TaskSeq.take eventCount
                        |> TaskSeq.iter (fun item -> received.Add(item))
                }

            // Give subscription time to set up
            do! Task.Delay(500)

            // Publish events
            for i in 1..eventCount do
                do! Stream.publish streamRef i

            // Wait for consumption with timeout
            let! completed = Task.WhenAny(consumerTask, Task.Delay(TimeSpan.FromSeconds(10.0)))
            test <@ Object.ReferenceEquals(completed, consumerTask) @>

            let items = received |> Seq.toList |> List.sort
            test <@ items.Length = eventCount @>
            test <@ items = [ 1..eventCount ] @>
        }

    [<Fact>]
    member _.``Multiple subscribers on same stream each receive all events`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "multi-sub-ns" (Guid.NewGuid().ToString())
            let received1 = ConcurrentBag<int>()
            let received2 = ConcurrentBag<int>()

            let! sub1 =
                Stream.subscribe streamRef (fun item ->
                    task { received1.Add(item) })

            let! sub2 =
                Stream.subscribe streamRef (fun item ->
                    task { received2.Add(item) })

            for i in 1..50 do
                do! Stream.publish streamRef i

            do! Task.Delay(2000)

            test <@ received1.Count = 50 @>
            test <@ received2.Count = 50 @>

            do! Stream.unsubscribe sub1
            do! Stream.unsubscribe sub2
        }

    [<Fact>]
    member _.``Unsubscribe stops event delivery`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "unsub-ns" (Guid.NewGuid().ToString())
            let received = ConcurrentBag<int>()

            let! sub =
                Stream.subscribe streamRef (fun item ->
                    task { received.Add(item) })

            // Publish 10 events
            for i in 1..10 do
                do! Stream.publish streamRef i

            do! Task.Delay(1000)
            test <@ received.Count = 10 @>

            // Unsubscribe
            do! Stream.unsubscribe sub

            // Publish 10 more events
            for i in 11..20 do
                do! Stream.publish streamRef i

            do! Task.Delay(1000)

            // Should still be 10 (no new events delivered)
            test <@ received.Count = 10 @>
        }
