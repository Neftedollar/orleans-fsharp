open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open MyApp.Grains

/// <summary>
/// Configure the Orleans silo using the siloConfig { } CE.
/// </summary>
let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder

// Register grain definitions with the DI container
builder.Services.AddFSharpGrain<CounterState, CounterCommand>(CounterGrainDef.counter)
|> ignore

let host = builder.Build()

/// <summary>
/// Run the sample silo, make a few grain calls, then exit cleanly.
/// </summary>
let runSample () : Task =
    task {
        do! host.StartAsync()

        let factory =
            host.Services.GetRequiredService<Orleans.IGrainFactory>()

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

        printfn ""
        printfn "Sample complete. Shutting down..."
        do! host.StopAsync()
    }

runSample().GetAwaiter().GetResult()
