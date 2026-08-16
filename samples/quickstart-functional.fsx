// Orleans.FSharp Quick Start Script -- functional grain runtime
// Run with: dotnet fsi quickstart-functional.fsx
//
// quickstart.fsx (beside this file) hosts its silo through Scripting.startOnPorts, a fixed
// helper with no configuration hook -- there is no way to hand it a functional grain definition
// to register (AddFunctionalGrain needs a silo builder). This script hosts its own silo instead,
// with the same siloConfig { } / SiloConfig.applyToHost recipe
// src/Orleans.FSharp.Sample/Program.fs uses, so AddFunctionalGrain has a builder to call. See
// docs/functional-grains.md for the full authoring model this contract/definition pair uses.
#r "nuget: Orleans.FSharp"
#r "nuget: Orleans.FSharp.Runtime"

open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp
open Orleans.FSharp.Runtime

// The API record: a plain F# record of functions -- no interface to write, no CodeGen bridge
// assembly needed (unlike quickstart.fsx's IHelloGrain; see docs/functional-grains.md, "Running
// a silo from a standalone F# process").
type HelloActor = private HelloActor of unit

[<NoEquality; NoComparison>]
type HelloApi = { sayHello: string -> Task<string> }

[<RequireQualifiedAccess>]
module HelloApi =
    let contract =
        grainContract<HelloActor, int64, HelloApi> () {
            grainType "quickstart.hello.functional"
            version 1
            int64Key
        }

    let ref = FunctionalGrain.ref contract

// The grain's own state is the number of times it has been called; each reply reports it back.
let helloDefinition =
    grainFor HelloApi.contract {
        defaultState (fun () -> 0)

        handle
            (_.sayHello)
            (fun _context callCount name -> task { return callCount + 1, $"Hello, {name}! (call #{callCount + 1})" })
    }

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder
builder.UseOrleans(fun siloBuilder -> siloBuilder.AddFunctionalGrain(helloDefinition) |> ignore) |> ignore
let host = builder.Build()

let run () =
    task {
        do! host.StartAsync()
        printfn "Silo started! GrainFactory ready."

        let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()
        let hello = HelloApi.ref factory 0L

        // Unlike quickstart.fsx's IHelloGrain, this reference is fully callable: the functional
        // transport's proxies ship pre-generated in the C# Orleans.FSharp.Abstractions package,
        // so no per-project bridge assembly is needed.
        let! greeting = hello.sayHello "World"
        printfn "%s" greeting

        let! greeting2 = hello.sayHello "World"
        printfn "%s" greeting2

        do! host.StopAsync()
        printfn "Silo stopped."
    }

run().GetAwaiter().GetResult()
