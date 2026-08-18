// Orleans.FSharp Quick Start Script -- functional grain runtime
// Run with: dotnet fsi quickstart-functional.fsx
//
// quickstart.fsx (beside this file) hosts its silo through Scripting.startOnPorts, which has no
// hook to hand it a functional grain definition (AddFunctionalGrain needs a silo builder).
// Orleans.FSharp.Runtime's FunctionalScripting.startOnPorts closes that gap (spec 004 item 8b):
// it reuses Scripting's own host-building core plus the standalone-host manifest pre-load, and
// takes the functional grain definitions to host, boxed with FunctionalGrainRegistration.of'.
// See docs/functional-grains.md for the full authoring model this contract/definition pair uses.
#r "nuget: Orleans.FSharp"
#r "nuget: Orleans.FSharp.Runtime"

open System.Threading.Tasks
open Orleans.FSharp

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

let run () =
    task {
        let! handle =
            FunctionalScripting.startOnPorts 11511 30001 [ FunctionalGrainRegistration.of' helloDefinition ]

        printfn "Silo started! GrainFactory ready."

        let hello = HelloApi.ref handle.GrainFactory 0L

        // Unlike quickstart.fsx's IHelloGrain, this reference is fully callable: the functional
        // transport's proxies ship pre-generated in the C# Orleans.FSharp.Abstractions package,
        // so no per-project bridge assembly is needed.
        let! greeting = hello.sayHello "World"
        printfn "%s" greeting

        let! greeting2 = hello.sayHello "World"
        printfn "%s" greeting2

        do! Scripting.shutdown handle
        printfn "Silo stopped."
    }

run().GetAwaiter().GetResult()
