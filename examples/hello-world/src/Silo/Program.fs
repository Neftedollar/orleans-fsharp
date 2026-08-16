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

// Functional-runtime equivalent of the grain above -- see CounterGrainFunctional.fs.
builder.UseOrleans(fun siloBuilder ->
    siloBuilder.AddFunctionalGrain(CounterFunctionalDef.counter) |> ignore)
|> ignore

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

        printfn ""
        printfn "--- Functional Grain Runtime equivalent (same counter domain) ---"
        let counterFn = CounterApi.ref factory "my-counter-functional"

        for i in 1..5 do
            let! result = counterFn.increment ()
            printfn "Increment #%d -> count = %d" i result

        let! finalValue = counterFn.value ()
        printfn "Final count (functional): %d" finalValue

        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
