open System
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.FSharp
open Orleans.Streams
open Orleans.Runtime
open System.Collections.Concurrent
open Testbed.Shared

let _ =
    let asm = typeof<Testbed.CodeGen.CounterGrainImpl>.Assembly
    asm.GetTypes() |> ignore
    true

let redisConn =
    Environment.GetEnvironmentVariable("REDIS_CONNECTION")
    |> Option.ofObj
    |> Option.defaultValue "localhost:6379"

let runStressTest () =
    task {
        printfn "========================================"
        printfn " Orleans.FSharp Stress Test"
        printfn " 2 Silos + Redis + 1000s of actors"
        printfn "========================================"
        printfn ""

        let builder = Host.CreateApplicationBuilder()

        builder.UseOrleansClient(fun (clientBuilder: IClientBuilder) ->
            clientBuilder.Services.Configure<ClusterOptions>(Action<ClusterOptions>(fun opts ->
                opts.ClusterId <- "testbed-cluster"
                opts.ServiceId <- "testbed-service"))
            |> ignore

            clientBuilder.UseRedisClustering(redisConn) |> ignore
            clientBuilder.AddMemoryStreams("StreamProvider") |> ignore

            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                clientBuilder.Services,
                Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
                    Orleans.Serialization.SerializationHostingExtensions.AddJsonSerializer(
                        serializerBuilder,
                        isSupported = Func<Type, bool>(fun _ -> true),
                        jsonSerializerOptions = Orleans.FSharp.FSharpJson.serializerOptions)
                    |> ignore))
            |> ignore)
        |> ignore

        let host = builder.Build()
        do! host.StartAsync()
        let factory = host.Services.GetRequiredService<IGrainFactory>()
        do! Task.Delay(5000)
        printfn "Connected to cluster via Redis at %s" redisConn
        printfn ""

        // ============================================================
        // TEST 1: 1000 counter grains — parallel activation
        // ============================================================
        let grainCount = 1000
        printfn "TEST 1: Activate %d counter grains in parallel..." grainCount
        let sw = Stopwatch.StartNew()

        let! _ =
            [| for i in 1..grainCount -> task {
                let grain = factory.GetGrain<ICounterGrain>($"stress-counter-{i}")
                let! _ = grain.HandleMessage(Increment)
                return ()
            } |]
            |> Task.WhenAll

        sw.Stop()
        printfn "  DONE: %d grains in %dms (%.0f grains/sec)" grainCount sw.ElapsedMilliseconds (float grainCount / sw.Elapsed.TotalSeconds)
        printfn ""

        // ============================================================
        // TEST 2: 10000 calls across 100 grains — throughput
        // ============================================================
        let callCount = 10_000
        let grainPool = 100
        printfn "TEST 2: %d calls across %d grains (throughput)..." callCount grainPool
        let sw2 = Stopwatch.StartNew()

        let! _ =
            [| for i in 1..callCount -> task {
                let grainId = i % grainPool
                let grain = factory.GetGrain<ICounterGrain>($"throughput-{grainId}")
                let! _ = grain.HandleMessage(Increment)
                return ()
            } |]
            |> Task.WhenAll

        sw2.Stop()
        printfn "  DONE: %d calls in %dms (%.0f calls/sec)" callCount sw2.ElapsedMilliseconds (float callCount / sw2.Elapsed.TotalSeconds)
        printfn ""

        // ============================================================
        // TEST 3: State consistency — verify counts
        // ============================================================
        printfn "TEST 3: Verify state consistency across %d grains..." grainPool
        let mutable correct = 0
        let mutable wrong = 0
        let expectedPerGrain = callCount / grainPool

        for i in 0..grainPool - 1 do
            let grain = factory.GetGrain<ICounterGrain>($"throughput-{i}")
            let! result = grain.HandleMessage(GetValue)
            let count = result :?> int
            if count = expectedPerGrain then correct <- correct + 1
            else
                wrong <- wrong + 1
                printfn "  MISMATCH: grain %d has %d (expected %d)" i count expectedPerGrain

        printfn "  RESULT: %d/%d correct" correct grainPool
        if wrong > 0 then printfn "  WARNING: %d grains have wrong count" wrong
        printfn ""

        // ============================================================
        // TEST 4: Silo distribution
        // ============================================================
        let distCount = 50
        printfn "TEST 4: Silo distribution across %d grains..." distCount
        let mutable silo1 = 0
        let mutable silo2 = 0

        for i in 1..distCount do
            let grain = factory.GetGrain<ICounterGrain>($"dist-{i}")
            let! result = grain.HandleMessage(GetSiloInfo)
            let info = result :?> string
            if info.Contains("silo1") then silo1 <- silo1 + 1
            else silo2 <- silo2 + 1

        printfn "  Silo1: %d grains (%d%%)" silo1 (silo1 * 100 / distCount)
        printfn "  Silo2: %d grains (%d%%)" silo2 (silo2 * 100 / distCount)
        printfn ""

        // ============================================================
        // TEST 5: 500 chat rooms, 10 messages each
        // ============================================================
        let roomCount = 500
        let msgsPerRoom = 10
        let totalMsgs = roomCount * msgsPerRoom
        printfn "TEST 5: %d chat rooms x %d messages = %d total..." roomCount msgsPerRoom totalMsgs
        let sw5 = Stopwatch.StartNew()

        let! _ =
            [| for room in 1..roomCount -> task {
                let grain = factory.GetGrain<IChatGrain>($"stress-room-{room}")
                for msg in 1..msgsPerRoom do
                    let chatMsg =
                        { Sender = $"user-{msg}"
                          Text = $"Message {msg} in room {room}"
                          Timestamp = DateTime.UtcNow }
                    let! _ = grain.HandleMessage(Send chatMsg)
                    ()
            } |]
            |> Task.WhenAll

        sw5.Stop()
        printfn "  DONE: %d messages in %dms (%.0f msg/sec)" totalMsgs sw5.ElapsedMilliseconds (float totalMsgs / sw5.Elapsed.TotalSeconds)

        // Verify random rooms
        for roomId in [ 1; 42; 250; 500 ] do
            let grain = factory.GetGrain<IChatGrain>($"stress-room-{roomId}")
            let! result = grain.HandleMessage(GetMessageCount)
            let count = result :?> int
            printfn "  Room %d: %d messages %s" roomId count (if count = msgsPerRoom then "OK" else "FAIL")

        printfn ""

        // ============================================================
        // TEST 6: Burst — 5000 concurrent calls on single grain
        // ============================================================
        printfn "TEST 6: Burst — 5000 concurrent calls to single grain..."
        let burstCount = 5000
        let sw6 = Stopwatch.StartNew()
        let burstGrain = factory.GetGrain<ICounterGrain>("burst-target")

        let! _ =
            [| for _ in 1..burstCount -> task {
                let! _ = burstGrain.HandleMessage(Increment)
                return ()
            } |]
            |> Task.WhenAll

        sw6.Stop()
        let! burstResult = burstGrain.HandleMessage(GetValue)
        let burstCount' = burstResult :?> int
        printfn "  DONE: %d calls in %dms, final count = %d (expected %d) %s"
            burstCount sw6.ElapsedMilliseconds burstCount' burstCount
            (if burstCount' = burstCount then "OK" else "FAIL")
        printfn ""

        // ============================================================
        // TEST 7: Streaming — publish 1000 events, verify delivery
        // ============================================================
        let streamEventCount = 1000
        printfn "TEST 7: Stream — publish %d events, verify delivery..." streamEventCount
        let streamProvider = host.Services.GetRequiredService<IClusterClient>().GetStreamProvider("StreamProvider")
        let streamId = StreamId.Create("metrics", "stress-stream-1")
        let stream = streamProvider.GetStream<MetricEvent>(streamId)
        let received = ConcurrentBag<MetricEvent>()

        let! sub = stream.SubscribeAsync(
            Func<MetricEvent, StreamSequenceToken, Task>(fun evt _token -> task {
                received.Add(evt)
            }))

        let sw7 = Stopwatch.StartNew()
        for i in 1..streamEventCount do
            let evt = { Source = $"sensor-{i % 10}"; Value = float i * 1.5; Timestamp = DateTime.UtcNow }
            do! stream.OnNextAsync(evt)

        // Wait for delivery
        do! Task.Delay(2000)
        sw7.Stop()

        printfn "  Published: %d events" streamEventCount
        printfn "  Received:  %d events" received.Count
        printfn "  Time:      %dms (%.0f events/sec)" sw7.ElapsedMilliseconds (float streamEventCount / sw7.Elapsed.TotalSeconds)
        printfn "  Status:    %s" (if received.Count = streamEventCount then "OK" else $"PARTIAL ({received.Count}/{streamEventCount})")

        do! sub.UnsubscribeAsync()
        printfn ""

        // ============================================================
        // TEST 8: Multi-stream — 50 streams in parallel
        // ============================================================
        let parallelStreams = 50
        let eventsPerStream = 100
        printfn "TEST 8: %d parallel streams x %d events = %d total..." parallelStreams eventsPerStream (parallelStreams * eventsPerStream)
        let allReceived = ConcurrentBag<string>()
        let sw8 = Stopwatch.StartNew()

        let! _ =
            [| for s in 1..parallelStreams -> task {
                let sid = StreamId.Create("parallel", $"stream-{s}")
                let strm = streamProvider.GetStream<MetricEvent>(sid)
                let bag = ConcurrentBag<MetricEvent>()

                let! subscription = strm.SubscribeAsync(
                    Func<MetricEvent, StreamSequenceToken, Task>(fun evt _token -> task {
                        bag.Add(evt)
                        allReceived.Add($"{s}-{evt.Source}")
                    }))

                for e in 1..eventsPerStream do
                    let evt = { Source = $"evt-{e}"; Value = float e; Timestamp = DateTime.UtcNow }
                    do! strm.OnNextAsync(evt)

                do! Task.Delay(500)
                do! subscription.UnsubscribeAsync()
            } |]
            |> Task.WhenAll

        sw8.Stop()
        let totalStreamEvents = parallelStreams * eventsPerStream
        printfn "  DONE: %d events across %d streams in %dms (%.0f events/sec)"
            totalStreamEvents parallelStreams sw8.ElapsedMilliseconds
            (float totalStreamEvents / sw8.Elapsed.TotalSeconds)
        printfn "  Total received: %d" allReceived.Count
        printfn ""

        // ============================================================
        // SUMMARY
        // ============================================================
        let totalGrains = grainCount + grainPool + distCount + roomCount + 1
        let totalCalls = grainCount + callCount + distCount + totalMsgs + roomCount * 1 + burstCount + streamEventCount + totalStreamEvents
        printfn "========================================"
        printfn " STRESS TEST COMPLETE"
        printfn "  Total grains: %d" totalGrains
        printfn "  Total calls:  %d" totalCalls
        printfn "  Chat rooms:   %d" roomCount
        printfn "  Messages:     %d" totalMsgs
        printfn "  Burst single: %d" burstCount
        printfn "  Streams:      %d events across %d streams" (streamEventCount + totalStreamEvents) (1 + parallelStreams)
        printfn "  2 silos + Redis"
        printfn "========================================"

        do! host.StopAsync()
    }

runStressTest().GetAwaiter().GetResult()
