open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open HelloWorld.Grains

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        useJsonFallbackSerialization
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder
builder.Services.AddFSharpGrain<CounterState, CounterCommand>(CounterGrainDef.counter) |> ignore

let host = builder.Build()

let run () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()
        let counterRef = GrainRef.ofString<ICounterGrain> factory "my-counter"

        printfn "--- Hello World: Counter Grain ---"

        for i in 1..5 do
            let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Increment))
            printfn "Increment #%d -> count = %A" i result

        let! value = GrainRef.invoke counterRef (fun g -> g.HandleMessage(GetValue))
        printfn "Final count: %A" value

        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
