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

            let distinctPayload () =
                received |> Seq.filter (fun i -> i > 0) |> Seq.distinct |> Seq.length

            // The consumer drains through the plain enumerator (the exact `await foreach`
            // desugaring) rather than TaskSeq combinators, and stops once every payload value
            // has been observed. It cannot use a fixed item count: the readiness sentinel below
            // may be delivered any number of times before the payload starts.
            let consumerTask =
                task {
                    let source = Stream.asTaskSeq streamRef
                    use enumerator = source.GetAsyncEnumerator()
                    let mutable go = true

                    while go do
                        let! moved = enumerator.MoveNextAsync()

                        if moved then
                            received.Add enumerator.Current

                            if distinctPayload () >= eventCount then
                                go <- false
                        else
                            go <- false
                }

            // Prove the subscription is live before publishing the payload: memory streams do
            // not replay for a subscriber that attaches late, so anything published before the
            // subscription completes is silently lost. Re-publishing the 0 sentinel until one
            // copy is observed replaces the guessed 500 ms setup delay this test used to rely
            // on -- the delay is exactly what a slower CI runner turned into lost first events
            // and a hung take (main red since 2026-08-12).
            let probeDeadline = DateTime.UtcNow.AddSeconds 30.0

            while received.IsEmpty && DateTime.UtcNow < probeDeadline do
                do! Stream.publish streamRef 0
                do! Task.Delay 100

            test <@ not received.IsEmpty @>

            for i in 1..eventCount do
                do! Stream.publish streamRef i

            // Wait for consumption with a generous bound; the passing path exits as soon as the
            // last distinct payload value arrives.
            let! completed = Task.WhenAny(consumerTask, Task.Delay(TimeSpan.FromSeconds(30.0)))
            test <@ Object.ReferenceEquals(completed, consumerTask) @>

            let items =
                received |> Seq.filter (fun i -> i > 0) |> Seq.distinct |> Seq.sort |> List.ofSeq

            test <@ items = [ 1..eventCount ] @>

            let asyncStream = streamProvider.GetStream<int>(streamRef.StreamId)
            let! remainingSubscriptions = asyncStream.GetAllSubscriptionHandles()
            test <@ remainingSubscriptions.Count = 0 @>
        }

    // -----------------------------------------------------------------------
    // Cursors: subscribeWithToken / subscribeFromWithToken over a rewindable provider
    // -----------------------------------------------------------------------
    //
    // The fixture's provider is AddMemoryStreams, and Orleans' memory streams are rewindable, so
    // these run against a provider that really does hand out cursors. Timing follows the
    // asTaskSeq test's discipline: a sentinel is re-published until the subscription is proven
    // live (memory streams do not replay for a late subscriber), and every wait is a poll with a
    // generous deadline rather than a guessed sleep.

    /// Re-publishes a 0 sentinel until the subscription observes something, then returns.
    member private _.ProveSubscriptionLive(streamRef: StreamRef<int>, seen: unit -> bool) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 30.0

            while not (seen ()) && DateTime.UtcNow < deadline do
                do! Stream.publish streamRef 0
                do! Task.Delay 100

            test <@ seen () @>
        }

    member private _.WaitUntil(condition: unit -> bool) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 30.0

            while not (condition ()) && DateTime.UtcNow < deadline do
                do! Task.Delay 100

            test <@ condition () @>
        }

    member private this.SeedCheckpoint(streamRef: StreamRef<int>, itemCount: int, checkpointItem: int) =
        task {
            let received = ConcurrentQueue<int * StreamSequenceToken option>()

            let! sub =
                Stream.subscribeWithToken streamRef (fun item token ->
                    task { received.Enqueue(item, token) })

            do! this.ProveSubscriptionLive(streamRef, fun () -> not received.IsEmpty)

            for item in 1..itemCount do
                do! Stream.publish streamRef item

            let payload () = received |> Seq.filter (fun (item, _) -> item > 0) |> Seq.toList

            do!
                this.WaitUntil(fun () ->
                    payload ()
                    |> List.map fst
                    |> List.distinct
                    |> List.length
                    |> (=) itemCount)

            let checkpoint =
                payload ()
                |> List.find (fun (item, _) -> item = checkpointItem)
                |> snd

            test <@ checkpoint.IsSome @>
            return sub, checkpoint.Value
        }

    [<Fact>]
    member this.``batch publish and batch subscription preserve every item`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "batch-ns" (Guid.NewGuid().ToString())
            let batches = ConcurrentQueue<StreamBatchItem<int> array>()

            let! sub =
                Stream.subscribeBatch streamRef (
                    StreamBatchHandlers.create (fun batch ->
                        task {
                            batches.Enqueue batch
                        })
                )

            do!
                this.ProveSubscriptionLive(
                    streamRef,
                    fun () -> not batches.IsEmpty
                )

            do! Stream.publishBatch streamRef [ 1..10 ]

            let received () =
                batches
                |> Seq.collect id
                |> Seq.map _.Item
                |> Seq.filter (fun item -> item > 0)
                |> Seq.distinct
                |> Seq.sort
                |> Seq.toList

            do! this.WaitUntil(fun () -> (received ()).Length = 10)
            test <@ received () = [ 1..10 ] @>
            do! Stream.unsubscribe sub
        }

    [<Fact>]
    member this.``publishBatchFrom accepts a real provider token and publishes the batch`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "batch-token-ns" (Guid.NewGuid().ToString())
            let batches = ConcurrentQueue<StreamBatchItem<int> array>()

            let! sub =
                Stream.subscribeBatch streamRef (
                    StreamBatchHandlers.create (fun batch ->
                        task {
                            batches.Enqueue batch
                        })
                )

            let checkpoint () =
                batches
                |> Seq.collect id
                |> Seq.tryPick _.Token

            do! this.ProveSubscriptionLive(streamRef, fun () -> checkpoint () |> Option.isSome)
            let checkpointToken = checkpoint () |> Option.get

            // Orleans' memory adapter owns its delivery cursors and intentionally ignores a
            // producer-supplied token. This proves the live-provider overload accepts a real
            // cursor; StreamingTests proves the wrapper forwards that exact object.
            do! Stream.publishBatchFrom streamRef checkpointToken [ 1; 2; 3 ]

            let received () =
                batches
                |> Seq.collect id
                |> Seq.map _.Item
                |> Seq.filter (fun item -> item > 0)
                |> Seq.distinct
                |> Seq.sort
                |> Seq.toList

            do! this.WaitUntil(fun () -> (received ()).Length = 3)
            test <@ received () = [ 1; 2; 3 ] @>
            do! Stream.unsubscribe sub
        }

    [<Fact>]
    member this.``resumeAllBatch reattaches every durable batch subscription`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "resume-batch-ns" (Guid.NewGuid().ToString())
            let initiallyReceived = ConcurrentQueue<int>()

            let! sub =
                Stream.subscribeBatch streamRef (
                    StreamBatchHandlers.create (fun batch ->
                        task {
                            batch |> Array.iter (fun item -> initiallyReceived.Enqueue item.Item)
                        })
                )

            do! this.ProveSubscriptionLive(streamRef, fun () -> not initiallyReceived.IsEmpty)

            let resumed = ConcurrentQueue<int>()

            do!
                Stream.resumeAllBatch streamRef (
                    StreamBatchHandlers.create (fun batch ->
                        task {
                            batch |> Array.iter (fun item -> resumed.Enqueue item.Item)
                        })
                )

            do! Stream.publishBatch streamRef [ 1; 2; 3 ]
            do! this.WaitUntil(fun () -> resumed |> Seq.filter (fun item -> item > 0) |> Seq.length = 3)

            let received = resumed |> Seq.filter (fun item -> item > 0) |> Seq.sort |> Seq.toList
            test <@ received = [ 1; 2; 3 ] @>
            do! Stream.unsubscribe sub
        }

    [<Fact>]
    member _.``producer completion and error preserve Orleans provider semantics`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "terminal-ns" (Guid.NewGuid().ToString())

            // Orleans 10.2.2 exposes producer terminal methods, but its persistent producer
            // deliberately throws NotImplementedException for both. The F# wrapper must not hide
            // or translate that provider contract. Consumer terminal callbacks are exercised by
            // the adapter unit tests and work for providers which emit them.
            let! completion =
                Assert.ThrowsAsync<NotImplementedException>(fun () -> Stream.complete streamRef :> Task)

            let! failure =
                Assert.ThrowsAsync<NotImplementedException>(fun () ->
                    Stream.fail streamRef (InvalidOperationException "stream failed") :> Task)

            test <@ completion.Message.Contains "not implemented" @>
            test <@ failure.Message.Contains "not implemented" @>
        }

    [<Fact>]
    member this.``filter data is evaluated by the Orleans stream filter`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "server-filter-ns" (Guid.NewGuid().ToString())
            let received = ConcurrentQueue<int>()

            let! sub =
                Stream.subscribeFiltered
                    streamRef
                    "even"
                    (StreamHandlers.create (fun item ->
                        task {
                            received.Enqueue item
                        }))

            do! this.ProveSubscriptionLive(streamRef, fun () -> not received.IsEmpty)

            for item in 1..10 do
                do! Stream.publish streamRef item

            let payload () = received |> Seq.filter (fun item -> item > 0) |> Seq.distinct |> Seq.sort |> Seq.toList
            do! this.WaitUntil(fun () -> (payload ()).Length = 5)
            test <@ payload () = [ 2; 4; 6; 8; 10 ] @>
            do! Stream.unsubscribe sub
        }

    /// <summary>
    /// Publishes <paramref name="nudge"/> until <paramref name="condition"/> holds.
    /// A rewound subscription's backlog is delivered on the pulling agent's next cycle for the
    /// stream, and that cycle is driven by new data: measured on this fixture, a resumed
    /// subscription saw nothing at all for 30 s of idling, and one further publish flushed the
    /// whole backlog from the checkpoint plus the new event. So the wake-up is published rather
    /// than waited for — the same publish-until-observed rule the sentinel above follows.
    /// </summary>
    member private _.NudgeUntil(streamRef: StreamRef<int>, nudge: int, condition: unit -> bool) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 30.0

            while not (condition ()) && DateTime.UtcNow < deadline do
                do! Stream.publish streamRef nudge
                do! Task.Delay 100

            test <@ condition () @>
        }

    [<Fact>]
    member this.``resume and resumeAllHandlers reattach item callbacks`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let oneRef = Stream.getStream<int> streamProvider "resume-item-ns" (Guid.NewGuid().ToString())
            let initial = ConcurrentQueue<int>()

            let! one =
                Stream.subscribeHandlers oneRef (StreamHandlers.create (fun item -> task { initial.Enqueue item }))

            do! this.ProveSubscriptionLive(oneRef, fun () -> not initial.IsEmpty)

            let resumed = ConcurrentQueue<int>()

            let! resumedSub =
                Stream.resume one (StreamHandlers.create (fun item -> task { resumed.Enqueue item }))

            do! Stream.publish oneRef 11
            do! this.WaitUntil(fun () -> Seq.contains 11 resumed)
            test <@ Seq.contains 11 resumed @>
            do! Stream.unsubscribe resumedSub

            let allRef = Stream.getStream<int> streamProvider "resume-all-item-ns" (Guid.NewGuid().ToString())
            let allInitial = ConcurrentQueue<int>()

            let! allSub =
                Stream.subscribeHandlers
                    allRef
                    (StreamHandlers.create (fun item -> task { allInitial.Enqueue item }))

            do! this.ProveSubscriptionLive(allRef, fun () -> not allInitial.IsEmpty)

            let allResumed = ConcurrentQueue<int>()

            do!
                Stream.resumeAllHandlers
                    allRef
                    (StreamHandlers.create (fun item -> task { allResumed.Enqueue item }))

            do! Stream.publish allRef 22
            do! this.WaitUntil(fun () -> Seq.contains 22 allResumed)
            test <@ Seq.contains 22 allResumed @>
            do! Stream.unsubscribe allSub
        }

    [<Fact>]
    member this.``resumeFrom and subscribeFromHandlers rewind item callbacks`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let resumeRef = Stream.getStream<int> streamProvider "resume-from-item-ns" (Guid.NewGuid().ToString())
            let! seedSub, checkpoint = this.SeedCheckpoint(resumeRef, 5, 3)
            let resumed = ConcurrentQueue<int>()

            let! resumedSub =
                Stream.resumeFrom
                    seedSub
                    checkpoint
                    (StreamHandlers.create (fun item -> task { resumed.Enqueue item }))

            do! this.NudgeUntil(resumeRef, 6, fun () -> Seq.contains 4 resumed && Seq.contains 5 resumed)
            // ResumeAsync reattaches the existing durable handle. Since that handle already
            // acknowledged the checkpoint event, Orleans continues after it rather than
            // redelivering it as a new SubscribeAsync(..., token) call does.
            test <@ not (Seq.contains 3 resumed) @>
            test <@ not (Seq.contains 1 resumed) @>
            test <@ not (Seq.contains 2 resumed) @>
            do! Stream.unsubscribe resumedSub

            let subscribeRef =
                Stream.getStream<int> streamProvider "subscribe-from-handlers-ns" (Guid.NewGuid().ToString())

            let! sourceSub, sourceCheckpoint = this.SeedCheckpoint(subscribeRef, 5, 3)
            do! Stream.unsubscribe sourceSub
            let subscribed = ConcurrentQueue<int>()

            let! subscribedSub =
                Stream.subscribeFromHandlers
                    subscribeRef
                    sourceCheckpoint
                    (StreamHandlers.create (fun item -> task { subscribed.Enqueue item }))

            do!
                this.NudgeUntil(
                    subscribeRef,
                    6,
                    fun () -> Seq.contains 4 subscribed && Seq.contains 5 subscribed
                )

            test <@ Seq.contains 3 subscribed @>
            test <@ not (Seq.contains 1 subscribed) @>
            test <@ not (Seq.contains 2 subscribed) @>
            do! Stream.unsubscribe subscribedSub
        }

    [<Fact>]
    member this.``resumeBatch reattaches one durable batch subscription`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "resume-one-batch-ns" (Guid.NewGuid().ToString())
            let initial = ConcurrentQueue<int>()

            let! sub =
                Stream.subscribeBatch streamRef (
                    StreamBatchHandlers.create (fun batch ->
                        task { batch |> Array.iter (fun item -> initial.Enqueue item.Item) })
                )

            do! this.ProveSubscriptionLive(streamRef, fun () -> not initial.IsEmpty)
            let resumed = ConcurrentQueue<int>()

            let! resumedSub =
                Stream.resumeBatch sub (
                    StreamBatchHandlers.create (fun batch ->
                        task { batch |> Array.iter (fun item -> resumed.Enqueue item.Item) })
                )

            do! Stream.publishBatch streamRef [ 31; 32 ]
            do! this.WaitUntil(fun () -> Seq.contains 31 resumed && Seq.contains 32 resumed)
            test <@ resumed |> Seq.filter (fun item -> item > 0) |> Seq.sort |> Seq.toList = [ 31; 32 ] @>
            do! Stream.unsubscribe resumedSub
        }

    [<Fact>]
    member this.``subscribeBatchFrom and resumeBatchFrom rewind batch callbacks`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")

            let subscribeRef =
                Stream.getStream<int> streamProvider "subscribe-from-batch-ns" (Guid.NewGuid().ToString())

            let! sourceSub, checkpoint = this.SeedCheckpoint(subscribeRef, 5, 3)
            do! Stream.unsubscribe sourceSub
            let subscribed = ConcurrentQueue<int>()

            let! subscribedSub =
                Stream.subscribeBatchFrom subscribeRef checkpoint (
                    StreamBatchHandlers.create (fun batch ->
                        task { batch |> Array.iter (fun item -> subscribed.Enqueue item.Item) })
                )

            do!
                this.NudgeUntil(
                    subscribeRef,
                    6,
                    fun () -> Seq.contains 4 subscribed && Seq.contains 5 subscribed
                )

            test <@ Seq.contains 3 subscribed @>
            test <@ not (Seq.contains 1 subscribed) @>
            test <@ not (Seq.contains 2 subscribed) @>
            do! Stream.unsubscribe subscribedSub

            let resumeRef = Stream.getStream<int> streamProvider "resume-from-batch-ns" (Guid.NewGuid().ToString())
            let! resumeSource, resumeCheckpoint = this.SeedCheckpoint(resumeRef, 5, 3)
            let resumed = ConcurrentQueue<int>()

            let! resumedSub =
                Stream.resumeBatchFrom resumeSource resumeCheckpoint (
                    StreamBatchHandlers.create (fun batch ->
                        task { batch |> Array.iter (fun item -> resumed.Enqueue item.Item) })
                )

            do! this.NudgeUntil(resumeRef, 6, fun () -> Seq.contains 4 resumed && Seq.contains 5 resumed)
            test <@ not (Seq.contains 3 resumed) @>
            test <@ not (Seq.contains 1 resumed) @>
            test <@ not (Seq.contains 2 resumed) @>
            do! Stream.unsubscribe resumedSub
        }

    [<Fact>]
    member this.``subscribeWithToken yields a cursor that subscribeFrom resumes from`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let key = Guid.NewGuid().ToString()
            let streamRef = Stream.getStream<int> streamProvider "cursor-ns" key
            let received = ConcurrentQueue<int * StreamSequenceToken option>()

            let! sub =
                Stream.subscribeWithToken streamRef (fun item token ->
                    task { received.Enqueue(item, token) })

            do! this.ProveSubscriptionLive(streamRef, fun () -> not received.IsEmpty)

            for i in 1..5 do
                do! Stream.publish streamRef i

            let payload () =
                received |> Seq.filter (fun (i, _) -> i > 0) |> Seq.toList

            do! this.WaitUntil(fun () -> payload () |> List.map fst |> List.distinct |> List.length = 5)

            // The whole point of the member: the cursor exists and is not a stub.
            test <@ payload () |> List.forall (fun (_, token) -> Option.isSome token) @>

            let checkpoint = payload () |> List.find (fun (i, _) -> i = 3) |> snd
            test <@ checkpoint.IsSome @>

            do! Stream.unsubscribe sub

            // Resume a fresh subscription from that cursor. Without a token a new subscriber sees
            // nothing already published, so any of 4/5 arriving proves the rewind took effect;
            // 1/2 never arriving proves it rewound TO the checkpoint and not to the start.
            let resumed = ConcurrentQueue<int>()

            let! resumedSub =
                Stream.subscribeFrom streamRef checkpoint.Value (fun item -> task { resumed.Enqueue item })

            do! this.NudgeUntil(streamRef, 6, fun () -> Seq.contains 4 resumed && Seq.contains 5 resumed)

            // Rewind is inclusive: the event that produced the checkpoint is delivered again.
            test <@ Seq.contains 3 resumed @>
            test <@ not (Seq.contains 1 resumed) @>
            test <@ not (Seq.contains 2 resumed) @>

            do! Stream.unsubscribe resumedSub
        }

    [<Fact>]
    member this.``subscribeFromWithToken resumes from a checkpoint and keeps handing out cursors`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let key = Guid.NewGuid().ToString()
            let streamRef = Stream.getStream<int> streamProvider "cursor-resume-ns" key
            let received = ConcurrentQueue<int * StreamSequenceToken option>()

            let! sub =
                Stream.subscribeWithToken streamRef (fun item token ->
                    task { received.Enqueue(item, token) })

            do! this.ProveSubscriptionLive(streamRef, fun () -> not received.IsEmpty)

            for i in 1..5 do
                do! Stream.publish streamRef i

            let payload () =
                received |> Seq.filter (fun (i, _) -> i > 0) |> Seq.toList

            do! this.WaitUntil(fun () -> payload () |> List.map fst |> List.distinct |> List.length = 5)

            let checkpoint = payload () |> List.find (fun (i, _) -> i = 3) |> snd
            test <@ checkpoint.IsSome @>

            do! Stream.unsubscribe sub

            let resumed = ConcurrentQueue<int * StreamSequenceToken option>()

            let! resumedSub =
                Stream.subscribeFromWithToken streamRef checkpoint.Value (fun item token ->
                    task { resumed.Enqueue(item, token) })

            do! this.NudgeUntil(
                streamRef,
                6,
                fun () ->
                    let items = resumed |> Seq.map fst |> Seq.toList
                    List.contains 4 items && List.contains 5 items
            )

            let resumedItems = resumed |> Seq.map fst |> Seq.toList
            test <@ List.contains 3 resumedItems @>
            test <@ not (List.contains 1 resumedItems) @>
            test <@ not (List.contains 2 resumedItems) @>

            // Checkpointing survives the rewind: the resumed subscription still carries cursors,
            // so a consumer can keep saving its position after resuming.
            test <@ resumed |> Seq.forall (fun (_, token) -> Option.isSome token) @>

            let secondCheckpoint = resumed |> Seq.find (fun (i, _) -> i = 5) |> snd
            test <@ secondCheckpoint.IsSome @>
            test <@ secondCheckpoint.Value.CompareTo(checkpoint.Value) > 0 @>

            do! Stream.unsubscribe resumedSub
        }

    [<Fact>]
    member this.``subscribeFromFiltered combines rewind and server-side filtering`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<int> streamProvider "cursor-filter-ns" (Guid.NewGuid().ToString())
            let received = ConcurrentQueue<int * StreamSequenceToken option>()

            let! sub =
                Stream.subscribeWithToken streamRef (fun item token ->
                    task { received.Enqueue(item, token) })

            do! this.ProveSubscriptionLive(streamRef, fun () -> not received.IsEmpty)

            for item in 1..5 do
                do! Stream.publish streamRef item

            let payload () = received |> Seq.filter (fun (item, _) -> item > 0) |> Seq.toList
            do! this.WaitUntil(fun () -> payload () |> List.map fst |> List.distinct |> List.length = 5)

            let checkpoint = payload () |> List.find (fun (item, _) -> item = 3) |> snd
            test <@ checkpoint.IsSome @>
            do! Stream.unsubscribe sub

            let resumed = ConcurrentQueue<int>()

            let! resumedSub =
                Stream.subscribeFromFiltered
                    streamRef
                    checkpoint.Value
                    "even"
                    (StreamHandlers.create (fun item -> task { resumed.Enqueue item }))

            do! this.NudgeUntil(streamRef, 6, fun () -> Seq.contains 4 resumed)

            test <@ Seq.contains 4 resumed @>
            test <@ not (Seq.contains 3 resumed) @>
            test <@ not (Seq.contains 5 resumed) @>
            do! Stream.unsubscribe resumedSub
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
