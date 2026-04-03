// Orleans.FSharp Quick Start Script
// Run with: dotnet fsi quickstart.fsx
#r "nuget: Orleans.FSharp"
#r "nuget: Orleans.FSharp.Runtime"

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

// Define a simple grain interface
type IHelloGrain =
    inherit IGrainWithIntegerKey
    abstract SayHello: name: string -> Task<string>

// Start an in-process silo
let handle = Scripting.quickStart().GetAwaiter().GetResult()
let grain = Scripting.getGrain<IHelloGrain> handle 0L
printfn "Silo started! GrainFactory ready."

// Clean up
Scripting.shutdown(handle).GetAwaiter().GetResult()
printfn "Silo stopped."
