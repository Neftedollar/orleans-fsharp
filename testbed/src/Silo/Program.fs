open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans.Configuration
open Orleans.Hosting
open Serilog
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Testbed.Shared

// No CodeGen needed — FSharpGrain dispatcher + JSON fallback handles everything

let envRedisConn =
    Environment.GetEnvironmentVariable("REDIS_CONNECTION")
    |> Option.ofObj
    |> Option.defaultValue "localhost:6379"

let envSiloName =
    Environment.GetEnvironmentVariable("SILO_NAME")
    |> Option.ofObj
    |> Option.defaultValue "silo1"

let envSiloPort =
    Environment.GetEnvironmentVariable("SILO_PORT")
    |> Option.ofObj
    |> Option.map int
    |> Option.defaultValue 11111

let envGatewayPort =
    Environment.GetEnvironmentVariable("GATEWAY_PORT")
    |> Option.ofObj
    |> Option.map int
    |> Option.defaultValue 30000

let envAdvertisedIp =
    Environment.GetEnvironmentVariable("ADVERTISED_IP")
    |> Option.ofObj
    |> Option.defaultValue "localhost"

// Define grains
let counterGrain =
    grain {
        defaultState { Count = 0 }

        handle (fun state cmd ->
            task {
                match cmd with
                | Increment ->
                    let newCount = state.Count + 1
                    return { Count = newCount }, box newCount
                | Decrement ->
                    let newCount = max 0 (state.Count - 1)
                    return { Count = newCount }, box newCount
                | GetValue ->
                    return state, box state.Count
                | GetSiloInfo ->
                    return state, box $"{envSiloName}:{envSiloPort}"
            })

        persist "Default"
    }

let chatGrain =
    grain {
        defaultState { Messages = [] }

        handle (fun state cmd ->
            task {
                match cmd with
                | Send msg ->
                    let newState =
                        { Messages = msg :: state.Messages |> List.truncate 100 }

                    return newState, box true
                | GetHistory ->
                    return state, box state.Messages
                | GetMessageCount ->
                    return state, box state.Messages.Length
            })

        persist "Default"
    }

// Build the host directly, calling Redis extension methods explicitly
// rather than through the CE's reflection (which has overload-matching issues).
let builder = Host.CreateApplicationBuilder()

builder.UseOrleans(fun (siloBuilder: ISiloBuilder) ->
    // Cluster identity
    siloBuilder.Services.Configure<ClusterOptions>(Action<ClusterOptions>(fun opts ->
        opts.ClusterId <- "testbed-cluster"
        opts.ServiceId <- "testbed-service"))
    |> ignore

    // Silo identity
    siloBuilder.Services.Configure<SiloOptions>(Action<SiloOptions>(fun opts ->
        opts.SiloName <- envSiloName))
    |> ignore

    // Endpoints
    siloBuilder.Services.Configure<EndpointOptions>(Action<EndpointOptions>(fun opts ->
        opts.SiloPort <- envSiloPort
        opts.GatewayPort <- envGatewayPort
        // Resolve hostname to IP (Docker uses hostnames like "silo1", not numeric IPs)
        let ip =
            match System.Net.IPAddress.TryParse(envAdvertisedIp) with
            | true, addr -> addr
            | false, _ ->
                let addresses = System.Net.Dns.GetHostAddresses(envAdvertisedIp)
                addresses |> Array.find (fun a -> a.AddressFamily = System.Net.Sockets.AddressFamily.InterNetwork)

        opts.AdvertisedIPAddress <- ip))
    |> ignore

    // Redis clustering — call the (ISiloBuilder, string) overload directly
    siloBuilder.UseRedisClustering(envRedisConn) |> ignore

    // Redis grain storage — use ConfigurationOptions property
    siloBuilder.AddRedisGrainStorage(
        "Default",
        fun (opts: Orleans.Persistence.RedisStorageOptions) ->
            opts.ConfigurationOptions <- StackExchange.Redis.ConfigurationOptions.Parse(envRedisConn))
    |> ignore

    // Memory streams for pub/sub testing
    siloBuilder.AddMemoryStreams("StreamProvider") |> ignore
    siloBuilder.AddMemoryGrainStorage("PubSubStore") |> ignore

    // F# JSON fallback serialization (handles DU, record, option, list without attributes)
    Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
        siloBuilder.Services,
        Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
            Orleans.Serialization.SerializationHostingExtensions.AddJsonSerializer(
                serializerBuilder,
                isSupported = Func<Type, bool>(fun _ -> true),
                jsonSerializerOptions = Orleans.FSharp.FSharpJson.serializerOptions)
            |> ignore))
    |> ignore)
|> ignore

// Serilog
builder.Services.AddLogging(fun (loggingBuilder: ILoggingBuilder) ->
    loggingBuilder.AddSerilog() |> ignore)
|> ignore

builder.Services.AddFSharpGrain<CounterState, CounterCommand>(counterGrain) |> ignore
builder.Services.AddFSharpGrain<ChatState, ChatCommand>(chatGrain) |> ignore

printfn "Starting %s on port %d (gateway %d)..." envSiloName envSiloPort envGatewayPort
printfn "Redis: %s | Advertised IP: %s" envRedisConn envAdvertisedIp
let host = builder.Build()
host.Run()
