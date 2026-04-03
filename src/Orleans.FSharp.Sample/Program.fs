open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.FSharp.Sample

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder

// Register grain definitions with the DI container
builder.Services.AddFSharpGrain<CounterState, CounterCommand>(CounterGrainDef.counter) |> ignore
builder.Services.AddFSharpGrain<OrderStatus, OrderCommand>(OrderGrainDef.order) |> ignore
builder.Services.AddFSharpGrain<string, EchoCommand>(EchoGrainDef.echo) |> ignore

let host = builder.Build()

/// Run the sample silo, make a few grain calls, then exit cleanly.
let runSample () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()

        // Counter grain demo
        let counterRef = GrainRef.ofInt64<ICounterGrain> factory 1L
        printfn "--- Counter Grain Demo ---"

        let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Increment))
        printfn "After Increment: %A" result

        let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Increment))
        printfn "After Increment: %A" result

        let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(GetValue))
        printfn "Current value: %A" result

        let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Decrement))
        printfn "After Decrement: %A" result

        // Echo grain demo
        let echoRef = GrainRef.ofString<IEchoGrain> factory "world"
        printfn ""
        printfn "--- Echo Grain Demo ---"

        let! result = GrainRef.invoke echoRef (fun g -> g.HandleMessage(Echo "hello"))
        printfn "Echo: %A" result

        let! result = GrainRef.invoke echoRef (fun g -> g.HandleMessage(Greet))
        printfn "Greet: %A" result

        // Order grain demo
        let orderRef = GrainRef.ofString<IOrderGrain> factory "order-001"
        printfn ""
        printfn "--- Order Grain Demo ---"

        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(Place "Widget x10"))
        printfn "Place: %A" result

        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(Confirm))
        printfn "Confirm: %A" result

        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(GetStatus))
        printfn "Status: %A" result

        printfn ""
        printfn "Sample complete. Shutting down..."
        do! host.StopAsync()
    }

runSample().GetAwaiter().GetResult()
