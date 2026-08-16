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

(*
    Classic grain { } model -- cannot run standalone.

    F# assemblies carry none of Orleans' source-generated
    [assembly: ApplicationPart] / [assembly: TypeManifestProvider] attributes (Roslyn generators
    never run on an F# project), so a bare `factory.GetGrain<ICounterGrain>(...)` fails with
    "Could not find an implementation for interface ICounterGrain" the moment it runs. Historically
    this example closed the gap with a C# CodeGen bridge project; that project was removed
    (commit 4d10d5d) once the functional runtime made it unnecessary. The call sequence below is
    kept as reference -- see docs/functional-grains.md, "Running a silo from a standalone F#
    process" for the exact mechanism, and "Migrating from the grain { } CE" for the rewrite this
    file demonstrates.

    let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()
    let counterRef = GrainRef.ofString<ICounterGrain> factory "my-counter"

    printfn "--- Hello World: Counter Grain ---"

    for i in 1..5 do
        let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Increment))
        printfn "Increment #%d -> count = %A" i result

    let! value = GrainRef.invoke counterRef (fun g -> g.HandleMessage(GetValue))
    printfn "Final count: %A" value
*)

let run () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()

        printfn "--- Hello World: Counter Grain (Functional Grain Runtime) ---"
        let counterFn = CounterApi.ref factory "my-counter-functional"

        for i in 1..5 do
            let! result = counterFn.increment ()
            printfn "Increment #%d -> count = %d" i result

        let! finalValue = counterFn.value ()
        printfn "Final count: %d" finalValue

        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
