open System
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.FSharp
open Testbed.Shared

// No CodeGen needed — uses IFSharpGrain universal interface via FSharpGrain.ref

let redisConn =
    Environment.GetEnvironmentVariable("REDIS_CONNECTION")
    |> Option.ofObj
    |> Option.defaultValue "localhost:6379"

let runStressTest () =
    task {
        printfn "========================================"
        printfn " Orleans.FSharp Stress Test"
        printfn " 2 Silos + Redis + 1000s of actors"
        printfn " Using IFSharpGrain — zero per-grain interfaces"
        printfn "========================================"
        printfn ""

        let builder = Host.CreateApplicationBuilder()

        builder.UseOrleansClient(fun (clientBuilder: IClientBuilder) ->
            clientBuilder.Services.Configure<ClusterOptions>(Action<ClusterOptions>(fun opts ->
                opts.ClusterId <- "testbed-cluster"
                opts.ServiceId <- "testbed-service"))
            |> ignore

            clientBuilder.UseRedisClustering(redisConn) |> ignore
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                clientBuilder.Services,
                Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
                    Orleans.Serialization.SerializationHostingExtensions.AddJsonSerializer(
                        serializerBuilder,
                        isSupported = Func<Type, bool>(fun t ->
                        let ns = t.Namespace |> Option.ofObj |> Option.defaultValue ""
                        not (ns.StartsWith("Orleans.") || ns.StartsWith("Microsoft.") || ns.StartsWith("System."))),
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

        // Typed grain handle factories — no per-grain interfaces needed
        let counter key = FSharpGrain.ref<CounterState, CounterCommand> factory key
        let chat key = FSharpGrain.ref<ChatState, ChatCommand> factory key

        // ============================================================
        // TEST 1: 1000 counter grains — parallel activation
        // ============================================================
        let grainCount = 1000
        printfn "TEST 1: Activate %d counter grains in parallel..." grainCount
        let sw = Stopwatch.StartNew()

        let! _ =
            [| for i in 1..grainCount -> task {
                let! _ = counter $"stress-counter-{i}" |> FSharpGrain.send Increment
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
                let! _ = counter $"throughput-{grainId}" |> FSharpGrain.send Increment
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
            let! state = counter $"throughput-{i}" |> FSharpGrain.send GetValue
            let count = state.Count
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
            let! state = counter $"dist-{i}" |> FSharpGrain.send GetSiloInfo
            // GetSiloInfo returns the silo info as the result (boxed string),
            // but with FSharpGrain.send the result is typed as CounterState.
            // We need to use the raw HandleMessage for this pattern.
            let grain = factory.GetGrain<IFSharpGrain>($"dist-{i}")
            let! result = grain.HandleMessage(box GetSiloInfo)
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
                for msg in 1..msgsPerRoom do
                    let chatMsg =
                        { Sender = $"user-{msg}"
                          Text = $"Message {msg} in room {room}"
                          Timestamp = DateTime.UtcNow }
                    let! _ = chat $"stress-room-{room}" |> FSharpGrain.send (Send chatMsg)
                    ()
            } |]
            |> Task.WhenAll

        sw5.Stop()
        printfn "  DONE: %d messages in %dms (%.0f msg/sec)" totalMsgs sw5.ElapsedMilliseconds (float totalMsgs / sw5.Elapsed.TotalSeconds)

        // Verify random rooms
        for roomId in [ 1; 42; 250; 500 ] do
            let! state = chat $"stress-room-{roomId}" |> FSharpGrain.send GetMessageCount
            let count = state.Messages.Length
            printfn "  Room %d: %d messages %s" roomId count (if count = msgsPerRoom then "OK" else "FAIL")

        printfn ""

        // ============================================================
        // TEST 6: Burst — 5000 concurrent calls on single grain
        // ============================================================
        printfn "TEST 6: Burst — 5000 concurrent calls to single grain..."
        let burstCount = 5000
        let sw6 = Stopwatch.StartNew()
        let burstHandle = counter "burst-target"

        let! _ =
            [| for _ in 1..burstCount -> task {
                let! _ = burstHandle |> FSharpGrain.send Increment
                return ()
            } |]
            |> Task.WhenAll

        sw6.Stop()
        let! burstState = burstHandle |> FSharpGrain.send GetValue
        let burstCount' = burstState.Count
        printfn "  DONE: %d calls in %dms, final count = %d (expected %d) %s"
            burstCount sw6.ElapsedMilliseconds burstCount' burstCount
            (if burstCount' = burstCount then "OK" else "FAIL")
        printfn ""

        // ============================================================
        // SUMMARY
        // ============================================================
        let totalGrains = grainCount + grainPool + distCount + roomCount + 1
        let totalCalls = grainCount + callCount + distCount + totalMsgs + roomCount * 1 + burstCount
        printfn "========================================"
        printfn " STRESS TEST COMPLETE"
        printfn "  Total grains: %d" totalGrains
        printfn "  Total calls:  %d" totalCalls
        printfn "  Chat rooms:   %d" roomCount
        printfn "  Messages:     %d" totalMsgs
        printfn "  Burst single: %d" burstCount
        printfn "  Using IFSharpGrain — no per-grain CodeGen"
        printfn "========================================"

        do! host.StopAsync()
    }

runStressTest().GetAwaiter().GetResult()
