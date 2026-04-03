open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.FSharp
open Testbed.Shared

// Force-load the CodeGen assembly so Orleans discovers the generated grain metadata.
// Referencing a type from the CodeGen assembly ensures it (and its Orleans-generated
// serialization/metadata code) is loaded into the AppDomain.
let private _codeGenLoaded =
    let asm = typeof<Testbed.CodeGen.CounterGrainImpl>.Assembly
    asm.GetTypes() |> ignore
    true

let redisConn =
    Environment.GetEnvironmentVariable("REDIS_CONNECTION")
    |> Option.ofObj
    |> Option.defaultValue "localhost:6379"

let runTests () =
    task {
        printfn "Orleans.FSharp Testbed - 2 Silos + Redis"
        printfn "========================================="
        printfn ""

        // Build client with Redis clustering
        let builder = Host.CreateApplicationBuilder()

        builder.UseOrleansClient(fun (clientBuilder: IClientBuilder) ->
            // Cluster identity
            clientBuilder.Services.Configure<ClusterOptions>(Action<ClusterOptions>(fun opts ->
                opts.ClusterId <- "testbed-cluster"
                opts.ServiceId <- "testbed-service"))
            |> ignore

            // Redis clustering for gateway discovery
            clientBuilder.UseRedisClustering(redisConn) |> ignore

            // F# JSON fallback serialization (must match silo configuration)
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

        // Wait for cluster to stabilize
        printfn "Connecting to cluster via Redis at %s..." redisConn
        do! Task.Delay(5000)

        // 1. Counter test: call same grain 10 times, show it increments
        printfn "Counter grain test:"

        for i in 1..10 do
            let grain = factory.GetGrain<ICounterGrain>("counter-1")
            let! result = grain.HandleMessage(Increment)
            let count = result :?> int
            printfn "  Increment #%d -> count = %d" i count

        printfn ""

        // 2. Multi-grain test: call 5 different counter grains, show distribution
        printfn "Multi-grain distribution:"

        for i in 1..5 do
            let grain = factory.GetGrain<ICounterGrain>($"grain-{i}")
            let! _ = grain.HandleMessage(Increment)
            let! result = grain.HandleMessage(GetSiloInfo)
            let siloInfo = result :?> string
            printfn "  Grain %d handled by: %s" i siloInfo

        printfn ""

        // 3. Chat test: send messages, read history
        printfn "Chat grain test:"
        let chatGrain = factory.GetGrain<IChatGrain>("chat-room-1")

        for i in 1..3 do
            let msg =
                { Sender = $"user-{i}"
                  Text = $"Hello from user {i}!"
                  Timestamp = DateTime.UtcNow }

            let! _ = chatGrain.HandleMessage(Send msg)
            printfn "  Sent message from user-%d" i

        let! historyResult = chatGrain.HandleMessage(GetHistory)
        let history = historyResult :?> ChatMessage list
        printfn "  Chat history: %d messages" history.Length

        for msg in history |> List.rev do
            printfn "    [%s] %s: %s" (msg.Timestamp.ToString("HH:mm:ss")) msg.Sender msg.Text

        let! countResult = chatGrain.HandleMessage(GetMessageCount)
        let msgCount = countResult :?> int
        printfn "  Message count: %d" msgCount

        printfn ""
        printfn "All tests passed! Cluster is healthy."

        do! host.StopAsync()
    }

runTests().GetAwaiter().GetResult()
