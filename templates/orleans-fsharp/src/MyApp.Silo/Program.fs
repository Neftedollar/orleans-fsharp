open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open MyApp.Grains

/// <summary>Configure the Orleans silo.</summary>
let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder

builder.UseOrleans(fun siloBuilder ->
    siloBuilder.AddFunctionalGrain(CounterGrain.definition) |> ignore)
|> ignore

let host = builder.Build()

/// <summary>Run the sample silo, make a few typed grain calls, then exit cleanly.</summary>
let runSample () : Task =
    task {
        do! host.StartAsync()

        let factory =
            host.Services.GetRequiredService<Orleans.IGrainFactory>()

        let counter = CounterApi.ref factory 1L
        printfn "--- Functional Counter Grain Demo ---"

        let! first = counter.increment ()
        printfn "After increment: %d" first

        let! second = counter.increment ()
        printfn "After increment: %d" second

        let! current = counter.value ()
        printfn "Current value: %d" current

        let! decremented = counter.decrement ()
        printfn "After decrement: %d" decremented

        printfn "Sample complete. Shutting down..."
        do! host.StopAsync()
    }

runSample().GetAwaiter().GetResult()
