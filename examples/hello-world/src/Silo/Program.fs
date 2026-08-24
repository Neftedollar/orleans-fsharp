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
        useFSharpJsonSerialization
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder
builder.Services.AddFSharpGrain<CounterState, CounterCommand>(CounterGrainDef.counter) |> ignore

// Functional-runtime equivalent of the grain above -- see CounterGrainFunctional.fs.
builder.UseOrleans(fun siloBuilder ->
    siloBuilder.AddFunctionalGrain(CounterFunctionalDef.counter) |> ignore)
|> ignore

let host = builder.Build()

type CounterStateV1 = { Count: int }

type CounterStateV2 =
    { Count: int
      Label: string }

let counterMigrations =
    [ StateMigration.migration<CounterStateV1, CounterStateV2> 1 2 (fun oldState ->
          { Count = oldState.Count
            Label = "migrated counter" }) ]

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

        // GrainBatch is for a dynamic collection of references. Calls run concurrently and the
        // returned list keeps the same order as this input collection.
        let batchCounters =
            [ 1..3 ]
            |> List.map (fun index -> CounterApi.ref factory $"batch-counter-{index}")

        let! batchValues = GrainBatch.map batchCounters (fun counter -> counter.increment ())

        let! batchTotal =
            GrainBatch.aggregate batchCounters (fun counter -> counter.value ()) List.sum

        printfn "GrainBatch increments: %A (aggregate = %d)" batchValues batchTotal

        // StateMigration is a pure schema-upgrade core. Storage code supplies the persisted
        // version and value; the utility validates and applies the chain before the new state is
        // handed back to the actor boundary.
        match StateMigration.tryApplyMigrations<CounterStateV2> counterMigrations 1 (box { Count = 5 }) with
        | Ok migrated ->
            printfn "State migration v1 -> v2: count = %d, label = %s" migrated.Count migrated.Label
        | Error errors ->
            printfn "State migration was rejected: %s" (String.concat "; " errors)

        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
