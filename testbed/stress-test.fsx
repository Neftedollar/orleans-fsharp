#!/usr/bin/env dotnet fsi

// Stress test: 1000s of actors across 2 Orleans silos via Redis
// Run: docker compose up -d && dotnet fsi stress-test.fsx

#r "nuget: Microsoft.Orleans.Client, 10.0.1"
#r "nuget: Orleans.Clustering.Redis, 10.0.1"
#r "nuget: Microsoft.Extensions.Hosting, 10.0.0"
#r "../src/Orleans.FSharp/bin/Debug/net10.0/Orleans.FSharp.dll"
#r "src/Shared/bin/Debug/net10.0/Shared.dll"
#r "src/CodeGen/bin/Debug/net10.0/CodeGen.dll"

open System
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Testbed.Shared

// Force CodeGen load
let _ = typeof<Testbed.CodeGen.CounterGrainImpl>.Assembly.GetTypes()

let redisConn = "localhost:6379"

let stress () = task {
    printfn "========================================"
    printfn " Orleans.FSharp Stress Test"
    printfn " 2 Silos + Redis + 1000s of actors"
    printfn "========================================"
    printfn ""

    // Connect client
    let builder = Host.CreateApplicationBuilder()
    builder.UseOrleansClient(fun (cb: IClientBuilder) ->
        cb.Services.Configure<ClusterOptions>(Action<ClusterOptions>(fun o ->
            o.ClusterId <- "testbed-cluster"
            o.ServiceId <- "testbed-service")) |> ignore
        cb.UseRedisClustering(redisConn) |> ignore
        Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
            cb.Services,
            Action<Orleans.Serialization.ISerializerBuilder>(fun sb ->
                Orleans.Serialization.SerializationHostingExtensions.AddJsonSerializer(
                    sb, isSupported = Func<Type, bool>(fun _ -> true),
                    jsonSerializerOptions = Orleans.FSharp.FSharpJson.serializerOptions) |> ignore))
        |> ignore) |> ignore

    let host = builder.Build()
    do! host.StartAsync()
    let factory = host.Services.GetRequiredService<IGrainFactory>()
    do! Task.Delay(3000)
    printfn "Connected to cluster."
    printfn ""

    // ============================================================
    // Test 1: 1000 counter grains — parallel activation
    // ============================================================
    let grainCount = 1000
    printfn "TEST 1: Activate %d counter grains in parallel" grainCount
    let sw = Stopwatch.StartNew()

    let! results =
        [| for i in 1..grainCount -> task {
            let grain = factory.GetGrain<ICounterGrain>($"stress-counter-{i}")
            let! _ = grain.HandleMessage(Increment)
            return i
        } |]
        |> Task.WhenAll

    sw.Stop()
    printfn "  %d grains activated in %dms (%.0f grains/sec)" grainCount sw.ElapsedMilliseconds (float grainCount / sw.Elapsed.TotalSeconds)
    printfn ""

    // ============================================================
    // Test 2: 10000 calls across 100 grains — throughput
    // ============================================================
    let callCount = 10_000
    let grainPool = 100
    printfn "TEST 2: %d calls across %d grains (throughput)" callCount grainPool
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
    printfn "  %d calls in %dms (%.0f calls/sec)" callCount sw2.ElapsedMilliseconds (float callCount / sw2.Elapsed.TotalSeconds)
    printfn ""

    // ============================================================
    // Test 3: Verify state consistency — each grain got correct count
    // ============================================================
    printfn "TEST 3: Verify state consistency across %d grains" grainPool
    let mutable correct = 0
    let mutable wrong = 0
    let expectedPerGrain = callCount / grainPool

    for i in 0..grainPool-1 do
        let grain = factory.GetGrain<ICounterGrain>($"throughput-{i}")
        let! result = grain.HandleMessage(GetValue)
        let count = result :?> int
        if count = expectedPerGrain then correct <- correct + 1
        else wrong <- wrong + 1

    printfn "  Correct: %d/%d, Wrong: %d" correct grainPool wrong
    printfn ""

    // ============================================================
    // Test 4: Silo distribution — check load balancing
    // ============================================================
    printfn "TEST 4: Silo distribution across %d grains" 20
    let mutable silo1count = 0
    let mutable silo2count = 0

    for i in 1..20 do
        let grain = factory.GetGrain<ICounterGrain>($"dist-check-{i}")
        let! result = grain.HandleMessage(GetSiloInfo)
        let info = result :?> string
        if info.Contains("silo1") then silo1count <- silo1count + 1
        else silo2count <- silo2count + 1

    printfn "  Silo1: %d grains, Silo2: %d grains" silo1count silo2count
    printfn ""

    // ============================================================
    // Test 5: Chat grains — 500 rooms, 10 messages each
    // ============================================================
    let roomCount = 500
    let msgsPerRoom = 10
    printfn "TEST 5: %d chat rooms, %d messages each (%d total)" roomCount msgsPerRoom (roomCount * msgsPerRoom)
    let sw5 = Stopwatch.StartNew()

    let! _ =
        [| for room in 1..roomCount -> task {
            let grain = factory.GetGrain<IChatGrain>($"stress-room-{room}")
            for msg in 1..msgsPerRoom do
                let chatMsg = { Sender = $"user-{msg}"; Text = $"Message {msg} in room {room}"; Timestamp = DateTime.UtcNow }
                let! _ = grain.HandleMessage(Send chatMsg)
                ()
        } |]
        |> Task.WhenAll

    sw5.Stop()
    printfn "  %d messages sent in %dms (%.0f msg/sec)" (roomCount * msgsPerRoom) sw5.ElapsedMilliseconds (float (roomCount * msgsPerRoom) / sw5.Elapsed.TotalSeconds)

    // Verify a random room
    let verifyRoom = factory.GetGrain<IChatGrain>("stress-room-42")
    let! countResult = verifyRoom.HandleMessage(GetMessageCount)
    let count = countResult :?> int
    printfn "  Room 42 has %d messages (expected %d)" count msgsPerRoom
    printfn ""

    // ============================================================
    // Summary
    // ============================================================
    printfn "========================================"
    printfn " STRESS TEST COMPLETE"
    printfn " Grains activated: %d" (grainCount + grainPool + 20 + roomCount)
    printfn " Total calls: %d" (grainCount + callCount + grainPool + 20 + roomCount * msgsPerRoom)
    printfn " All across 2 silos + Redis"
    printfn "========================================"

    do! host.StopAsync()
}

stress().GetAwaiter().GetResult()
